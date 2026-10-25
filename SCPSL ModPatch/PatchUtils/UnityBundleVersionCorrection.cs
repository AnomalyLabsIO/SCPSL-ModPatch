using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>A validated, deferred correction of Unity's displayed game version.</summary>
    internal sealed class UnityBundleVersionCorrection
    {
        public const string RelativePath = "SCPSL_Data/globalgamemanagers";
        readonly byte[] _output;
        public string Path { get; }
        public string Before { get; }
        public string After { get; }
        public string OriginalSha256 { get; }
        public string Status => Before == After ? "already-correct" : "applied";

        UnityBundleVersionCorrection(string path, string before, string after, byte[] input, byte[] output)
        {
            Path = System.IO.Path.GetFullPath(path);
            Before = before;
            After = after;
            OriginalSha256 = Convert.ToHexString(SHA256.HashData(input));
            _output = output;
        }

        public static UnityBundleVersionCorrection Prepare(string path, string version)
        {
            if (!SupportedVersion(version))
                throw new InvalidDataException("Bundle-version correction requires a three-component numeric game version with major >= 10 and components <= 255.");
            byte[] input = File.ReadAllBytes(path);
            var candidates = new List<(int Position, int Length, string Value)>();
            for (int cursor = 0; cursor + sizeof(int) <= input.Length; cursor++)
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(input.AsSpan(cursor, sizeof(int)));
                if (size < 6 || size > 11 || (long)cursor + sizeof(int) + size > input.Length) continue;
                string value = Encoding.ASCII.GetString(input, cursor + sizeof(int), size);
                if (SupportedVersion(value)) candidates.Add((cursor, size, value));
            }
            if (candidates.Count != 1)
                throw new InvalidDataException($"Expected one unambiguous serialized bundle version in globalgamemanagers; found {candidates.Count}.");
            var field = candidates[0];
            if (field.Value == version) return new UnityBundleVersionCorrection(path, field.Value, version, input, input);

            byte[] encoded = Encoding.ASCII.GetBytes(version);
            int existingEnd = field.Position + sizeof(int) + field.Length;
            int correctedEnd = field.Position + sizeof(int) + encoded.Length;
            if (encoded.Length > field.Length || ((existingEnd + 3L) / 4) != ((correctedEnd + 3L) / 4))
                throw new InvalidDataException("Bundle-version correction would expand or shift serialized fields; no game files were saved.");
            byte[] output = input.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(field.Position, sizeof(int)), encoded.Length);
            Array.Clear(output, field.Position + sizeof(int), field.Length);
            encoded.CopyTo(output, field.Position + sizeof(int));
            return new UnityBundleVersionCorrection(path, field.Value, version, input, output);
        }

        public void Save()
        {
            using var file = new FileStream(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var hash = SHA256.Create();
            if (Convert.ToHexString(hash.ComputeHash(file)) != OriginalSha256)
                throw new IOException("globalgamemanagers changed after bundle-version correction was prepared.");
            if (Status == "already-correct") return;
            file.Position = 0;
            file.Write(_output);
            file.SetLength(_output.Length);
        }

        static bool SupportedVersion(string text)
        {
            string[] numbers = text.Split('.');
            if (numbers.Length != 3 || numbers.Any(part => part.Length is < 1 or > 3 || part.Any(character => character < '0' || character > '9'))) return false;
            return byte.TryParse(numbers[0], NumberStyles.None, CultureInfo.InvariantCulture, out byte major) && major >= 10 &&
                byte.TryParse(numbers[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
                byte.TryParse(numbers[2], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }
    }
}
