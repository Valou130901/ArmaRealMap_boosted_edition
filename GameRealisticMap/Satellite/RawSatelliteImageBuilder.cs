using System.Numerics;
using GameRealisticMap.Configuration;
using GameRealisticMap.Geometries;
using GameRealisticMap.IO;
using Pmad.HugeImages;
using Pmad.ProgressTracking;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GameRealisticMap.Satellite
{
    internal class RawSatelliteImageBuilder : IDataBuilderAsync<RawSatelliteImageData>, IDataSerializer<RawSatelliteImageData>
    {
        private readonly ISourceLocations sources;

        public RawSatelliteImageBuilder(ISourceLocations sources)
        {
            this.sources = sources;
        }

        public async Task<RawSatelliteImageData> BuildAsync(IBuildContext context, IProgressScope scope)
        {
            //Image<Rgb24> image;

            var totalSize = (int)Math.Ceiling(context.Area.SizeInMeters / context.Options.Resolution);

            //using (var report = progress.CreateStep("S2C OLD", totalSize /*tileSize * tileCount * tileCount*/))
            //{
            //    image = LoadImage(context, totalSize, report, Vector2.Zero, 0);
            //    image.SaveAsPng(@"c:\temp\test.png");
            //}

            var himage = new HugeImage<Rgba32>(context.HugeImageStorage, nameof(RawSatelliteImageBuilder), new Size(totalSize));
            var oceanData = context.GetData<GameRealisticMap.Nature.Ocean.OceanData>();
            var elevationData = context.GetData<GameRealisticMap.ElevationModel.ElevationData>();
            using (var report2 = scope.CreateInteger(SatelliteImageProvider.GetName(sources), himage.Parts.Sum(t => t.RealRectangle.Height)))
            {
                using var src = new SatelliteImageProvider(report2, sources);
                foreach (var part in himage.Parts)
                {
                    await LoadPart(context, totalSize, part, report2, src, oceanData, elevationData).ConfigureAwait(false);
                    await himage.OffloadAsync().ConfigureAwait(false);
                }
            }

            return new RawSatelliteImageData(himage);
        }

        private async Task LoadPart(IBuildContext context, int totalSize, HugeImagePart<Rgba32> part, IProgressInteger report, SatelliteImageProvider src, GameRealisticMap.Nature.Ocean.OceanData oceanData, GameRealisticMap.ElevationModel.ElevationData elevationData)
        {
            var imageryResolution = context.Options.Resolution;
            var options = context.Options.Satellite;

            using var token = await part.AcquireAsync().ConfigureAwait(false);

            var img = token.GetImageReadWrite();

            var parallel = 16;
            var dh = part.RealRectangle.Height / parallel;

            await Parallel.ForEachAsync(Enumerable.Range(0, parallel), async (dy, _) =>
            {
                var stX = part.RealRectangle.X;
                var stY = part.RealRectangle.Y;
                var endX = part.RealRectangle.Right;
                var y1 = part.RealRectangle.Y + (dy * dh);
                var y2 = dy == parallel - 1 ? part.RealRectangle.Bottom : part.RealRectangle.Y + ((dy + 1) * dh);
                for (int ry = y1; ry < y2; ry++)
                {
                    for (int rx = part.RealRectangle.X; rx < endX; rx++)
                    {
                        var latLong = context.Area.TerrainPointToLatLng(new TerrainPoint((float)(rx * imageryResolution), (float)((totalSize - ry - 1) * imageryResolution)));
                        img[rx - stX, ry - stY] = await src.GetPixel(latLong).ConfigureAwait(false);
                    }
                    report.ReportOneDone();
                }
            }).ConfigureAwait(false);

            img.Mutate(d => d.GaussianBlur(1f));

            if (oceanData != null && oceanData.IsIsland && oceanData.Land.Count > 0)
            {
                var mask = new Image<L8>(part.RealRectangle.Width, part.RealRectangle.Height);
                mask.Mutate(m =>
                {
                    m.Fill(Color.Black);
                    foreach (var poly in oceanData.Land)
                    {
                        var pts = poly.Shell.Select(p => new PointF(
                            (float)(p.X / imageryResolution) - part.RealRectangle.X,
                            totalSize - 1 - (float)(p.Y / imageryResolution) - part.RealRectangle.Y
                        )).ToArray();
                        m.FillPolygon(Color.White, pts);
                        
                        foreach (var hole in poly.Holes)
                        {
                            var hpts = hole.Select(p => new PointF(
                                (float)(p.X / imageryResolution) - part.RealRectangle.X,
                                totalSize - 1 - (float)(p.Y / imageryResolution) - part.RealRectangle.Y
                            )).ToArray();
                            m.FillPolygon(Color.Black, hpts);
                        }
                    }
                    m.GaussianBlur(2f); // Smooth the edge
                });

                var oceanColor = new Rgba32(10, 28, 48); // Dark blue ocean water
                var shallowColor = new Rgba32(30, 80, 120); // Lighter blue for shallow water

                for (int y = 0; y < part.RealRectangle.Height; y++)
                {
                    for (int x = 0; x < part.RealRectangle.Width; x++)
                    {
                        var maskWeight = mask[x, y].PackedValue / 255f;
                        
                        var rx = x + part.RealRectangle.X;
                        var ry = y + part.RealRectangle.Y;
                        var terrainX = (float)(rx * imageryResolution);
                        var terrainY = (float)((totalSize - ry - 1) * imageryResolution);
                        var elevation = elevationData.Elevation.ElevationAt(new TerrainPoint(terrainX, terrainY));

                        var orig = img[x, y];
                        
                        Rgba32 fakeColor = new Rgba32(75, 95, 55, 255); // Default land color for outside the island
                        
                        if (elevation < 0.5f)
                        {
                            float depth = -elevation;
                            if (depth < 0f) depth = 0f;
                            
                            float depthWeight = (0.5f - elevation) / 5.5f; 
                            if (depthWeight > 1f) depthWeight = 1f;
                            
                            Rgba32 pureWaterColor;
                            if (depth < 2f)
                            {
                                float w = depth / 2f;
                                pureWaterColor = new Rgba32(
                                    (byte)(shallowColor.R * w + fakeColor.R * (1 - w)),
                                    (byte)(shallowColor.G * w + fakeColor.G * (1 - w)),
                                    (byte)(shallowColor.B * w + fakeColor.B * (1 - w)),
                                    255
                                );
                            }
                            else
                            {
                                float w = Math.Min(1f, (depth - 2f) / 18f);
                                pureWaterColor = new Rgba32(
                                    (byte)(oceanColor.R * w + shallowColor.R * (1 - w)),
                                    (byte)(oceanColor.G * w + shallowColor.G * (1 - w)),
                                    (byte)(oceanColor.B * w + shallowColor.B * (1 - w)),
                                    255
                                );
                            }

                            // Blend both real and fake land to water color based on depth
                            fakeColor = new Rgba32(
                                (byte)(fakeColor.R * (1f - depthWeight) + pureWaterColor.R * depthWeight),
                                (byte)(fakeColor.G * (1f - depthWeight) + pureWaterColor.G * depthWeight),
                                (byte)(fakeColor.B * (1f - depthWeight) + pureWaterColor.B * depthWeight),
                                255
                            );
                            
                            orig = new Rgba32(
                                (byte)(orig.R * (1f - depthWeight) + pureWaterColor.R * depthWeight),
                                (byte)(orig.G * (1f - depthWeight) + pureWaterColor.G * depthWeight),
                                (byte)(orig.B * (1f - depthWeight) + pureWaterColor.B * depthWeight),
                                255
                            );
                        }

                        // Handle transparent Swisstopo No Data tiles gracefully inside the island mask
                        float origAlpha = orig.A / 255f;
                        float realWeight = maskWeight * origAlpha;

                        // maskWeight is 1.0 INSIDE the island, 0.0 OUTSIDE.
                        img[x, y] = new Rgba32(
                            (byte)(orig.R * realWeight + fakeColor.R * (1f - realWeight)),
                            (byte)(orig.G * realWeight + fakeColor.G * (1f - realWeight)),
                            (byte)(orig.B * realWeight + fakeColor.B * (1f - realWeight)),
                            255
                        );
                    }
                }
            }

            if ( options.Brightness != 1f || options.Contrast != 1f || options.Saturation != 1f)
            {
                img.Mutate(d => d.Brightness(options.Brightness).Contrast(options.Contrast).Saturate(options.Saturation));
            }

        }

        public async ValueTask<RawSatelliteImageData> Read(IPackageReader package, IContext context)
        {
            //var image = await Image.LoadAsync<Rgb24>(package.ReadFile("RawSatellite.png"), new PngDecoder());
            //return new RawSatelliteImageData(image);
            throw new NotImplementedException();
        }

        public async Task Write(IPackageWriter package, RawSatelliteImageData data)
        {
            //using(var stream = package.CreateFile("RawSatellite.png"))
            //{
            //    await data.Image.SaveAsPngAsync(stream);
            //}
            throw new NotImplementedException();
        }

        private Image<Rgba32> LoadImage(IBuildContext context, int tileSize, IProgressInteger report, Vector2 start, int done)
        {
            var imageryResolution = context.Options.Resolution;
            using var src = new SatelliteImageProvider(report, sources);
            var img = new Image<Rgba32>(tileSize, tileSize);
            var parallel = 16;
            var dh = img.Height / parallel;
            Parallel.For(0, parallel, dy =>
            {
                var y1 = dy * dh;
                var y2 = (dy + 1) * dh;
                for (int y = y1; y < y2; y++)
                {
                    for (int x = 0; x < img.Width; x++)
                    {
                        var latLong = context.Area.TerrainPointToLatLng(new TerrainPoint((float)(x * imageryResolution), (float)(y * imageryResolution)) + start);
                        img[x, img.Height - y - 1] = src.GetPixel(latLong).Result;
                    }
                    report.Report(Interlocked.Increment(ref done));
                }
            });
            img.Mutate(d => d.GaussianBlur(1f));
            return img;
        }
    }
}
