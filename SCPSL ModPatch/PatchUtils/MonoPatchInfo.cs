using Newtonsoft.Json.Linq;

namespace SCPSL_ModPatch.PatchUtils
{
    /// <summary>A symbolic CIL instruction. Member operands use Cecil's full signatures.</summary>
    public sealed class MonoInstructionInfo
    {
        public string op = string.Empty;
        [System.Text.Json.Serialization.JsonConverter(typeof(MonoOperandJsonConverter))]
        public JToken? operand;
        public string? label;
    }

    /// <summary>An edit relative to one unique, method-scoped instruction pattern.</summary>
    public sealed class MonoEditInfo
    {
        public string operation = string.Empty;
        public int? count;
        public MonoInstructionInfo[]? instructions;
    }

    /// <summary>Preserves the shared JSON operand representation for both manifest readers.</summary>
    public sealed class MonoOperandJsonConverter : System.Text.Json.Serialization.JsonConverter<JToken>
    {
        public override JToken? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
            System.Text.Json.JsonSerializerOptions options)
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.ParseValue(ref reader);
            return JToken.Parse(document.RootElement.GetRawText());
        }

        public override void Write(System.Text.Json.Utf8JsonWriter writer, JToken value,
            System.Text.Json.JsonSerializerOptions options) => writer.WriteRawValue(value.ToString(Newtonsoft.Json.Formatting.None));
    }
}
