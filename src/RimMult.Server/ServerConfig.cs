using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RimMult.ServerCore;
using RimMult.Shared;

namespace RimMult.Server;

/// <summary>Contents of <c>server.json</c>.</summary>
public sealed class ServerConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public int Port { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>Where the shared world (planet, colonies, world time) is kept between restarts.</summary>
    public string WorldFile { get; set; } = "world.dat";

    public ServerSettings Server { get; set; } = new();

    public static ServerConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ServerConfig>(stream, JsonOptions)
               ?? throw new InvalidDataException($"{path} is empty");
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
