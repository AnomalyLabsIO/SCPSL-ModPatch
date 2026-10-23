using AssetsTools.NET;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>Reads release labels from managed version fields and their serialized scene values.</summary>
    internal static class MonoVersionReader
    {
        static readonly UTF8Encoding Utf8 = new(false, true);

        internal static Version? ParseVersion(string? label)
        {
            Match match = Regex.Match(label ?? string.Empty, @"^(?:Open-Beta\s+)?(\d+\.\d+\.\d+)(?!\d|\.\d)");
            return match.Success && Version.TryParse(match.Groups[1].Value, out Version? version) ? version : null;
        }

        internal static string? Read(byte[] image, string assemblyPath)
        {
            using var stream = new MemoryStream(image, writable: false);
            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(stream, new ReaderParameters { InMemory = true, ReadSymbols = false });
            TypeDefinition[] networks = assembly.MainModule.Types.Where(type => type.FullName == "CustomNetworkManager").ToArray();
            if (networks.Length == 0) return null;
            if (networks.Length != 1) throw new InvalidDataException("The managed network version type is ambiguous.");
            TypeDefinition network = networks[0];
            MethodDefinition[] methods = AllTypes(assembly.MainModule.Types).SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
            FieldDefinition[] statics = network.Fields.Where(field => field.IsStatic && field.Name == "CompatibleVersions" && field.FieldType.FullName == "System.String[]").ToArray();
            if (statics.Length > 1) throw new InvalidDataException("The managed compatible-version field is ambiguous.");
            if (statics.Length == 1)
            {
                FieldDefinition field = statics[0];
                var labels = new List<string>();
                int stores = 0;
                foreach (MethodDefinition method in methods)
                {
                    Instruction[] code = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                    for (int index = 0; index < code.Length; index++)
                    {
                        if (code[index].OpCode.Code != Code.Stsfld || !IsField(code[index], field)) continue;
                        stores++;
                        if (method.DeclaringType != network || method.Name is not ("SetCompatibleVersions" or ".cctor"))
                            throw new InvalidDataException("The compatible-version field has an unsupported writer.");
                        string? label = ArrayInitializer(code, index);
                        if (label == null) throw new InvalidDataException("The compatible-version array initializer is not a supported literal array.");
                        labels.Add(label);
                    }
                }
                if (stores != 1 || labels.Count != 1 || !HasConsumer(methods, field))
                    throw new InvalidDataException("The compatible-version publication or first-element consumer is absent or ambiguous.");
                return labels[0];
            }

            // These legacy layouts serialize the version as the final public/SerializeField
            // member. Constructor defaults can be overridden by the scene and are not identity.
            FieldDefinition? serialized = network.Fields.LastOrDefault(field => !field.IsStatic && !field.IsLiteral && !field.IsInitOnly && !field.IsNotSerialized &&
                (field.IsPublic || field.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == "UnityEngine.SerializeField")));
            if (serialized == null || serialized.Name is not ("versionstring" or "compatibleVersions" or "CompatibleVersions") ||
                serialized.FieldType.FullName is not ("System.String" or "System.String[]")) return null;
            if (!HasConsumer(methods, serialized))
                throw new InvalidDataException("The serialized network version field has no supported consumer.");
            string? dataDirectory = Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!)?.FullName;
            if (dataDirectory == null) return null;
            var candidates = new List<string>();
            foreach (string name in new[] { "level0", "level1" })
            {
                string scene = Path.Combine(dataDirectory, name);
                if (File.Exists(scene)) candidates.AddRange(ReadScene(scene, serialized.FieldType.FullName == "System.String[]"));
            }
            if (candidates.Count > 1) throw new InvalidDataException("The serialized network release label is ambiguous across menu scene objects.");
            return candidates.SingleOrDefault();
        }

        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition type in types)
            {
                yield return type;
                foreach (TypeDefinition nested in AllTypes(type.NestedTypes)) yield return nested;
            }
        }

        static bool IsField(Instruction instruction, FieldDefinition field) => instruction.Operand is FieldReference reference && reference.FullName == field.FullName;

        static bool HasConsumer(IEnumerable<MethodDefinition> methods, FieldDefinition field)
        {
            foreach (MethodDefinition method in methods)
            {
                Instruction[] code = method.Body.Instructions.Where(instruction => instruction.OpCode.Code != Code.Nop).ToArray();
                for (int i = 0; i < code.Length; i++)
                    if (code[i].OpCode.Code == (field.IsStatic ? Code.Ldsfld : Code.Ldfld) && IsField(code[i], field) &&
                        (field.FieldType.FullName == "System.String" ? code.Skip(i + 1).Take(3).Any(instruction => instruction.OpCode.Code is Code.Call or Code.Callvirt &&
                            instruction.Operand is MethodReference reference && reference.FullName is
                                "System.Boolean System.String::op_Equality(System.String,System.String)" or
                                "System.Boolean Steamworks.SteamMatchmaking::SetLobbyData(Steamworks.CSteamID,System.String,System.String)") :
                            i + 2 < code.Length && Integer(code[i + 1]) == 0 && code[i + 2].OpCode.Code == Code.Ldelem_Ref)) return true;
            }
            return false;
        }

        static string? ArrayInitializer(Instruction[] code, int store)
        {
            for (int count = 1; count <= 32; count++)
            {
                int start = store - 2 - count * 4;
                if (start < 0 || Integer(code[start]) != count || code[start + 1].OpCode.Code != Code.Newarr ||
                    code[start + 1].Operand is not TypeReference type || type.FullName != "System.String") continue;
                string? first = null;
                bool valid = true;
                for (int item = 0; item < count; item++)
                {
                    int index = start + 2 + item * 4;
                    if (code[index].OpCode.Code != Code.Dup || Integer(code[index + 1]) != item || code[index + 2].OpCode.Code != Code.Ldstr ||
                        code[index + 2].Operand is not string text || !ValidLabel(text) || code[index + 3].OpCode.Code != Code.Stelem_Ref)
                    { valid = false; break; }
                    first ??= text;
                }
                if (valid) return first;
            }
            return null;
        }

        static int? Integer(Instruction instruction) => instruction.OpCode.Code switch
        {
            Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2,
            Code.Ldc_I4_3 => 3, Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6,
            Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8, Code.Ldc_I4 => (int)instruction.Operand,
            Code.Ldc_I4_S => Convert.ToInt32(instruction.Operand), _ => null
        };

        static bool ValidLabel(string text) => text.Length is > 0 and <= 256 && !text.Any(char.IsControl);

        static IEnumerable<string> ReadScene(string path, bool array)
        {
            // AssetsTools reads only serialized data; it never loads or executes game scripts.
            AssetsFile file = OpenAssets(path, out FileStream stream);
            var externals = new Dictionary<string, (AssetsFile File, FileStream Stream)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var networkObjects = new Dictionary<long, HashSet<long>>();
                foreach (AssetFileInfo info in file.GetAssetsOfType(1))
                {
                    byte[] raw = ReadObject(file, stream, info);
                    if (raw.Length < 12) continue;
                    int count = Int32(raw, 0);
                    if (count is < 1 or > 512 || 8L + 12L * count + 4 > raw.Length) continue;
                    int position = 8 + count * 12;
                    if (!ReadString(raw, ref position, out string? name) || position + 3 != raw.Length ||
                        !Regex.IsMatch(name!, @"^network\s*manager$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
                    var components = new HashSet<long>();
                    for (int i = 0; i < count; i++)
                        if (Int32(raw, 4 + i * 12) == 0) components.Add(Int64(raw, 8 + i * 12));
                    networkObjects.Add(info.PathId, components);
                }
                var result = new List<string>();
                foreach (AssetFileInfo info in file.GetAssetsOfType(114))
                {
                    byte[] raw = ReadObject(file, stream, info);
                    if (raw.Length < 32 || Int32(raw, 0) != 0 || !networkObjects.TryGetValue(Int64(raw, 4), out HashSet<long>? components) || !components.Contains(info.PathId)) continue;
                    // MonoBehaviour's GameObject PPtr, enabled flag, script PPtr and name.
                    // A live script reference and the backlink from GameObject are both required.
                    if (raw[12] > 1 || raw[13] != 0 || raw[14] != 0 || raw[15] != 0 || Int32(raw, 16) < 0 || Int64(raw, 20) == 0) continue;
                    int headerEnd = 28;
                    if (!ReadString(raw, ref headerEnd, out _, allowEmpty: true)) continue;
                    if (!IsNetworkScript(file, stream, raw, Path.GetDirectoryName(path)!, externals)) continue;
                    for (int start = headerEnd; start + 4 <= raw.Length; start += 4)
                    {
                        int cursor = start;
                        int count = array ? Int32(raw, cursor) : 1;
                        if (count is < 1 or > 32) continue;
                        if (array) cursor += 4;
                        string? label = null;
                        int item = 0;
                        for (; item < count; item++)
                        {
                            if (!ReadString(raw, ref cursor, out string? text)) break;
                            label ??= text;
                        }
                        if (item == count && cursor == raw.Length) result.Add(label!);
                    }
                }
                return result;
            }
            finally
            {
                foreach (var external in externals.Values) external.File.Close();
                file.Close();
            }
        }

        static bool IsNetworkScript(AssetsFile scene, FileStream sceneStream, byte[] behaviour, string directory,
            Dictionary<string, (AssetsFile File, FileStream Stream)> externals)
        {
            int fileId = Int32(behaviour, 16);
            long pathId = Int64(behaviour, 20);
            AssetsFile source = scene;
            FileStream stream = sceneStream;
            if (fileId != 0)
            {
                if (fileId < 1 || fileId > scene.Metadata.Externals.Count)
                    throw new InvalidDataException("The network MonoBehaviour script points outside its scene reference table.");
                string name = scene.Metadata.Externals[fileId - 1].PathName;
                if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || name.Contains('\\') || name.Contains('/') || name.Contains(':'))
                    throw new InvalidDataException("The network MonoScript must be stored in a sibling serialized asset file.");
                string path = Path.Combine(directory, name);
                if (!externals.TryGetValue(path, out var external))
                {
                    if (!File.Exists(path)) throw new InvalidDataException($"The matching Mono version script asset is missing: {name}.");
                    AssetsFile loaded = OpenAssets(path, out FileStream loadedStream);
                    external = (loaded, loadedStream);
                    externals.Add(path, external);
                }
                source = external.File;
                stream = external.Stream;
                if (source.Metadata.UnityVersion != scene.Metadata.UnityVersion)
                    throw new InvalidDataException("The Mono version script asset does not match the menu scene Unity version.");
            }
            AssetFileInfo? info = source.GetAssetInfo(pathId);
            if (info == null || info.GetTypeId(source) != 115)
                throw new InvalidDataException("The network MonoBehaviour script pointer does not resolve to a MonoScript.");
            byte[] raw = ReadObject(source, stream, info);
            int position = 0;
            if (!ReadString(raw, ref position, out _, allowEmpty: true) || position + 20 > raw.Length) return false;
            position += 20; // Execution order followed by the 128-bit properties hash.
            if (!ReadString(raw, ref position, out string? className) ||
                !ReadString(raw, ref position, out string? nameSpace, allowEmpty: true) ||
                !ReadString(raw, ref position, out string? assemblyName)) return false;
            return className == "CustomNetworkManager" && nameSpace == "" && assemblyName == "Assembly-CSharp.dll";
        }

        static AssetsFile OpenAssets(string path, out FileStream stream)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("A Mono version asset cannot be a symbolic link.");
            stream = File.OpenRead(path);
            try
            {
                if (stream.Length is < 20 or > 268435456) throw new InvalidDataException("The Mono version asset has an unsupported size.");
                byte[] header = new byte[20];
                if (stream.Read(header, 0, header.Length) != header.Length || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4)) != 17 || header[16] != 0 ||
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4)) != stream.Length || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) > 16777216 ||
                    BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4)) > stream.Length)
                    throw new InvalidDataException("The Mono version asset has an unsupported serialized file header.");
                stream.Position = 0;
                var file = new AssetsFile();
                file.Read(stream);
                if (file.Header.Version != 17 || file.Header.Endianness || file.Metadata.AssetInfos.Count > 100000 ||
                    !(file.Metadata.UnityVersion.StartsWith("2017.", StringComparison.Ordinal) || file.Metadata.UnityVersion.StartsWith("2018.", StringComparison.Ordinal)))
                    throw new InvalidDataException("The Mono version asset uses an unsupported serialized object layout.");
                return file;
            }
            catch { stream.Dispose(); throw; }
        }

        static byte[] ReadObject(AssetsFile file, FileStream stream, AssetFileInfo info)
        {
            long offset = info.GetAbsoluteByteOffset(file);
            if (info.ByteSize > 16777216 || offset < file.Header.DataOffset || offset > stream.Length - info.ByteSize)
                throw new InvalidDataException("The Mono version scene contains an invalid object span.");
            byte[] raw = new byte[info.ByteSize];
            stream.Position = offset;
            int read = 0;
            while (read < raw.Length)
            {
                int length = stream.Read(raw, read, raw.Length - read);
                if (length == 0) throw new EndOfStreamException("The Mono version scene ended inside an object.");
                read += length;
            }
            return raw;
        }

        static int Int32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        static long Int64(byte[] bytes, int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));

        static bool ReadString(byte[] raw, ref int position, out string? value, bool allowEmpty = false)
        {
            value = null;
            if (position < 0 || position + 4 > raw.Length || position % 4 != 0) return false;
            int length = Int32(raw, position);
            if (length < (allowEmpty ? 0 : 1) || length > 1024 || position + 4L + length > raw.Length) return false;
            int end = position + 4 + length;
            int aligned = (end + 3) & ~3;
            if (aligned > raw.Length) return false;
            for (int padding = end; padding < aligned; padding++) if (raw[padding] != 0) return false;
            try { value = Utf8.GetString(raw, position + 4, length); }
            catch (DecoderFallbackException) { return false; }
            if ((!allowEmpty || value.Length != 0) && !ValidLabel(value)) return false;
            position = aligned;
            return true;
        }
    }
}
