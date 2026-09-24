using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContosoPizza.Probe.Shipping;

/// <summary>
/// A durable id for this probe installation.
///
/// Without one, a restarted probe is indistinguishable from a newly installed
/// one, and the collector's fleet list grows a fresh entry every time a server
/// reboots. The server name alone will not do: two machines can be cloned from
/// the same image and arrive with the same name.
/// </summary>
public sealed record ProbeIdentity
{
    [JsonPropertyName("probeId")]
    public required string ProbeId { get; init; }

    [JsonPropertyName("machineName")]
    public required string MachineName { get; init; }

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string DefaultStateDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ContosoProbe");

    public static ProbeIdentity LoadOrCreate(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, "probe-identity.json");

        if (File.Exists(path))
        {
            try
            {
                var existing = JsonSerializer.Deserialize<ProbeIdentity>(
                    File.ReadAllText(path), SerializerOptions);

                if (existing is { ProbeId.Length: > 0 })
                {
                    return existing;
                }
            }
            catch (JsonException)
            {
                // A corrupt identity file is recoverable by minting a new id; a
                // probe that refuses to start is not.
            }
        }

        var identity = new ProbeIdentity
        {
            ProbeId = Guid.NewGuid().ToString("n"),
            MachineName = Environment.MachineName,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(identity, SerializerOptions));
        return identity;
    }
}
