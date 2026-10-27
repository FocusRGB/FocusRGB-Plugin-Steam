namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class SteamCatalogReaderTests : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), $"focusrgb-steam-tests-{Guid.NewGuid():N}");

        [Fact]
        public async Task ReadAsync_ReturnsScopedManifestIdentityAndUniqueExecutableFileNames()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            var secondLibrary = Path.Combine(_directory, "Library");
            CopyFixture("appmanifest_570.acf", Path.Combine(steamRoot, "steamapps"));
            CopyFixture("appmanifest_730.acf", Path.Combine(secondLibrary, "steamapps"));
            WriteLibraryFolders(steamRoot, secondLibrary);
            Touch(Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota", "game.exe"));
            Touch(Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota", "bin", "GAME.EXE"));
            Touch(Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota", "UnityCrashHandler64.exe"));
            Touch(Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota", "_CommonRedist", "vc_redist.x64.exe"));
            Touch(Path.Combine(secondLibrary, "steamapps", "common", "Synthetic Counter-Strike", "cs2.exe"));

            var games = await new SteamCatalogReader(new StubLocator(steamRoot))
                .ReadAsync(TestContext.Current.CancellationToken);

            Assert.Collection(
                games,
                game =>
                {
                    Assert.Equal("730", game.ResourceId);
                    Assert.Equal("Synthetic Counter-Strike", game.DisplayName);
                    Assert.Equal(["cs2.exe"], game.Game!.Executables);
                    Assert.True(game.Available);
                },
                game =>
                {
                    Assert.Equal("570", game.ResourceId);
                    Assert.Equal("Synthetic Dota", game.DisplayName);
                    Assert.Equal(["game.exe"], game.Game!.Executables, StringComparer.OrdinalIgnoreCase);
                    Assert.True(game.Available);
                });
        }

        [Fact]
        public async Task ReadAsync_ReturnsUnavailableGame_WhenManifestInstallDirectoryIsMissing()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            CopyFixture("appmanifest_570.acf", Path.Combine(steamRoot, "steamapps"));

            var game = Assert.Single(await new SteamCatalogReader(new StubLocator(steamRoot))
                .ReadAsync(TestContext.Current.CancellationToken));

            Assert.False(game.Available);
            Assert.Empty(game.Game!.Executables);
            Assert.Equal("game-files-missing", game.Reason!.Code);
        }

        [Fact]
        public async Task ReadAsync_ReturnsUnavailableGame_WhenInstallDirectoryHasNoExecutables()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            CopyFixture("appmanifest_570.acf", Path.Combine(steamRoot, "steamapps"));
            Directory.CreateDirectory(
                Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota"));

            var game = Assert.Single(await new SteamCatalogReader(new StubLocator(steamRoot))
                .ReadAsync(TestContext.Current.CancellationToken));

            Assert.False(game.Available);
            Assert.Empty(game.Game!.Executables);
            Assert.Equal("no-executables-found", game.Reason!.Code);
        }

        [Fact]
        public async Task ReadAsync_IgnoresMalformedAndEscapingManifests()
        {
            var steamApps = Path.Combine(_directory, "Steam", "steamapps");
            Directory.CreateDirectory(steamApps);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_1.acf"),
                "\"AppState\" {",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_2.acf"),
                "\"AppState\" { \"appid\" \"2\" \"name\" \"Escape\" \"installdir\" \"..\\\\outside\" }",
                TestContext.Current.CancellationToken);

            var games = await new SteamCatalogReader(new StubLocator(Path.Combine(_directory, "Steam")))
                .ReadAsync(TestContext.Current.CancellationToken);

            Assert.Empty(games);
        }

        [Fact]
        public async Task ReadAsync_IgnoresDotInstallDirectoryAndOversizedManifest()
        {
            var steamApps = Path.Combine(_directory, "Steam", "steamapps");
            Directory.CreateDirectory(steamApps);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_1.acf"),
                "\"AppState\" { \"appid\" \"1\" \"name\" \"All games\" \"installdir\" \".\" }",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(steamApps, "appmanifest_2.acf"),
                new string('x', 2 * 1024 * 1024 + 1),
                TestContext.Current.CancellationToken);

            var games = await new SteamCatalogReader(new StubLocator(Path.Combine(_directory, "Steam")))
                .ReadAsync(TestContext.Current.CancellationToken);

            Assert.Empty(games);
        }

        [Fact]
        public async Task ReadAsync_BoundsExecutableCountAndDepth_WithNonAsciiPath()
        {
            var steamRoot = Path.Combine(_directory, "Stéam");
            CopyFixture("appmanifest_570.acf", Path.Combine(steamRoot, "steamapps"));
            var install = Path.Combine(steamRoot, "steamapps", "common", "Synthetic Dota");
            for (var index = 0; index < 300; index++)
            {
                Touch(Path.Combine(install, $"game-{index:D3}.exe"));
            }
            var deep = install;
            for (var depth = 0; depth < 10; depth++) deep = Path.Combine(deep, $"d{depth}");
            Touch(Path.Combine(deep, "too-deep.exe"));

            var game = Assert.Single(await new SteamCatalogReader(new StubLocator(steamRoot))
                .ReadAsync(TestContext.Current.CancellationToken));

            Assert.True(game.Available);
            Assert.Equal(256, game.Game!.Executables.Count);
            Assert.DoesNotContain("too-deep.exe", game.Game.Executables);
        }

        [Fact]
        public async Task ReadAsync_ReturnsEmpty_WhenSteamIsUnavailable()
        {
            var reader = new SteamCatalogReader(new StubLocator(null));

            Assert.False(reader.IsInstalled);
            Assert.Empty(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task ReadAsync_ReusesUnchangedManifestAndInvalidatesChangedManifest()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            CopyFixture("appmanifest_570.acf", steamApps);
            var install = Path.Combine(steamApps, "common", "Synthetic Dota");
            Touch(Path.Combine(install, "game.exe"));
            var reader = new SteamCatalogReader(new StubLocator(steamRoot));
            var original = Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Touch(Path.Combine(install, "new-game.exe"));

            var cached = Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));

            Assert.Same(original, cached);
            Assert.DoesNotContain("new-game.exe", cached.Game!.Executables);
            await File.AppendAllTextAsync(Path.Combine(steamApps, "appmanifest_570.acf"),
                "\n// changed", TestContext.Current.CancellationToken);
            var refreshed = Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Contains("new-game.exe", refreshed.Game!.Executables);
        }

        [Fact]
        public async Task ReadAsync_ReturnsWithinBudgetAndSharesBlockedScan_WithConcurrentRequests()
        {
            using var locator = new BlockingLocator(null);
            using var reader = new SteamCatalogReader(locator, TimeSpan.FromMilliseconds(100));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var first = reader.ReadAsync(TestContext.Current.CancellationToken);
                Assert.True(locator.Entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                var second = reader.ReadAsync(TestContext.Current.CancellationToken);

                Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                Assert.Empty(await second.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
                Assert.True(reader.IsScanIncomplete);
                Assert.Equal(1, locator.Calls);
            }
            finally
            {
                locator.Release.Set();
            }
        }

        [Fact]
        public async Task ReadAsync_PreservesCachedGames_WhenRefreshExceedsBudget()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            CopyFixture("appmanifest_570.acf", Path.Combine(steamRoot, "steamapps"));
            using var locator = new BlockingLocator(steamRoot);
            locator.Release.Set();
            using var reader = new SteamCatalogReader(locator, TimeSpan.FromSeconds(1));
            var original = Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));
            locator.Release.Reset();
            try
            {
                var cached = Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));

                Assert.Same(original, cached);
                Assert.True(reader.IsScanIncomplete);
            }
            finally
            {
                reader.Dispose();
                locator.Release.Set();
            }
        }

        [Fact]
        public async Task ReadAsync_CancelsWaitingCaller_WithoutDuplicatingOrCancellingSharedScan()
        {
            using var locator = new BlockingLocator(null);
            using var reader = new SteamCatalogReader(locator, TimeSpan.FromMilliseconds(100));
            using var cancellation = new CancellationTokenSource();
            try
            {
                var waiting = reader.ReadAsync(cancellation.Token);
                Assert.True(locator.Entered.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

                Assert.Empty(await reader.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1, locator.Calls);
            }
            finally
            {
                locator.Release.Set();
            }
        }

        [Fact]
        public async Task ReadAsync_PrunesRemovedGames_OnlyAfterCompleteScan()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            CopyFixture("appmanifest_570.acf", steamApps);
            using var reader = new SteamCatalogReader(new StubLocator(steamRoot));
            Assert.Single(await reader.ReadAsync(TestContext.Current.CancellationToken));

            File.Delete(Path.Combine(steamApps, "appmanifest_570.acf"));

            Assert.Empty(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.False(reader.IsScanIncomplete);
        }

        [Fact]
        public async Task ReadAsync_ColdStartReturnsPersistedFullCatalog_WhileSlowScanContinuesWithoutAnotherRequest()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            CopyFixture("appmanifest_570.acf", steamApps);
            CopyFixture("appmanifest_730.acf", steamApps);
            var data = Path.Combine(_directory, "plugin-data");
            using (var first = new SteamCatalogReader(new StubLocator(steamRoot), dataDirectory: data))
            {
                Assert.Equal(2, (await first.ReadAsync(TestContext.Current.CancellationToken)).Count);
                Assert.False(first.IsScanIncomplete);
            }
            var persistedPath = Path.Combine(data, "catalog-cache.json");
            Assert.True(File.Exists(persistedPath));
            File.Delete(Path.Combine(steamApps, "appmanifest_730.acf"));
            using var locator = new BlockingLocator(steamRoot);
            using var restarted = new SteamCatalogReader(locator, TimeSpan.FromMilliseconds(100), data);
            try
            {
                var cold = await restarted.ReadAsync(TestContext.Current.CancellationToken);

                Assert.Equal(["570", "730"], cold.Select(game => game.ResourceId).Order());
                Assert.True(restarted.IsScanIncomplete);
                Assert.Equal(1, locator.Calls);
                locator.Release.Set();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                while (restarted.IsScanIncomplete) await Task.Delay(10, timeout.Token);

                // No second request triggered completion: the background scan reconciled and saved removals.
                using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(
                    persistedPath, TestContext.Current.CancellationToken));
                Assert.Equal("570", Assert.Single(saved.RootElement.GetProperty("items").EnumerateArray())
                    .GetProperty("resourceId").GetString());
                var complete = await restarted.ReadAsync(TestContext.Current.CancellationToken);
                Assert.Equal("570", Assert.Single(complete).ResourceId);
            }
            finally
            {
                restarted.Dispose();
                locator.Release.Set();
            }
        }

        [Theory]
        [InlineData("{broken")]
        [InlineData("{\"schemaVersion\":1,\"items\":[null]}")]
        [InlineData("{\"schemaVersion\":1,\"items\":[{\"resourceId\":\"570\",\"displayName\":\"Game\",\"available\":true,\"game\":{\"executables\":[\"C:/private/game.exe\"]}}]}")]
        public async Task ReadAsync_IgnoresInvalidPersistedCatalogWithoutFailingDiscovery(string json)
        {
            var data = Path.Combine(_directory, "plugin-data");
            Directory.CreateDirectory(data);
            await File.WriteAllTextAsync(Path.Combine(data, "catalog-cache.json"), json, TestContext.Current.CancellationToken);
            using var locator = new BlockingLocator(null);
            using var reader = new SteamCatalogReader(locator, TimeSpan.FromMilliseconds(100), data);
            try
            {
                Assert.Empty(await reader.ReadAsync(TestContext.Current.CancellationToken));
            }
            finally
            {
                reader.Dispose();
                locator.Release.Set();
            }
        }

        [Fact]
        public async Task ReadAsync_RetainsPersistedGames_WhenLibraryMetadataCannotBeRead()
        {
            var steamRoot = Path.Combine(_directory, "Steam");
            var steamApps = Path.Combine(steamRoot, "steamapps");
            CopyFixture("appmanifest_570.acf", steamApps);
            var data = Path.Combine(_directory, "plugin-data");
            using (var first = new SteamCatalogReader(new StubLocator(steamRoot), dataDirectory: data))
                Assert.Single(await first.ReadAsync(TestContext.Current.CancellationToken));
            File.Delete(Path.Combine(steamApps, "appmanifest_570.acf"));
            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), "{broken");
            using var restarted = new SteamCatalogReader(new StubLocator(steamRoot), dataDirectory: data);

            Assert.Single(await restarted.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(restarted.IsScanIncomplete);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, true);
            }
        }

        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

        private static void CopyFixture(string name, string destination)
        {
            Directory.CreateDirectory(destination);
            File.Copy(FixturePath(name), Path.Combine(destination, name));
        }

        private static void Touch(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
        }

        private static void WriteLibraryFolders(string steamRoot, string secondLibrary)
        {
            var text = File.ReadAllText(FixturePath("libraryfolders.vdf"))
                .Replace("__STEAM_ROOT__", Escape(steamRoot), StringComparison.Ordinal)
                .Replace("__SECOND_LIBRARY__", Escape(secondLibrary), StringComparison.Ordinal);
            var steamApps = Path.Combine(steamRoot, "steamapps");
            Directory.CreateDirectory(steamApps);
            File.WriteAllText(Path.Combine(steamApps, "libraryfolders.vdf"), text);
        }

        private static string Escape(string path) => path.Replace("\\", "\\\\", StringComparison.Ordinal);

        private sealed class StubLocator(string? path) : ISteamRootLocator
        {
            public string? FindSteamRoot() => path;
        }

        private sealed class BlockingLocator(string? path) : ISteamRootLocator, IDisposable
        {
            private int _calls;
            public ManualResetEventSlim Entered { get; } = new();
            public ManualResetEventSlim Release { get; } = new();
            public int Calls => Volatile.Read(ref _calls);

            public string? FindSteamRoot()
            {
                Interlocked.Increment(ref _calls);
                Entered.Set();
                Release.Wait();
                return path;
            }

            public void Dispose()
            {
                Release.Set();
                // The scan may still be returning from a blocked OS call; it owns its remaining lifetime.
            }
        }
    }
}
