using System.Text.Json;
using FocusRgb.Contracts;

namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class SteamLibrarySettingsTests : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "focusrgb-steam-settings-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Load_EmptyDefaultsKeepAutomaticDetection()
        {
            var settings = SteamLibrarySettings.Load(_directory);
            Assert.Null(settings.Error);
            Assert.Null(settings.SteamRoot);
            Assert.Empty(settings.ExtraLibraryFolders);
        }

        [Fact]
        public async Task Plugin_ConfiguredRootAndExtraFoldersDiscoverGamesWithoutRegistry()
        {
            var primary = Library("Primary", "570", "First");
            var extra = Library("Extra", "730", "Second");
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(new
            {
                steamRoot = primary, extraLibraryFolders = new[] { primary, extra, extra.ToUpperInvariant() }
            }));
            var settings = SteamLibrarySettings.Load(_directory);
            Assert.Null(settings.Error);
            Assert.Equal(2, settings.ExtraLibraryFolders.Count);
            using var plugin = new SteamPlugin(new ThrowingLocator(), dataDirectory: _directory);

            var catalog = await plugin.ListCatalogAsync(new CatalogListParameters { Kind = "game" }, TestContext.Current.CancellationToken);

            Assert.Equal(["570", "730"], catalog.Items.Select(item => item.ResourceId).Order());
            Assert.All(catalog.Items, item => Assert.True(item.Available));
            Assert.NotNull(catalog.RefreshedAt);
        }

        [Fact]
        public async Task Plugin_ExtraLibrariesWorkWhenAutomaticDetectionFindsNothing()
        {
            var extra = Library("Extra", "570", "Extra");
            File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(new
            {
                steamRoot = "", extraLibraryFolders = new[] { extra }
            }));
            using var plugin = new SteamPlugin(new EmptyLocator(), dataDirectory: _directory);

            var catalog = await plugin.ListCatalogAsync(new CatalogListParameters { Kind = "game" }, TestContext.Current.CancellationToken);

            Assert.Equal("570", Assert.Single(catalog.Items).ResourceId);
        }

        [Theory]
        [InlineData("{broken")]
        [InlineData("{\"steamRoot\":1}")]
        [InlineData("{\"steamRoot\":\"relative/path\"}")]
        [InlineData("{\"steamRoot\":\"//server/share\"}")]
        [InlineData("{\"extraLibraryFolders\":{}}")]
        [InlineData("{\"extraLibraryFolders\":[null]}")]
        [InlineData("{\"extraLibraryFolders\":[\"relative\"]}")]
        [InlineData("{\"unknown\":true}")]
        public async Task Plugin_InvalidSettingsDisableCatalogWithActionableReason(string json)
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "settings.json"), json);
            Assert.NotNull(SteamLibrarySettings.Load(_directory).Error);
            using var plugin = new SteamPlugin(new ThrowingLocator(), dataDirectory: _directory);

            var status = await plugin.GetStatusAsync(TestContext.Current.CancellationToken);
            var catalog = await plugin.ListCatalogAsync(new CatalogListParameters { Kind = "game" }, TestContext.Current.CancellationToken);

            Assert.Equal("unavailable", status.Availability);
            Assert.Equal("capability-unsupported", status.Reason!.Code);
            Assert.Equal("unsupported", catalog.Support);
            Assert.NotEmpty(catalog.Reason!.Message);
        }

        [Fact]
        public void Load_RejectsTooManyLibrariesAndOversizedSettings()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "settings.json"), JsonSerializer.Serialize(new
            {
                extraLibraryFolders = Enumerable.Repeat("relative", 65).ToArray()
            }));
            Assert.Contains("64", SteamLibrarySettings.Load(_directory).Error!, StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(_directory, "settings.json"), new string(' ', 1024 * 1024 + 1));
            Assert.Contains("1 MiB", SteamLibrarySettings.Load(_directory).Error!, StringComparison.Ordinal);
        }

        [Fact]
        public void PublishedSettingsSchema_UsesEmptyDefaultsAndSupportedHostFieldTypes()
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "focusrgb-settings.schema.json")));
            Assert.Equal(1, schema.RootElement.GetProperty("schemaVersion").GetInt32());
            var fields = schema.RootElement.GetProperty("fields").EnumerateArray().ToDictionary(field => field.GetProperty("id").GetString()!);
            Assert.Equal("string", fields["steamRoot"].GetProperty("type").GetString());
            Assert.Equal("", fields["steamRoot"].GetProperty("default").GetString());
            Assert.Equal("json", fields["extraLibraryFolders"].GetProperty("type").GetString());
            Assert.Empty(fields["extraLibraryFolders"].GetProperty("default").EnumerateArray());
        }

        private string Library(string directory, string appId, string name)
        {
            var root = Path.Combine(_directory, directory);
            var apps = Path.Combine(root, "steamapps");
            var install = Path.Combine(apps, "common", name);
            Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(apps, "appmanifest_" + appId + ".acf"),
                $"\"AppState\" {{ \"appid\" \"{appId}\" \"name\" \"{name}\" \"installdir\" \"{name}\" }}");
            File.WriteAllBytes(Path.Combine(install, name + ".exe"), []);
            return root;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }

        private sealed class ThrowingLocator : ISteamRootLocator
        {
            public string? FindSteamRoot() => throw new InvalidOperationException("Explicit root or invalid settings must not query automatic detection.");
        }

        private sealed class EmptyLocator : ISteamRootLocator
        {
            public string? FindSteamRoot() => null;
        }
    }
}
