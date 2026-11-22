using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

public static class JsonOptions
{
    public static JsonSerializerOptions ForProject { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters =
            {
                // Specialized enum converters must precede the catch-all JsonStringEnumConverter.
                new TransitionTypeJsonConverter(),
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
                new MediaSourceJsonConverter(),
            },
        };
        return o;
    }
}
