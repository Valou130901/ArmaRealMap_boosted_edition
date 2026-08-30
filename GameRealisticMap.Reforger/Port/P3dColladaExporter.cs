using System.Globalization;
using System.Numerics;
using System.Text;
using BIS.Core.Math;
using BIS.P3D;
using BIS.P3D.ODOL;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>
    /// Writes the visual LODs of a binarized Arma 3 model as a textured COLLADA file, the mesh
    /// format Torque based engines such as BeamNG.drive load directly.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="P3dObjExporter"/>, which targets the Enfusion pipeline through Blender,
    /// this produces something a game can consume as-is: one triangle group per texture, with UVs
    /// and a material bound to the converted PNG.
    /// <para>
    /// Every visual LOD is exported, not just the finest one. Torque reads a shape's detail levels
    /// from node names of the form <c>name123</c>, where the number is the on-screen pixel size the
    /// mesh is meant for, so the whole Arma LOD chain maps straight onto it. This matters enormously
    /// for vegetation: a forest places hundreds of thousands of instances, and without detail levels
    /// the engine draws every distant tree at full density.
    /// </para>
    /// </remarks>
    public static class P3dColladaExporter
    {
        private const float FirstSpecialLodResolution = 1000f;

        /// <summary>
        /// Detail sizes handed to the successive LODs, finest first. Torque draws the largest detail
        /// whose size fits within the shape's current pixel size, so these act as switch distances,
        /// and the smallest also becomes the size below which the shape stops drawing at all. The
        /// values are the medians measured over the 3570 multi-detail shapes BeamNG ships: 550, 200,
        /// 100, 30, with the empty level at 10 and a ratio of about 2.3 between steps. Individual
        /// shapes scale these with their own size, which is why a large rock reaches 2200, but the
        /// ladder itself is what the game expects.
        /// <para>
        /// Five steps rather than the median four, at that same 2.3 ratio, because Arma usually
        /// supplies five or six visual LODs and throwing one away helps nobody: an extra rung costs
        /// only the memory of a mesh that was already authored, and lets the engine shed triangles
        /// gradually instead of in one visible jump.
        /// </para>
        /// </summary>
        private static readonly int[] DetailSizes = { 550, 240, 105, 45, 20 };

        /// <summary>
        /// Detail size of the generated billboard, the last thing drawn before the shape disappears.
        /// </summary>
        /// <remarks>
        /// Arma's coarsest LOD is still two to six hundred triangles, because Arma never has to draw
        /// three hundred thousand of them at once. BeamNG's own props end on a flat imposter and then
        /// on nothing, which is why they scale. Measured on Malden: 342 029 instances of a four
        /// triangle billboard run smoothly, the same instances of the real meshes do not.
        /// </remarks>
        private const int BillboardDetailSize = 12;

        /// <summary>
        /// Size of the empty <c>nulldetail</c> level: below this the shape draws nothing at all.
        /// Half of BeamNG's own shapes carry one, and without it the coarsest mesh keeps being drawn
        /// out to the horizon.
        /// </summary>
        private const int CullDetailSize = 10;

        /// <summary>
        /// Torque reads collision geometry from a node whose name starts with <c>col</c>; this is
        /// the spelling BeamNG's own shapes use.
        /// </summary>
        private const string CollisionNodeName = "Colmesh-1";

        /// <summary>
        /// Above this, a finest LOD is dropped in favour of the next one down. Arma carries a few
        /// showcase models whose top LOD runs to hundreds of thousands of faces; a terrain places
        /// them by the thousand, where they cost far more than the detail is worth.
        /// </summary>
        private const int MaxFinestFaceCount = 30_000;

        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        /// <summary>
        /// Exports <paramref name="odol"/> as a .dae next to its textures, or returns null when the
        /// model has no drawable geometry.
        /// </summary>
        /// <param name="resolveTexture">
        /// Maps an Arma texture path to a file name the dae should reference, or null for an
        /// untextured material.
        /// </param>
        public static P3dExportResult? Export(ODOL odol, string modelName, string targetDirectory,
            Func<string, string?>? resolveTexture = null)
        {
            var lods = SelectVisualLods(odol);
            if (lods.Count == 0)
            {
                return null;
            }
            while (lods.Count > 1 && lods[0].FaceCount > MaxFinestFaceCount)
            {
                lods.RemoveAt(0);
            }

            Directory.CreateDirectory(targetDirectory);
            var baseName = P3dObjExporter.SanitizeName(modelName);
            var daePath = Path.Combine(targetDirectory, baseName + ".dae");

            var details = new List<DetailLevel>();
            var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lod in lods)
            {
                var detail = BuildDetail(lod, baseName + DetailSizes[details.Count].ToString(Invariant), resolveTexture);
                if (detail == null)
                {
                    continue;
                }
                details.Add(detail);
                foreach (var group in detail.Groups.Where(g => !string.IsNullOrEmpty(g.ArmaTexture)))
                {
                    textures.Add(group.ArmaTexture!);
                }
                if (details.Count == DetailSizes.Length)
                {
                    break;
                }
            }
            if (details.Count == 0)
            {
                return null;
            }

            var billboard = BuildBillboard(baseName, details[^1]);
            if (billboard != null)
            {
                details.Add(billboard);
            }

            // Without a collision node Torque has to fall back on the finest visual mesh, which for
            // a map holding tens of thousands of buildings means tens of thousands of full density
            // collision hulls. Arma already ships a low poly geometry LOD meant for exactly this.
            var collision = BuildCollision(odol, lods);
            if (collision != null)
            {
                details.Add(collision);
            }

            var finest = details[0];
            var result = new P3dExportResult(modelName, daePath, lods[0].Resolution, finest.VertexCount, lods[0].FaceCount);
            foreach (var texture in textures)
            {
                result.Textures.Add(texture);
            }

            File.WriteAllText(daePath, BuildDocument(baseName, details), new UTF8Encoding(false));

            return result;
        }

        /// <summary>Geometry of one LOD, ready to be written as a Torque detail level.</summary>
        private sealed class DetailLevel
        {
            public string Name { get; set; } = string.Empty;

            public int VertexCount { get; set; }

            public int NormalCount { get; set; }

            public int UvCount { get; set; }

            public string Positions { get; set; } = string.Empty;

            public string Normals { get; set; } = string.Empty;

            public string Uvs { get; set; } = string.Empty;

            public List<TriangleGroup> Groups { get; set; } = new();

            /// <summary>Bounds in Torque space, X east, Y north, Z up. Used to size the billboard.</summary>
            public Vector3 Min { get; set; }

            public Vector3 Max { get; set; }
        }

        /// <summary>
        /// Two crossed, double sided quads carrying the model's dominant texture, sized to its
        /// bounding box: what the shape collapses to before it stops being drawn at all.
        /// </summary>
        private static DetailLevel? BuildBillboard(string baseName, DetailLevel coarsest)
        {
            // Textured surfaces only. An untextured model is a flat coloured thing whose billboard
            // would read as a coloured rectangle hanging in the air.
            var dominant = coarsest.Groups
                .Where(g => !string.IsNullOrEmpty(g.Texture))
                .OrderByDescending(g => g.TriangleCount)
                .FirstOrDefault();
            if (dominant == null)
            {
                return null;
            }

            var size = coarsest.Max - coarsest.Min;
            var halfWidth = MathF.Max(size.X, size.Y) / 2f;
            if (halfWidth < 0.05f || size.Z < 0.05f)
            {
                return null;
            }
            var cx = (coarsest.Min.X + coarsest.Max.X) / 2f;
            var cy = (coarsest.Min.Y + coarsest.Max.Y) / 2f;
            var z0 = coarsest.Min.Z;
            var z1 = coarsest.Max.Z;

            var corners = new[]
            {
                new Vector3(cx - halfWidth, cy, z0), new Vector3(cx + halfWidth, cy, z0),
                new Vector3(cx + halfWidth, cy, z1), new Vector3(cx - halfWidth, cy, z1),
                new Vector3(cx, cy - halfWidth, z0), new Vector3(cx, cy + halfWidth, z0),
                new Vector3(cx, cy + halfWidth, z1), new Vector3(cx, cy - halfWidth, z1),
            };

            var positions = new StringBuilder();
            var normals = new StringBuilder();
            var uvs = new StringBuilder();
            var uvCorners = new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) };
            for (var i = 0; i < corners.Length; i++)
            {
                positions.Append(corners[i].X.ToString("0.####", Invariant)).Append(' ')
                         .Append(corners[i].Y.ToString("0.####", Invariant)).Append(' ')
                         .Append(corners[i].Z.ToString("0.####", Invariant)).Append(' ');
                // Straight up, the usual trick for foliage cards: it keeps them evenly lit instead
                // of flipping between bright and black as the camera goes round them.
                normals.Append("0 0 1 ");
                var (u, v) = uvCorners[i % 4];
                uvs.Append(u.ToString("0.###", Invariant)).Append(' ')
                   .Append(v.ToString("0.###", Invariant)).Append(' ');
            }

            var group = new TriangleGroup
            {
                Material = dominant.Material,
                Texture = dominant.Texture,
                ArmaTexture = dominant.ArmaTexture,
                Colour = dominant.Colour,
            };
            foreach (var quad in new[] { 0, 4 })
            {
                foreach (var (a, b, c) in new[] { (0, 1, 2), (0, 2, 3) })
                {
                    // Both windings, so a card stays visible whichever side it is seen from
                    AppendVertex(group.Indices, quad + a, true, true);
                    AppendVertex(group.Indices, quad + b, true, true);
                    AppendVertex(group.Indices, quad + c, true, true);
                    AppendVertex(group.Indices, quad + c, true, true);
                    AppendVertex(group.Indices, quad + b, true, true);
                    AppendVertex(group.Indices, quad + a, true, true);
                    group.TriangleCount += 2;
                }
            }

            return new DetailLevel
            {
                Name = baseName + BillboardDetailSize.ToString(Invariant),
                VertexCount = corners.Length,
                NormalCount = corners.Length,
                UvCount = corners.Length,
                Positions = positions.ToString(),
                Normals = normals.ToString(),
                Uvs = uvs.ToString(),
                Groups = new List<TriangleGroup> { group },
            };
        }

        /// <summary>
        /// Picks the mesh Torque should collide against: Arma's dedicated geometry LOD when the
        /// model has one, otherwise the coarsest visual LOD, which is still far cheaper than the
        /// finest one Torque would use on its own.
        /// </summary>
        private static DetailLevel? BuildCollision(ODOL odol, List<LOD> visualLods)
        {
            // Roadway first. It is the surface Arma actually drives on, and on a bridge it is the
            // deck: the geometry LOD holds the structure and the railings but leaves the carriageway
            // open, so a car built to collide against geometry alone falls straight through.
            var roadway = odol.Lods.FirstOrDefault(
                lod => lod.Resolution == Resolution.ROADWAY && lod.Vertices.Count > 0);
            var geometry = odol.Lods.FirstOrDefault(
                lod => lod.Resolution == Resolution.GEOMETRY && lod.Vertices.Count > 0);

            // Both, merged. Roadway alone would cost every building the collision of its walls,
            // since a house carries a roadway LOD for its floors and stairs.
            var built = BuildDetail(geometry ?? visualLods[^1], CollisionNodeName, null, geometryOnly: true)
                ?? BuildDetail(visualLods[^1], CollisionNodeName, null, geometryOnly: true);
            if (built == null || roadway == null)
            {
                return built;
            }
            var deck = BuildDetail(roadway, CollisionNodeName, null, geometryOnly: true);
            return deck == null ? built : Merge(built, deck);
        }

        /// <summary>
        /// Appends one collision mesh onto another, shifting the second's vertex indices past the
        /// first's so both sets of triangles address the same combined position array.
        /// </summary>
        private static DetailLevel Merge(DetailLevel first, DetailLevel second)
        {
            var offset = first.VertexCount;
            var group = first.Groups[0];
            foreach (var other in second.Groups)
            {
                foreach (var token in other.Indices.ToString()
                             .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    group.Indices.Append((int.Parse(token, Invariant) + offset).ToString(Invariant)).Append(' ');
                }
                group.TriangleCount += other.TriangleCount;
            }

            first.Positions += second.Positions;
            first.VertexCount += second.VertexCount;
            return first;
        }

        private static DetailLevel? BuildDetail(LOD lod, string nodeName,
            Func<string, string?>? resolveTexture, bool geometryOnly = false)
        {
            var vertices = lod.Vertices;
            if (vertices.Count == 0)
            {
                return null;
            }
            // A collision hull is never shaded or textured, so it carries positions only
            var normals = geometryOnly ? Array.Empty<Vector3P>() : GetNormals(lod);
            var uvs = geometryOnly || lod.UvSets.Length == 0 ? null : lod.UvSets[0].GetUV();

            // Arma is left-handed with Y up and Z north; Torque is right-handed with Z up and Y
            // north. Swapping Y and Z converts the axes and the handedness at once, so the triangle
            // winding has to be reversed to compensate (see BuildTriangleGroups).
            var positions = new StringBuilder();
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var vertex in vertices)
            {
                positions.Append(vertex.X.ToString("0.####", Invariant)).Append(' ')
                         .Append(vertex.Z.ToString("0.####", Invariant)).Append(' ')
                         .Append(vertex.Y.ToString("0.####", Invariant)).Append(' ');
                var swapped = new Vector3(vertex.X, vertex.Z, vertex.Y);
                min = Vector3.Min(min, swapped);
                max = Vector3.Max(max, swapped);
            }

            // Negated, and that is not cosmetic. Swapping Y and Z mirrors the model, so the triangle
            // winding is reversed in BuildTriangleGroups to keep the faces pointing outwards. The
            // stored normals then sit at odds with the faces they belong to: measured on the shipped
            // shapes, 995 triangles out of 1000 had a normal opposing their own winding. Lighting
            // reads those as facing away from the sun, so every surface renders unlit, which is what
            // made the buildings, the rocks and the road markings look black whatever the material.
            var normalText = new StringBuilder();
            foreach (var normal in normals)
            {
                normalText.Append((-normal.X).ToString("0.####", Invariant)).Append(' ')
                          .Append((-normal.Z).ToString("0.####", Invariant)).Append(' ')
                          .Append((-normal.Y).ToString("0.####", Invariant)).Append(' ');
            }

            var uvText = new StringBuilder();
            if (uvs != null)
            {
                foreach (var uv in uvs)
                {
                    // COLLADA measures V from the bottom, Arma from the top
                    uvText.Append(uv.X.ToString("0.####", Invariant)).Append(' ')
                          .Append((1f - uv.Y).ToString("0.####", Invariant)).Append(' ');
                }
            }

            var groups = BuildTriangleGroups(lod, normals.Count > 0, uvs != null, resolveTexture);
            if (groups.Count == 0)
            {
                return null;
            }

            return new DetailLevel
            {
                // Torque takes the trailing number as the detail size and the rest as the shape name
                Name = nodeName,
                VertexCount = vertices.Count,
                NormalCount = normals.Count,
                UvCount = uvs?.Length ?? 0,
                Positions = positions.ToString(),
                Normals = normalText.ToString(),
                Uvs = uvText.ToString(),
                Groups = groups,
                Min = min,
                Max = max,
            };
        }

        private sealed class TriangleGroup
        {
            public string Material { get; set; } = string.Empty;

            public string? Texture { get; set; }

            public string? ArmaTexture { get; set; }

            /// <summary>Flat colour to use when the section has no real texture.</summary>
            public string Colour { get; set; } = "0.62 0.6 0.56 1";

            public StringBuilder Indices { get; } = new();

            public int TriangleCount { get; set; }
        }

        /// <summary>
        /// One group per texture, with quads split into triangles. COLLADA wants every polygon of a
        /// triangles element to have three vertices.
        /// </summary>
        private static List<TriangleGroup> BuildTriangleGroups(LOD lod, bool hasNormals, bool hasUv,
            Func<string, string?>? resolveTexture)
        {
            var groups = new List<TriangleGroup>();
            var byMaterial = new Dictionary<string, TriangleGroup>(StringComparer.OrdinalIgnoreCase);
            var textureCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in lod.Sections)
            {
                var armaTexture = section.TextureIndex >= 0 && section.TextureIndex < lod.Textures.Length
                    ? lod.Textures[section.TextureIndex]
                    : null;
                if (string.IsNullOrEmpty(armaTexture))
                {
                    // Most sections name no texture of their own and reach it through their rvmat
                    // instead. Reading only LOD.Textures left 47% of the exported triangles on the
                    // untextured fallback, which is why whole buildings came out a flat pale grey.
                    armaTexture = FindMaterialTexture(lod, section.MaterialIndex);
                }

                // Arma writes flat colours as a pseudo texture, #(argb,8,8,3)color(r,g,b,a). There is
                // no file behind those, so they become a coloured material instead.
                var procedural = armaTexture != null ? TryReadProceduralColour(armaTexture) : null;
                if (procedural != null)
                {
                    armaTexture = null;
                }

                var material = armaTexture == null
                    ? (procedural == null ? "grm_untextured" : "grm_color_" + P3dObjExporter.SanitizeName(procedural))
                    : P3dObjExporter.SanitizeName(Path.GetFileNameWithoutExtension(armaTexture));

                if (!byMaterial.TryGetValue(material, out var group))
                {
                    string? texture = null;
                    if (armaTexture != null && resolveTexture != null)
                    {
                        if (!textureCache.TryGetValue(armaTexture, out texture))
                        {
                            texture = resolveTexture(armaTexture);
                            textureCache[armaTexture] = texture;
                        }
                    }
                    group = new TriangleGroup { Material = material, Texture = texture, ArmaTexture = armaTexture };
                    if (procedural != null)
                    {
                        group.Colour = procedural.Replace('_', ' ');
                    }
                    byMaterial.Add(material, group);
                    groups.Add(group);
                }

                foreach (var face in section.GetFaces(lod.Polygons.Faces))
                {
                    var indices = face.VertexIndices;
                    for (var i = 2; i < indices.Length; i++)
                    {
                        // Swapping the Y and Z axes has a determinant of -1, so it flips handedness
                        // and with it the winding order. The corners are emitted in reverse to put
                        // the faces back outwards; without this every model is inside out, which
                        // reads as invisible walls you still collide with.
                        AppendVertex(group.Indices, indices[i], hasNormals, hasUv);
                        AppendVertex(group.Indices, indices[i - 1], hasNormals, hasUv);
                        AppendVertex(group.Indices, indices[0], hasNormals, hasUv);
                        group.TriangleCount++;
                    }
                }
            }

            return groups.Where(g => g.TriangleCount > 0).ToList();
        }

        /// <summary>
        /// Colour texture of a section's embedded material, or null when it has none.
        /// </summary>
        /// <remarks>
        /// An rvmat lists its textures as numbered stages: the diffuse map is the one Arma suffixes
        /// <c>_co</c>, the rest being normal, specular and detail maps that mean nothing here. The
        /// suffix is the reliable marker, since the stage order is not fixed across shaders.
        /// </remarks>
        private static string? FindMaterialTexture(LOD lod, int materialIndex)
        {
            if (lod.Materials == null || materialIndex < 0 || materialIndex >= lod.Materials.Length)
            {
                return null;
            }
            var stages = lod.Materials[materialIndex]?.StageTextures;
            if (stages == null)
            {
                return null;
            }

            string? fallback = null;
            foreach (var stage in stages)
            {
                var texture = stage?.Texture;
                if (string.IsNullOrEmpty(texture) || texture.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }
                var name = Path.GetFileNameWithoutExtension(texture);
                if (name.EndsWith("_co", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("_ca", StringComparison.OrdinalIgnoreCase))
                {
                    return texture;
                }
                // Anything that is plainly not a colour map stays a last resort
                if (fallback == null
                    && !name.EndsWith("_nohq", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("_smdi", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("_as", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("_dt", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith("_mc", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = texture;
                }
            }
            return fallback;
        }

        private static void AppendVertex(StringBuilder indices, int index, bool hasNormals, bool hasUv)
        {
            indices.Append(index.ToString(Invariant)).Append(' ');
            if (hasNormals)
            {
                indices.Append(index.ToString(Invariant)).Append(' ');
            }
            if (hasUv)
            {
                indices.Append(index.ToString(Invariant)).Append(' ');
            }
        }

        private static string BuildDocument(string name, List<DetailLevel> details)
        {
            var effects = new StringBuilder();
            var materials = new StringBuilder();
            var images = new StringBuilder();
            var geometries = new StringBuilder();
            var nodes = new StringBuilder();

            // Every detail level shares one material library: the LODs of a model are the same
            // surfaces at different densities, and Torque expects a material name to mean the same
            // thing at every detail.
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in details.SelectMany(d => d.Groups))
            {
                if (!written.Add(group.Material))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(group.Texture))
                {
                    images.Append($@"
  <image id=""{group.Material}-image""><init_from>{group.Texture}</init_from></image>");
                    effects.Append($@"
  <effect id=""{group.Material}-effect""><profile_COMMON>
   <newparam sid=""{group.Material}-surface""><surface type=""2D""><init_from>{group.Material}-image</init_from></surface></newparam>
   <newparam sid=""{group.Material}-sampler""><sampler2D><source>{group.Material}-surface</source></sampler2D></newparam>
   <technique sid=""common""><lambert><diffuse><texture texture=""{group.Material}-sampler"" texcoord=""UVMap""/></diffuse></lambert></technique>
  </profile_COMMON></effect>");
                }
                else
                {
                    effects.Append($@"
  <effect id=""{group.Material}-effect""><profile_COMMON><technique sid=""common""><lambert><diffuse><color>{group.Colour}</color></diffuse></lambert></technique></profile_COMMON></effect>");
                }
                materials.Append($@"
  <material id=""{group.Material}-material"" name=""{group.Material}""><instance_effect url=""#{group.Material}-effect""/></material>");
            }

            foreach (var detail in details)
            {
                geometries.Append(BuildGeometry(detail));

                var binds = new StringBuilder();
                foreach (var group in detail.Groups)
                {
                    binds.Append($@"
      <instance_material symbol=""{group.Material}"" target=""#{group.Material}-material""><bind_vertex_input semantic=""UVMap"" input_semantic=""TEXCOORD"" input_set=""0""/></instance_material>");
                }
                nodes.Append($@"
     <node id=""{detail.Name}"" name=""{detail.Name}"" type=""NODE"">
      <instance_geometry url=""#{detail.Name}-mesh"">
       <bind_material><technique_common>{binds}
       </technique_common></bind_material>
      </instance_geometry>
     </node>");
            }

            return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<COLLADA xmlns=""http://www.collada.org/2005/11/COLLADASchema"" version=""1.4.1"">
 <asset><created>2026-01-01T00:00:00Z</created><modified>2026-01-01T00:00:00Z</modified><unit name=""meter"" meter=""1""/><up_axis>Z_UP</up_axis></asset>
 <library_images>{images}
 </library_images>
 <library_effects>{effects}
 </library_effects>
 <library_materials>{materials}
 </library_materials>
 <library_geometries>{geometries}
 </library_geometries>
 <library_visual_scenes>
  <visual_scene id=""Scene"" name=""Scene"">
   <node id=""base00"" name=""base00"" type=""NODE"">
    <node id=""start01"" name=""start01"" type=""NODE"">
     <node id=""nulldetail{CullDetailSize}"" name=""nulldetail{CullDetailSize}"" type=""NODE""/>{nodes}
    </node>
   </node>
  </visual_scene>
 </library_visual_scenes>
 <scene><instance_visual_scene url=""#Scene""/></scene>
</COLLADA>";
        }

        private static string BuildGeometry(DetailLevel detail)
        {
            var name = detail.Name;
            var hasNormals = detail.NormalCount > 0;
            var hasUv = detail.UvCount > 0;

            var sources = new StringBuilder();
            sources.Append($@"
    <source id=""{name}-pos"">
     <float_array id=""{name}-pos-array"" count=""{detail.VertexCount * 3}"">{detail.Positions}</float_array>
     <technique_common><accessor source=""#{name}-pos-array"" count=""{detail.VertexCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>");
            if (hasNormals)
            {
                sources.Append($@"
    <source id=""{name}-nrm"">
     <float_array id=""{name}-nrm-array"" count=""{detail.NormalCount * 3}"">{detail.Normals}</float_array>
     <technique_common><accessor source=""#{name}-nrm-array"" count=""{detail.NormalCount}"" stride=""3""><param name=""X"" type=""float""/><param name=""Y"" type=""float""/><param name=""Z"" type=""float""/></accessor></technique_common>
    </source>");
            }
            if (hasUv)
            {
                sources.Append($@"
    <source id=""{name}-uv"">
     <float_array id=""{name}-uv-array"" count=""{detail.UvCount * 2}"">{detail.Uvs}</float_array>
     <technique_common><accessor source=""#{name}-uv-array"" count=""{detail.UvCount}"" stride=""2""><param name=""S"" type=""float""/><param name=""T"" type=""float""/></accessor></technique_common>
    </source>");
            }

            var triangles = new StringBuilder();
            foreach (var group in detail.Groups)
            {
                var offset = 1;
                var inputs = new StringBuilder($@"
      <input semantic=""VERTEX"" source=""#{name}-verts"" offset=""0""/>");
                if (hasNormals)
                {
                    inputs.Append($@"
      <input semantic=""NORMAL"" source=""#{name}-nrm"" offset=""{offset++}""/>");
                }
                if (hasUv)
                {
                    inputs.Append($@"
      <input semantic=""TEXCOORD"" source=""#{name}-uv"" offset=""{offset}"" set=""0""/>");
                }
                triangles.Append($@"
    <triangles material=""{group.Material}"" count=""{group.TriangleCount}"">{inputs}
     <p>{group.Indices}</p>
    </triangles>");
            }

            return $@"
  <geometry id=""{name}-mesh"" name=""{name}"">
   <mesh>{sources}
    <vertices id=""{name}-verts""><input semantic=""POSITION"" source=""#{name}-pos""/></vertices>{triangles}
   </mesh>
  </geometry>";
        }

        /// <summary>
        /// Reads Arma's procedural colour pseudo textures, <c>#(argb,8,8,3)color(r,g,b,a)</c>,
        /// returning the components separated by underscores, or null when it is a real texture.
        /// </summary>
        private static string? TryReadProceduralColour(string texture)
        {
            if (!texture.StartsWith("#", StringComparison.Ordinal))
            {
                return null;
            }
            var match = System.Text.RegularExpressions.Regex.Match(texture,
                @"color\(\s*([0-9.]+)\s*,\s*([0-9.]+)\s*,\s*([0-9.]+)\s*,\s*([0-9.]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return "0.62_0.6_0.56_1";
            }
            return $"{match.Groups[1].Value}_{match.Groups[2].Value}_{match.Groups[3].Value}_1";
        }

        /// <summary>
        /// The model's visual LODs, finest first. Arma numbers them by resolution, a lower value
        /// meaning more detail; anything at or above <see cref="FirstSpecialLodResolution"/> is a
        /// special LOD (geometry, roadway, view pilot and so on) and is not drawable.
        /// </summary>
        private static List<LOD> SelectVisualLods(ODOL odol)
        {
            return odol.Lods
                .Where(lod => lod.Resolution < FirstSpecialLodResolution && lod.Vertices.Count > 0)
                .OrderBy(lod => lod.Resolution)
                .ToList();
        }

        private static IReadOnlyList<Vector3P> GetNormals(LOD lod)
        {
            if (lod.Normals != null && lod.Normals.Count > 0)
            {
                return lod.Normals;
            }
            if (lod.NormalsCompressed != null && lod.NormalsCompressed.Count > 0)
            {
                return lod.NormalsCompressed.Select(n => (Vector3P)n).ToList();
            }
            return Array.Empty<Vector3P>();
        }
    }
}
