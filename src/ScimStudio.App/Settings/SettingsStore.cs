using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScimStudio.App.Settings;

/// <summary>Reads and writes the settings file. A file that cannot be read is set aside rather than lost, and the tool starts fresh.</summary>
/// <param name="path">Where the file lives.</param>
public sealed class SettingsStore(string path) {
    private static readonly JsonSerializerOptions Options = new() {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScimStudio", "settings.json");

    public string Path { get; } = path;

    public AppSettings Load() {
        if (!File.Exists(Path)) {
            return new AppSettings();
        }

        try {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new AppSettings();
        } catch (Exception failure) when (failure is JsonException or IOException or UnauthorizedAccessException) {
            TrySetAside();
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings through a file beside them, so a crash halfway leaves the old ones intact.</summary>
    /// <param name="settings">The settings.</param>
    public void Save(AppSettings settings) {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);

        var temporary = $"{Path}.tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));

        // Tokens outside Windows are only as safe as the file; nobody but the owner reads it.
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, Path, overwrite: true);
    }

    private void TrySetAside() {
        try {
            File.Move(Path, $"{Path}.broken", overwrite: true);
        } catch (IOException) {
            // Left where it is; the next save replaces it.
        }
    }
}

/// <summary>
/// Keeps a token out of the settings file in readable form where the platform allows: on Windows it is encrypted for the signed-in user
/// (DPAPI). Elsewhere it is stored as it is, in a file only its owner may read, which the README says.
/// </summary>
public static class TokenVault {
    private const string PROTECTED = "dpapi:";
    private const string PLAIN = "plain:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScimStudio token");

    public static bool Encrypts => OperatingSystem.IsWindows();

    public static string Protect(string token) {
        ArgumentNullException.ThrowIfNull(token);

        if (OperatingSystem.IsWindows()) {
            var sealedToken = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser);
            return PROTECTED + Convert.ToBase64String(sealedToken);
        }

        return PLAIN + token;
    }

    /// <summary>The token, or null when there is none or it cannot be opened here - a settings file copied from another account, say.</summary>
    /// <param name="stored">The token as stored.</param>
    public static string? Unprotect(string? stored) {
        if (string.IsNullOrEmpty(stored)) {
            return null;
        }

        if (stored.StartsWith(PLAIN, StringComparison.Ordinal)) {
            return stored[PLAIN.Length..];
        }

        if (!stored.StartsWith(PROTECTED, StringComparison.Ordinal) || !OperatingSystem.IsWindows()) {
            return null;
        }

        try {
            var opened = ProtectedData.Unprotect(Convert.FromBase64String(stored[PROTECTED.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(opened);
        } catch (Exception failure) when (failure is CryptographicException or FormatException) {
            return null;
        }
    }
}
