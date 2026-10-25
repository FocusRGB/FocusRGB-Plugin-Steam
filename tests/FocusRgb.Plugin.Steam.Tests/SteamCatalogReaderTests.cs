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
        public async Task ReadAsync_ReturnsEmpty_WhenSteamIsUnavailable()
        {
            var reader = new SteamCatalogReader(new StubLocator(null));

            Assert.False(reader.IsInstalled);
            Assert.Empty(await reader.ReadAsync(TestContext.Current.CancellationToken));
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
    }
}
