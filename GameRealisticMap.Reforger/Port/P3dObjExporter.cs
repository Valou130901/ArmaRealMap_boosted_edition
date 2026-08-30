using System.Globalization;
using System.Text;
using BIS.Core.Math;
using BIS.P3D.ODOL;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>Files produced for one ported model, and the textures it still needs.</summary>
    public sealed class P3dExportResult
    {
        public P3dExportResult(string modelName, string objFile, float resolution, int vertexCount, int faceCount)
        {
            ModelName = modelName;
            ObjFile = objFile;
            Resolution = resolution;
            VertexCount = vertexCount;
            FaceCount = faceCount;
        }

        public string ModelName { get; }

        public string ObjFile { get; }

        /// <summary>Resolution of the visual LOD that was exported (lower is more detailed).</summary>
        public float Resolution { get; }

        public int VertexCount { get; }

        public int FaceCount { get; }

        /// <summary>Arma texture paths referenced by the exported geometry.</summary>
        public List<string> Textures { get; } = new();
    }

    /// <summary>
    /// Writes the most detailed visual LOD of a binarized Arma 3 model as a Wavefront OBJ, the
    /// interchange step of the model port pipeline.
    /// </summary>
    /// <remarks>
    /// OBJ rather than FBX on purpose: the Enfusion Workbench ingests FBX, but writing binary FBX
    /// from scratch is a large surface to get wrong, whereas Blender reads OBJ losslessly and the
    /// official Enfusion Blender Tools export the FBX the Workbench wants. The generated
    /// <c>convert.py</c> drives that second hop in batch.
    /// </remarks>
    public static class P3dObjExporter
    {
        /// <summary>
        /// Resolution values at or above this mark special LODs (shadow volumes, geometry, memory,
        /// hit points...) rather than something drawable.
        /// </summary>
        private const float FirstSpecialLodResolution = 1000f;

        /// <summary>
        /// Exports the most detailed visual LOD of <paramref name="odol"/> into
        /// <paramref name="targetDirectory"/>, or returns null when the model has no drawable LOD.
        /// </summary>
        /// <param name="resolveTexture">
        /// Maps an Arma texture path to a file name usable from the mtl, or null to leave the
        /// material without a texture. Called once per distinct texture.
        /// </param>
        public static P3dExportResult? Export(ODOL odol, string modelName, string targetDirectory,
            Func<string, string?>? resolveTexture = null)
        {
            var lod = SelectVisualLod(odol);
            if (lod == null)
            {
                return null;
            }

            Directory.CreateDirectory(targetDirectory);

            var baseName = SanitizeName(modelName);
            var objPath = Path.Combine(targetDirectory, baseName + ".obj");
            var mtlPath = Path.Combine(targetDirectory, baseName + ".mtl");

            var vertices = lod.Vertices;
            var normals = GetNormals(lod);
            var uvs = lod.UvSets.Length > 0 ? lod.UvSets[0].GetUV() : null;

            var result = new P3dExportResult(modelName, objPath, lod.Resolution, vertices.Count, lod.FaceCount);
            var materials = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            var obj = new StringBuilder();
            obj.AppendLine($"# {modelName}");
            obj.AppendLine(FormattableString.Invariant($"# exported from Arma 3 by Game Realistic Map, visual LOD {lod.Resolution}"));
            obj.AppendLine($"mtllib {baseName}.mtl");
            obj.AppendLine($"o {baseName}");

            // Arma models are left-handed (X right, Y up, Z forward). Negating Z makes them
            // right-handed for OBJ; the winding is reversed further down to keep faces outward.
            foreach (var vertex in vertices)
            {
                obj.AppendLine(FormattableString.Invariant($"v {vertex.X:0.#####} {vertex.Y:0.#####} {-vertex.Z:0.#####}"));
            }
            if (uvs != null)
            {
                foreach (var uv in uvs)
                {
                    // OBJ measures V from the bottom, Arma from the top
                    obj.AppendLine(FormattableString.Invariant($"vt {uv.X:0.#####} {1f - uv.Y:0.#####}"));
                }
            }
            foreach (var normal in normals)
            {
                obj.AppendLine(FormattableString.Invariant($"vn {-normal.X:0.#####} {-normal.Y:0.#####} {normal.Z:0.#####}"));
            }

            var faceCount = 0;
            foreach (var section in lod.Sections)
            {
                var texture = GetSectionTexture(lod, section);
                var materialName = GetMaterialName(texture, materials.Count);
                if (!materials.ContainsKey(materialName))
                {
                    materials.Add(materialName, texture);
                    if (!string.IsNullOrEmpty(texture))
                    {
                        result.Textures.Add(texture);
                    }
                }

                obj.AppendLine($"usemtl {materialName}");
                foreach (var face in section.GetFaces(lod.Polygons.Faces))
                {
                    AppendFace(obj, face.VertexIndices, uvs != null, normals.Count > 0);
                    faceCount++;
                }
            }

            if (faceCount == 0)
            {
                // A model whose sections carry no drawable face is not worth a file
                return null;
            }

            File.WriteAllText(objPath, obj.ToString(), new UTF8Encoding(false));
            WriteMtl(mtlPath, materials, resolveTexture);

            return result;
        }

        /// <summary>
        /// Vertex normals of a LOD. From model version 45 on, Bohemia stores them packed in
        /// <see cref="LOD.NormalsCompressed"/> and leaves <see cref="LOD.Normals"/> null, so both
        /// have to be handled.
        /// </summary>
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

        /// <summary>Most detailed drawable LOD, or null when the model only has special LODs.</summary>
        private static LOD? SelectVisualLod(ODOL odol)
        {
            LOD? best = null;
            foreach (var lod in odol.Lods)
            {
                if (lod.Resolution >= FirstSpecialLodResolution || lod.Vertices.Count == 0)
                {
                    continue;
                }
                // Lower resolution value means more detail
                if (best == null || lod.Resolution < best.Resolution)
                {
                    best = lod;
                }
            }
            return best;
        }

        private static string? GetSectionTexture(LOD lod, Section section)
        {
            if (section.TextureIndex < 0 || section.TextureIndex >= lod.Textures.Length)
            {
                return null;
            }
            var texture = lod.Textures[section.TextureIndex];
            return string.IsNullOrEmpty(texture) ? null : texture;
        }

        private static string GetMaterialName(string? texture, int index)
        {
            if (string.IsNullOrEmpty(texture))
            {
                return FormattableString.Invariant($"material_{index}");
            }
            return SanitizeName(Path.GetFileNameWithoutExtension(texture));
        }

        /// <summary>
        /// Writes one OBJ face, reversing the winding to compensate for the mirrored Z axis.
        /// </summary>
        private static void AppendFace(StringBuilder obj, int[] indices, bool hasUv, bool hasNormals)
        {
            obj.Append('f');
            for (var i = indices.Length - 1; i >= 0; i--)
            {
                var index = indices[i] + 1; // OBJ indices are 1-based
                if (hasUv && hasNormals)
                {
                    obj.Append(CultureInfo.InvariantCulture, $" {index}/{index}/{index}");
                }
                else if (hasUv)
                {
                    obj.Append(CultureInfo.InvariantCulture, $" {index}/{index}");
                }
                else if (hasNormals)
                {
                    obj.Append(CultureInfo.InvariantCulture, $" {index}//{index}");
                }
                else
                {
                    obj.Append(CultureInfo.InvariantCulture, $" {index}");
                }
            }
            obj.AppendLine();
        }

        private static void WriteMtl(string mtlPath, Dictionary<string, string?> materials, Func<string, string?>? resolveTexture)
        {
            var mtl = new StringBuilder();
            mtl.AppendLine("# Generated by Game Realistic Map");
            foreach (var pair in materials)
            {
                mtl.AppendLine();
                mtl.AppendLine($"newmtl {pair.Key}");
                mtl.AppendLine("Kd 1.000 1.000 1.000");
                mtl.AppendLine("d 1.0");
                mtl.AppendLine("illum 2");
                if (!string.IsNullOrEmpty(pair.Value))
                {
                    var file = resolveTexture?.Invoke(pair.Value);
                    if (!string.IsNullOrEmpty(file))
                    {
                        mtl.AppendLine($"map_Kd {file}");
                    }
                    else
                    {
                        // Keep the trail to the Arma texture even when it could not be converted
                        mtl.AppendLine($"# source texture: {pair.Value}");
                    }
                }
            }
            File.WriteAllText(mtlPath, mtl.ToString(), new UTF8Encoding(false));
        }

        /// <summary>Turns an Arma path into a flat file name safe on disk and in OBJ tokens.</summary>
        public static string SanitizeName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            }
            return sb.ToString();
        }
    }
}
