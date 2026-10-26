using System.Text.Json;
using FocusRgb.Contracts;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class SteamCatalogReader(
        ISteamRootLocator rootLocator,
        TimeSpan? scanBudget = null,
        string? dataDirectory = null) : IDisposable
    {
        private const int MaximumLibraries = 64;
        private const int MaximumManifests = 4096;
        private const int MaximumExecutablesPerGame = 256;
        private const int MaximumScanEntries = 10000;
        private const int MaximumManifestBytes = 2 * 1024 * 1024;
        private const int MaximumLibraryFileBytes = 4 * 1024 * 1024;
        private const int MaximumExecutableDepth = 8;
        private const int MaximumCacheBytes = 16 * 1024 * 1024;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
        private static readonly string[] ExcludedDirectories =
            ["_CommonRedist", "redist", "redistributables", "installers", "support"];
        private static readonly string[] ExcludedExecutablePrefixes =
            ["unitycrashhandler", "crashreportclient", "crashpad_handler", "vc_redist", "vcredist", "dotnetfx", "ueprereqsetup", "installer", "easyanticheat", "beservice"];
        private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, PluginCatalogItem> _lastComplete = new(StringComparer.Ordinal);
        private readonly object _cacheGate = new();
        private readonly object _scanGate = new();
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TimeSpan _scanBudget = scanBudget ?? TimeSpan.FromSeconds(3);
        private Task? _scanTask;
        private bool _incomplete;
        private bool _disposed;
        private bool _persistedLoaded;
        private bool _scanFailed;

        public bool IsScanIncomplete => Volatile.Read(ref _incomplete);

        public bool IsInstalled => TryGetSteamRoot(out _);

        public async Task<IReadOnlyList<PluginCatalogItem>> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task scan;
            lock (_scanGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_scanTask is null || _scanTask.IsCompleted)
                {
                    Volatile.Write(ref _incomplete, true);
                    var lifetimeToken = _lifetime.Token;
                    _scanTask = Task.Run(() => ScanInBackgroundAsync(lifetimeToken), CancellationToken.None);
                }
                scan = _scanTask;
            }
            try
            {
                // Filesystem calls can block without honoring cancellation; never block catalog/list with them.
                await scan.WaitAsync(_scanBudget, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            lock (_cacheGate)
            {
                var games = new Dictionary<string, PluginCatalogItem>(_lastComplete, StringComparer.Ordinal);
                foreach (var entry in _cache.Values) games[entry.Game.ResourceId] = entry.Game;
                return [.. games.Values.OrderBy(game => game.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(game => game.ResourceId, StringComparer.Ordinal)];
            }
        }

        public void Dispose()
        {
            lock (_scanGate)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                _lifetime.Dispose();
            }
        }

        private async Task ScanInBackgroundAsync(CancellationToken lifetimeToken)
        {
            try
            {
                if (!_persistedLoaded)
                {
                    await LoadPersistedCatalogAsync(lifetimeToken).ConfigureAwait(false);
                    _persistedLoaded = true;
                }
                _scanFailed = false;
                await ScanAsync(lifetimeToken).ConfigureAwait(false);
                if (!_scanFailed)
                {
                    List<PluginCatalogItem> complete;
                    lock (_cacheGate)
                    {
                        _lastComplete = _cache.Values.Select(entry => entry.Game)
                            .GroupBy(game => game.ResourceId, StringComparer.Ordinal)
                            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
                        complete = [.. _lastComplete.Values];
                    }
                    await PersistCatalogAsync(complete, lifetimeToken).ConfigureAwait(false);
                    Volatile.Write(ref _incomplete, false);
                }
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                // Shutdown is the only cancellation of shared scanning; callers cancel their own waits.
            }
        }

        private async Task ScanAsync(CancellationToken cancellationToken)
        {
            if (!TryGetSteamRoot(out var steamRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _scanFailed = true;
                return;
            }

            var seenManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var library in await ReadLibraryRootsAsync(steamRoot, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var steamApps = Path.Combine(library, "steamapps");
                string[] manifests;
                try
                {
                    manifests = [.. Directory.EnumerateFiles(
                        steamApps, "appmanifest_*.acf", SearchOption.TopDirectoryOnly)
                        .Select(path => { cancellationToken.ThrowIfCancellationRequested(); return path; })
                        .Take(MaximumManifests - seenManifests.Count + 1)];
                    if (manifests.Length > MaximumManifests - seenManifests.Count)
                    {
                        _scanFailed = true;
                        manifests = [.. manifests.Take(MaximumManifests - seenManifests.Count)];
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _scanFailed = true;
                    continue;
                }

                foreach (var manifestPath in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    seenManifests.Add(manifestPath);
                    var game = await ReadGameAsync(steamApps, manifestPath, cancellationToken).ConfigureAwait(false);
                    if (game is null)
                    {
                        if (!_scanFailed) { lock (_cacheGate) _cache.Remove(manifestPath); }
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_scanFailed) lock (_cacheGate)
            {
                foreach (var removed in _cache.Keys.Except(seenManifests, StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    _cache.Remove(removed);
                }
            }

        }

        private bool TryGetSteamRoot(out string steamRoot)
        {
            steamRoot = rootLocator.FindSteamRoot() ?? string.Empty;
            return steamRoot.Length > 0 && !IsNetworkPath(steamRoot)
                && Directory.Exists(Path.Combine(steamRoot, "steamapps"));
        }

        private async Task<IReadOnlyList<string>> ReadLibraryRootsAsync(
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
                var document = VdfParser.Parse(await ReadBoundedTextAsync(
                    path, MaximumLibraryFileBytes, cancellationToken).ConfigureAwait(false));
                var libraries = document.GetObject("libraryfolders");
                if (libraries is null)
                {
                    _scanFailed = true;
                    return [.. roots];
                }

                foreach (var entry in libraries.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var libraryPath = entry.Value switch
                    {
                        string value => value,
                        VdfObject value => value.GetString("path"),
                        _ => null
                    };
                    if (!string.IsNullOrWhiteSpace(libraryPath))
                    {
                        var fullPath = Path.GetFullPath(libraryPath);
                        if (!IsNetworkPath(fullPath) && !roots.Contains(fullPath))
                        {
                            if (roots.Count < MaximumLibraries) roots.Add(fullPath);
                            else _scanFailed = true;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or FormatException)
            {
                _scanFailed = true;
            }

            return [.. roots];
        }

        private async Task<PluginCatalogItem?> ReadGameAsync(
            string steamApps,
            string manifestPath,
            CancellationToken cancellationToken)
        {
            try
            {
                var manifest = new FileInfo(manifestPath);
                var stamp = manifest.LastWriteTimeUtc.Ticks;
                lock (_cacheGate)
                {
                    if (_cache.TryGetValue(manifestPath, out var cached)
                        && cached.LastWriteTicks == stamp
                        && cached.Length == manifest.Length
                        && DateTimeOffset.UtcNow - cached.CachedAt < CacheLifetime)
                    {
                        return cached.Game;
                    }
                }
                var document = VdfParser.Parse(
                    await ReadBoundedTextAsync(
                        manifestPath, MaximumManifestBytes, cancellationToken).ConfigureAwait(false));
                var state = document.GetObject("AppState");
                var appId = state?.GetString("appid");
                var name = state?.GetString("name");
                var installDirectoryName = state?.GetString("installdir");
                if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)
                    || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDirectoryName))
                {
                    return null;
                }

                if (installDirectoryName is "." or ".."
                    || Path.IsPathRooted(installDirectoryName)
                    || Path.GetFileName(installDirectoryName) != installDirectoryName)
                {
                    return null;
                }

                var common = Path.GetFullPath(Path.Combine(steamApps, "common"));
                var installDirectory = Path.GetFullPath(Path.Combine(common, installDirectoryName));
                if (!IsContainedBy(common, installDirectory))
                {
                    return null;
                }

                var directoryExists = Directory.Exists(installDirectory);
                var executables = directoryExists ? ReadExecutables(installDirectory, cancellationToken) : [];
                var available = executables.Count > 0;
                cancellationToken.ThrowIfCancellationRequested();
                var game = new PluginCatalogItem
                {
                    ResourceId = appId,
                    DisplayName = name,
                    Available = available,
                    Reason = available ? null : MissingGameReason(directoryExists),
                    Game = new PluginGameCatalogMetadata
                    {
                        Executables = executables
                    }
                };
                lock (_cacheGate)
                {
                    if (_cache.Count >= MaximumManifests)
                    {
                        _cache.Remove(_cache.MinBy(entry => entry.Value.CachedAt).Key);
                    }
                    _cache[manifestPath] = new CacheEntry(stamp, manifest.Length, DateTimeOffset.UtcNow, game);
                }
                return game;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or FormatException)
            {
                _scanFailed = true;
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
                    MaxRecursionDepth = MaximumExecutableDepth,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive
                };
                var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in Directory.EnumerateFileSystemEntries(installDirectory, "*", options)
                    .Take(MaximumScanEntries))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path))
                    {
                        continue;
                    }
                    var relative = Path.GetRelativePath(installDirectory, path);
                    if (IsExcludedExecutable(relative))
                    {
                        continue;
                    }
                    executables.Add(Path.GetFileName(path));
                    if (executables.Count >= MaximumExecutablesPerGame)
                    {
                        break;
                    }
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

        private static bool IsExcludedExecutable(string relativePath)
        {
            var segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Take(segments.Length - 1).Any(segment =>
                ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            {
                return true;
            }
            var name = Path.GetFileName(relativePath);
            return ExcludedExecutablePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || name.Equals("dxsetup.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("setup.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("unins", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNetworkPath(string path)
        {
            if (new Uri(path).IsUnc) return true;
            try
            {
                var root = Path.GetPathRoot(path);
                return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private static async Task<string> ReadBoundedTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximumBytes) throw new FormatException("The Steam metadata file is too large.");
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maximumBytes + 1];
            var count = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count > maximumBytes) throw new FormatException("The Steam metadata file is too large.");
            return new string(buffer, 0, count);
        }

        private static PluginReason MissingGameReason(bool directoryExists) => directoryExists
            ? new PluginReason
            {
                Code = "no-executables-found",
                Message = "Steam lists the game, but no Windows executable was found in its installation directory."
            }
            : new PluginReason
            {
                Code = "game-files-missing",
                Message = "Steam lists the game, but its installation directory is unavailable."
            };

        private async Task LoadPersistedCatalogAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) return;
            try
            {
                var path = Path.Combine(dataDirectory, "catalog-cache.json");
                if (!File.Exists(path) || new FileInfo(path).Length > MaximumCacheBytes) return;
                var saved = JsonSerializer.Deserialize<PersistedCatalog>(
                    await ReadBoundedTextAsync(path, MaximumCacheBytes, cancellationToken).ConfigureAwait(false), PluginJson.Options);
                if (saved?.SchemaVersion != 1 || saved.Items is null || saved.Items.Count > MaximumManifests
                    || saved.Items.Any(item => !IsValidCachedGame(item))
                    || saved.Items.Select(item => item.ResourceId).Distinct(StringComparer.Ordinal).Count() != saved.Items.Count) return;
                lock (_cacheGate)
                    _lastComplete = saved.Items.ToDictionary(item => item.ResourceId, StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
            {
                // A disposable cache must never prevent discovery or expose unvalidated data.
            }
        }

        private async Task PersistCatalogAsync(List<PluginCatalogItem> games, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(dataDirectory)) return;
            var path = Path.Combine(dataDirectory, "catalog-cache.json");
            var temporary = path + ".tmp";
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new PersistedCatalog { SchemaVersion = 1, Items = games }, PluginJson.Options);
                if (bytes.Length > MaximumCacheBytes) return;
                Directory.CreateDirectory(dataDirectory);
                await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("The completed Steam catalog could not be cached for the next startup.");
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }

        private static bool IsValidCachedGame(PluginCatalogItem? game) => game is not null
            && !string.IsNullOrWhiteSpace(game.ResourceId) && game.ResourceId.Length <= 128
            && game.ResourceId.All(char.IsAsciiDigit)
            && !string.IsNullOrWhiteSpace(game.DisplayName) && game.DisplayName.Length <= 128
            && !game.DisplayName.Any(char.IsControl)
            && game.Game?.Executables is { Count: <= MaximumExecutablesPerGame } executables
            && executables.Distinct(StringComparer.OrdinalIgnoreCase).Count() == executables.Count
            && executables.All(name => !string.IsNullOrWhiteSpace(name) && name.Length <= 260
                && !name.Any(char.IsControl) && !name.Contains('/') && !name.Contains('\\')
                && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        private sealed class PersistedCatalog
        {
            public int SchemaVersion { get; init; }
            public List<PluginCatalogItem>? Items { get; init; }
        }

        private sealed record CacheEntry(
            long LastWriteTicks,
            long Length,
            DateTimeOffset CachedAt,
            PluginCatalogItem Game);
    }
}
