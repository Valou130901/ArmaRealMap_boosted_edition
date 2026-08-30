using System.Buffers.Binary;
using System.Text;

namespace GameRealisticMap.Reforger.Port
{
    /// <summary>One resource of an Enfusion addon: its path and the GUID that names it.</summary>
    public sealed record ReforgerResource(string Path, string Guid)
    {
        /// <summary>ResourceName as the World Editor uses it, <c>{GUID}Path/To/File.et</c>.</summary>
        public string ResourceName => "{" + Guid + "}" + Path;
    }

    /// <summary>
    /// Reads an Enfusion <c>resourceDatabase.rdb</c>, which maps every resource of an addon to its
    /// GUID. Used to harvest the prefab ResourceNames the user built in the Workbench.
    /// </summary>
    /// <remarks>
    /// Layout: <c>FORM</c>, 4 bytes, <c>RDBC</c>, then four 32-bit values, a length-prefixed
    /// NUL-terminated project name and a record count. Records are a length-prefixed NUL-terminated
    /// path, a 32-bit kind, a 16-bit flag word and a little-endian 64-bit GUID; file records carry 8
    /// further bytes. The kind value is not a dependable file/folder discriminator, so the presence
    /// of those extra bytes is detected by testing whether the next record parses.
    /// </remarks>
    public static class ReforgerResourceDatabase
    {
        public const string FileName = "resourceDatabase.rdb";

        public static IReadOnlyList<ReforgerResource> Read(string path)
        {
            return Parse(File.ReadAllBytes(path));
        }

        /// <summary>Reads the resource database of an addon folder, or an empty list if it has none.</summary>
        public static IReadOnlyList<ReforgerResource> ReadFromAddon(string addonDirectory)
        {
            var file = Path.Combine(addonDirectory, FileName);
            return File.Exists(file) ? Read(file) : Array.Empty<ReforgerResource>();
        }

        public static IReadOnlyList<ReforgerResource> Parse(byte[] data)
        {
            if (data.Length < 16
                || Encoding.ASCII.GetString(data, 0, 4) != "FORM"
                || Encoding.ASCII.GetString(data, 8, 4) != "RDBC")
            {
                throw new FormatException("Not an Enfusion resource database (FORM/RDBC header missing).");
            }

            var offset = 12 + 16; // four 32-bit header values after the magic
            if (!TryReadName(data, ref offset, out _))
            {
                throw new FormatException("Resource database header is truncated.");
            }
            offset += 4; // record count, recomputed from what actually parses

            var results = new List<ReforgerResource>();
            while (offset < data.Length && CanReadName(data, offset))
            {
                if (!TryReadName(data, ref offset, out var name))
                {
                    break;
                }
                if (offset + 14 > data.Length)
                {
                    break;
                }
                offset += 4; // kind
                offset += 2; // flags
                var guid = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
                offset += 8;

                // File records carry 8 extra bytes; probe instead of trusting the kind value
                if (!CanReadName(data, offset) && CanReadName(data, offset + 8))
                {
                    offset += 8;
                }

                results.Add(new ReforgerResource(name, guid.ToString("X16")));
            }
            return results;
        }

        private static bool CanReadName(byte[] data, int offset)
        {
            if (offset + 4 > data.Length)
            {
                return false;
            }
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (length < 1 || length > 1024 || offset + 4 + length > data.Length)
            {
                return false;
            }
            var raw = data.AsSpan(offset + 4, (int)length);
            if (raw[raw.Length - 1] != 0)
            {
                return false;
            }
            for (var i = 0; i < raw.Length - 1; i++)
            {
                if (raw[i] < 32 || raw[i] >= 127)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadName(byte[] data, ref int offset, out string name)
        {
            name = string.Empty;
            if (!CanReadName(data, offset))
            {
                return false;
            }
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            name = Encoding.UTF8.GetString(data, offset + 4, length - 1);
            offset += 4 + length;
            return true;
        }
    }
}
