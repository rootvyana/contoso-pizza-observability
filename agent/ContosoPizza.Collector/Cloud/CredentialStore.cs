using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ContosoPizza.Collector.Cloud;

/// <summary>
/// What the collector knows about itself after enrollment, on disk.
///
/// The credential this holds never expires, so it is the one secret worth
/// protecting properly: it is written DPAPI-encrypted under the local machine
/// scope, which means a copy of the state directory taken to another box
/// decrypts to nothing. That is a real difference from an environment variable,
/// which is readable by anything running as the same user and survives in
/// process dumps and crash reports.
///
/// Machine scope rather than user scope on purpose: the collector runs as a
/// Windows service, often under an account nobody logs in as, and a
/// user-scoped blob written by the installing administrator would be
/// undecryptable by the service that has to read it.
/// </summary>
public sealed record CollectorCredentials
{
    [JsonPropertyName("collectorId")]
    public required string CollectorId { get; init; }

    [JsonPropertyName("credential")]
    public required string Credential { get; init; }

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("enrolledAt")]
    public required DateTimeOffset EnrolledAt { get; init; }

    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }
}

public sealed class CredentialStore
{
    private const string FileName = "collector-credential.dat";
    private const string PlaintextFileName = "collector-credential.json";

    private readonly string _path;
    private readonly string _plaintextPath;
    private readonly ILogger<CredentialStore> _logger;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Ties the ciphertext to this purpose, so a blob lifted from here cannot be
    // fed to another DPAPI consumer on the same machine and decrypted.
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("ContosoPizza.Collector.credential.v1");

    public CredentialStore(string stateDirectory, ILogger<CredentialStore> logger)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, FileName);
        _plaintextPath = Path.Combine(stateDirectory, PlaintextFileName);
        _logger = logger;
    }

    public bool Exists => File.Exists(_path) || File.Exists(_plaintextPath);

    public CollectorCredentials? Load()
    {
        if (File.Exists(_path) && OperatingSystem.IsWindows())
        {
            try
            {
                return Deserialize(Unprotect(File.ReadAllBytes(_path)));
            }
            catch (CryptographicException ex)
            {
                // Happens when the state directory was copied from another
                // machine. Saying so is the whole value here: the alternative
                // is a collector that silently re-enrolls and appears in the
                // dashboard as a second, duplicate machine.
                _logger.LogError(ex,
                    "The stored credential could not be decrypted on this machine. If the state "
                  + "directory was copied from another server, delete {Path} and enroll again.",
                    _path);
                return null;
            }
        }

        if (File.Exists(_plaintextPath))
        {
            try
            {
                return Deserialize(File.ReadAllBytes(_plaintextPath));
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Stored credential at {Path} is not readable", _plaintextPath);
            }
        }

        return null;
    }

    public void Save(CollectorCredentials credentials)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials, Json);

        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(_path, Protect(plaintext));

            // An earlier unencrypted file would otherwise sit there holding a
            // credential that is still valid.
            if (File.Exists(_plaintextPath))
            {
                File.Delete(_plaintextPath);
            }

            return;
        }

        // Non-Windows is a development convenience only; the collector's kernel
        // source is Windows-only, so nothing ships this way.
        _logger.LogWarning(
            "DPAPI is unavailable on this platform; the credential is stored unencrypted at {Path}",
            _plaintextPath);
        File.WriteAllBytes(_plaintextPath, plaintext);
    }

    public void Clear()
    {
        foreach (string path in new[] { _path, _plaintextPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static CollectorCredentials? Deserialize(byte[] bytes) =>
        JsonSerializer.Deserialize<CollectorCredentials>(bytes, Json);

    [SupportedOSPlatform("windows")]
    private static byte[] Protect(byte[] plaintext) =>
        ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);

    [SupportedOSPlatform("windows")]
    private static byte[] Unprotect(byte[] ciphertext) =>
        ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.LocalMachine);
}
