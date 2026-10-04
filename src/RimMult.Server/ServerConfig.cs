using System;
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

    /// <summary>A copy of the world goes into <see cref="BackupDirectory"/> this often (0: never).</summary>
    public int BackupIntervalMinutes { get; set; } = 30;

    /// <summary>Older backups beyond this many are deleted.</summary>
    public int BackupsToKeep { get; set; } = 48;

    public string BackupDirectory { get; set; } = "backups";

    public ServerSettings Server { get; set; } = new();

    /// <summary>
    /// Environment variables win over the file (handy in Docker): RIMMULT_PORT, RIMMULT_NAME, RIMMULT_PASSWORD,
    /// RIMMULT_MAXPLAYERS, RIMMULT_PVP. They are not written back into the file.
    /// </summary>
    public void ApplyEnvironment(Func<string, string?> get)
    {
        if (int.TryParse(get("RIMMULT_PORT"), out var port))
            Port = port;
        if (get("RIMMULT_NAME") is { Length: > 0 } name)
            Server.Name = name;
        if (get("RIMMULT_PASSWORD") is { } password)
            Server.Password = password.Length == 0 ? null : password;
        if (int.TryParse(get("RIMMULT_MAXPLAYERS"), out var max))
            Server.MaxPlayers = max;
        if (bool.TryParse(get("RIMMULT_PVP"), out var pvp))
            Server.AllowPvp = pvp;
    }

    public static ServerConfig Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<ServerConfig>(stream, JsonOptions)
               ?? throw new InvalidDataException($"{path} is empty");
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
