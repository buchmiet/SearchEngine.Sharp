using System.Text.Json;
using System.Text.Json.Serialization;

namespace SearchEngine.CompetitorBenchmarks;

internal static class JsonConfig
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
