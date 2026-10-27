using System.Security;
using System.Text.Json;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class SteamLibrarySettings
    {
        public string? SteamRoot { get; private init; }
        public IReadOnlyList<string> ExtraLibraryFolders { get; private init; } = [];
        public string? Error { get; private init; }

        public static SteamLibrarySettings Load(string? dataDirectory)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) return new();
            try
            {
                var path = Path.Combine(dataDirectory, "settings.json");
                if (!File.Exists(path)) return new();
                if (new FileInfo(path).Length > 1024 * 1024)
                    return Invalid("The plugin settings exceed 1 MiB. Open Configure and reset them.");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || root.EnumerateObject().Any(field => field.Name is not ("steamRoot" or "extraLibraryFolders"))
                    || root.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
                    return Invalid("Plugin settings must contain only steamRoot and extraLibraryFolders.");
                string? steamRoot = null;
                if (root.TryGetProperty("steamRoot", out var configured))
                {
                    if (configured.ValueKind != JsonValueKind.String)
                        return Invalid("steamRoot must be a string.");
                    var value = configured.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) steamRoot = NormalizeLibrary(value);
                }
                var extra = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("extraLibraryFolders", out var folders))
                {
                    if (folders.ValueKind != JsonValueKind.Array || folders.GetArrayLength() > SteamCatalogReader.MaximumLibraries)
                        return Invalid("extraLibraryFolders must be an array of at most 64 local library paths.");
                    foreach (var folder in folders.EnumerateArray())
                    {
                        if (folder.ValueKind != JsonValueKind.String)
                            return Invalid("Every extraLibraryFolders entry must be a string.");
                        extra.Add(NormalizeLibrary(folder.GetString()!));
                    }
                }
                return new() { SteamRoot = steamRoot, ExtraLibraryFolders = [.. extra] };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
                or JsonException or ArgumentException or NotSupportedException or FormatException)
            {
                return Invalid("Steam paths must be absolute, accessible local folders containing steamapps. Open Configure to correct the settings.");
            }
        }

        public static void WriteDetectedDefaults(string? dataDirectory, string? detectedRoot)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) return;
            try
            {
                var root = "";
                if (!string.IsNullOrWhiteSpace(detectedRoot))
                {
                    try { root = NormalizeLibrary(detectedRoot); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
                        or ArgumentException or NotSupportedException or FormatException) { }
                }
                Directory.CreateDirectory(dataDirectory);
                var path = Path.Combine(dataDirectory, "defaults.json");
                var temporary = path + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new { steamRoot = root, extraLibraryFolders = Array.Empty<string>() }));
                    File.Move(temporary, path, true);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException
                or ArgumentException or NotSupportedException)
            {
                // Display defaults are optional; failure must not disable discovery or modify saved settings.
            }
        }

        private static string NormalizeLibrary(string value)
        {
            if (value.Length > 4096 || !Path.IsPathFullyQualified(value) || value.Any(char.IsControl)
                || value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
                throw new FormatException("An absolute local library path is required.");
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            if (SteamCatalogReader.IsNetworkPath(path) || !Directory.Exists(Path.Combine(path, "steamapps")))
                throw new FormatException("The local Steam library is unavailable.");
            _ = Directory.EnumerateFileSystemEntries(Path.Combine(path, "steamapps")).Take(1).ToArray();
            return path;
        }

        private static SteamLibrarySettings Invalid(string error) => new() { Error = error };
    }
}
