using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PcAgent.Collection;
using PcAgent.Export;
using PcAgent.Hosting;
using PcAgent.Inventory;

namespace PcAgent;

// 리플렉션 없이(AOT 호환) 직렬화하기 위한 소스 생성 컨텍스트
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentConfig))]
[JsonSerializable(typeof(PressureRecord))]
[JsonSerializable(typeof(InventoryInfo))]
[JsonSerializable(typeof(ExportSummary))]
[JsonSerializable(typeof(AgentUsageInfo))]
internal partial class AgentJsonContext : JsonSerializerContext
{
}

internal static class Json
{
    // 한글이 \uXXXX 로 이스케이프되지 않도록
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    public static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo, bool indented = false)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented, Encoder = Encoder }))
        {
            JsonSerializer.Serialize(writer, value, typeInfo);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public static T? Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(json, typeInfo);
}
