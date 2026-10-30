using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VsChatHistoryToolkit;

public static class JsonOut
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    public static string Error(ToolkitException ex) => JsonSerializer.Serialize(new
    {
        ok = false,
        coverage = Caps.Coverage,
        error = new { code = ex.Code, message = ex.Message },
    }, Options);

    public static string Error(string code, string message) => JsonSerializer.Serialize(new
    {
        ok = false,
        coverage = Caps.Coverage,
        error = new { code, message },
    }, Options);
}
