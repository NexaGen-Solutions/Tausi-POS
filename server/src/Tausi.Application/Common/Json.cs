using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tausi.Application.Common;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, Options);
}
