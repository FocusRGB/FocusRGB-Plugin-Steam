using FocusRgb.Contracts;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class SteamCatalogReader(ISteamRootLocator rootLocator)
    {
        public bool IsInstalled => TryGetSteamRoot(out _);

        public async Task<IReadOnlyList<PluginCatalogItem>> ReadAsync(CancellationToken cancellationToken)
        {
            if (!TryGetSteamRoot(out var steamRoot))
            {
                return [];
            }

            var games = new Dictionary<string, PluginCatalogItem>(StringComparer.Ordinal);
            foreach (var library in await ReadLibraryRootsAsync(steamRoot, cancellationToken).ConfigureAwait(false))
            {
                var steamApps = Path.Combine(library, "steamapps");
                string[] manifests;
                try
                {
                    manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var manifestPath in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var game = await ReadGameAsync(steamApps, manifestPath, cancellationToken).ConfigureAwait(false);
                    if (game is not null)
                    {
                        games[game.ResourceId] = game;
                    }
                }
            }

            return [.. games.Values.OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.ResourceId, StringComparer.Ordinal)];
        }

        private bool TryGetSteamRoot(out string steamRoot)
        {
            steamRoot = rootLocator.FindSteamRoot() ?? string.Empty;
            return steamRoot.Length > 0 && Directory.Exists(Path.Combine(steamRoot, "steamapps"));
        }

        private static async Task<IReadOnlyList<string>> ReadLibraryRootsAsync(
            string steamRoot,
            CancellationToken cancellationToken)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(steamRoot) };
            var path = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(path))
            {
                return [.. roots];
            }

            try
            {
                var document = VdfParser.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
                var libraries = document.GetObject("libraryfolders");
                if (libraries is null)
                {
                    return [.. roots];
                }

                foreach (var entry in libraries.Values)
                {
                    var libraryPath = entry.Value switch
                    {
                        string value => value,
                        VdfObject value => value.GetString("path"),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(libraryPath))
                    {
                        roots.Add(Path.GetFullPath(libraryPath));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or FormatException)
            {
            }

            return [.. roots];
        }

        private static async Task<PluginCatalogItem?> ReadGameAsync(
            string steamApps,
            string manifestPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var document = VdfParser.Parse(
                    await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
                var state = document.GetObject("AppState");
                var appId = state?.GetString("appid");
                var name = state?.GetString("name");
                var installDirectoryName = state?.GetString("installdir");
                if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)
                    || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDirectoryName))
                {
                    return null;
                }

                var common = Path.GetFullPath(Path.Combine(steamApps, "common"));
                var installDirectory = Path.GetFullPath(Path.Combine(common, installDirectoryName));
                if (!IsContainedBy(common, installDirectory))
                {
                    return null;
                }

                var available = Directory.Exists(installDirectory);
                return new PluginCatalogItem
                {
                    ResourceId = appId,
                    DisplayName = name,
                    Available = available,
                    Reason = available ? null : new PluginReason
                    {
                        Code = "game-files-missing",
                        Message = "Steam lists the game, but its installation directory is unavailable."
                    },
                    Game = new PluginGameCatalogMetadata
                    {
                        Executables = available ? ReadExecutables(installDirectory, cancellationToken) : []
                    }
                };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or FormatException)
            {
                return null;
            }
        }

        private static IReadOnlyList<string> ReadExecutables(
            string installDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive
                };
                var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFiles(installDirectory, "*.exe", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    executables.Add(Path.GetFileName(path));
                }

                return [.. executables.Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static bool IsContainedBy(string parent, string path)
        {
            var relative = Path.GetRelativePath(parent, path);
            return !Path.IsPathRooted(relative)
                && relative != ".."
                && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
        }
    }
}
