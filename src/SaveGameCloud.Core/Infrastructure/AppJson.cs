using System.Text.Json;
using System.Text.Json.Serialization;

namespace SaveGameCloud.Core.Infrastructure;

/// <summary>Options JSON communes (config locale et bibliothèque cloud).</summary>
public static class AppJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
