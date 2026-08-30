using System.Buffers.Binary;
using System.Text;
using BIS.Core.Streams;
using BIS.PAA;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>
    /// Repackages an Arma <c>.paa</c> as a <c>.dds</c> without recompressing anything.
    /// </summary>
    /// <remarks>
    /// A paa already stores DXT compressed blocks, the very same layout a dds carries, so the
    /// conversion is a header swap. That matters: the lossless PNG expansion of a map's textures
    /// runs to hundreds of megabytes, while the dds copies stay close to the size of the source
    /// paa files, and DXT is what the engines want to upload to the GPU anyway.
    /// </remarks>
    public static class PaaToDdsConverter
    {
        private const uint DDSD_CAPS = 0x1;
        private const uint DDSD_HEIGHT = 0x2;
        private const uint DDSD_WIDTH = 0x4;
        private const uint DDSD_PIXELFORMAT = 0x1000;
        private const uint DDSD_MIPMAPCOUNT = 0x20000;
        private const uint DDSD_LINEARSIZE = 0x80000;

        private const uint DDPF_FOURCC = 0x4;

        private const uint DDSCAPS_COMPLEX = 0x8;
        private const uint DDSCAPS_MIPMAP = 0x400000;
        private const uint DDSCAPS_TEXTURE = 0x1000;

        /// <summary>True when the paa holds block compressed data this converter can repackage.</summary>
        public static bool IsBlockCompressed(PAAType type)
        {
            return type is PAAType.DXT1 or PAAType.DXT2 or PAAType.DXT3 or PAAType.DXT4 or PAAType.DXT5;
        }

        /// <summary>
        /// Writes <paramref name="paaStream"/> as a dds file. Returns false when the paa is not
        /// block compressed, in which case the caller should fall back to another format.
        /// </summary>
        public static bool TryConvert(Stream paaStream, string targetPath)
        {
            paaStream.Position = 0;
            var reader = new BinaryReaderEx(paaStream);
            var paa = new PAA(reader);
            if (!IsBlockCompressed(paa.Type))
            {
                return false;
            }

            // Largest first: a dds mip chain has to descend, and DXT blocks are 4x4 so anything
            // smaller than a block is left out rather than risking a malformed tail.
            var mipmaps = paa.Mipmaps
                .OrderByDescending(m => m.Width)
                .Where(m => m.Width >= 4 && m.Height >= 4)
                .ToList();
            if (mipmaps.Count == 0)
            {
                return false;
            }

            var levels = new List<byte[]>(mipmaps.Count);
            foreach (var mipmap in mipmaps)
            {
                levels.Add(mipmap.GetRawPixelData(reader, paa.Type));
            }

            var main = mipmaps[0];
            using var output = File.Create(targetPath);
            output.Write(BuildHeader(main.Width, main.Height, levels[0].Length, levels.Count, paa.Type));
            foreach (var level in levels)
            {
                output.Write(level);
            }
            return true;
        }

        private static byte[] BuildHeader(int width, int height, int mainSize, int mipmapCount, PAAType type)
        {
            var header = new byte[128];
            var span = header.AsSpan();

            Encoding.ASCII.GetBytes("DDS ").CopyTo(span);
            var flags = DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_LINEARSIZE;
            var caps = DDSCAPS_TEXTURE;
            if (mipmapCount > 1)
            {
                flags |= DDSD_MIPMAPCOUNT;
                caps |= DDSCAPS_COMPLEX | DDSCAPS_MIPMAP;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4), 124);          // dwSize
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(8), flags);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12), (uint)height);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16), (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(20), (uint)mainSize); // dwPitchOrLinearSize
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24), 0);           // dwDepth
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28), (uint)mipmapCount);

            // DDS_PIXELFORMAT starts at offset 76
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(76), 32);          // dwSize
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(80), DDPF_FOURCC);
            Encoding.ASCII.GetBytes(GetFourCC(type)).CopyTo(span.Slice(84));

            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(108), caps);
            return header;
        }

        private static string GetFourCC(PAAType type) => type switch
        {
            PAAType.DXT1 => "DXT1",
            PAAType.DXT2 => "DXT2",
            PAAType.DXT3 => "DXT3",
            PAAType.DXT4 => "DXT4",
            PAAType.DXT5 => "DXT5",
            _ => throw new NotSupportedException($"{type} is not block compressed.")
        };
    }
}
