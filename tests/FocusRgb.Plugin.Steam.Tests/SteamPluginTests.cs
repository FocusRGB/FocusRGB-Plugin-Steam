using FocusRgb.Contracts;

namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class SteamPluginTests
    {
        [Fact]
        public async Task InitializeAsync_ReportsSteamIdentityAndGameCatalogCapability()
        {
            var plugin = new SteamPlugin(new StubLocator(null));

            var result = await plugin.InitializeAsync(new InitializeParameters
            {
                ContractVersion = ContractVersion.Current,
                Host = new PluginHostInfo { Name = "tests", Version = "1.0.0" }
            }, TestContext.Current.CancellationToken);

            Assert.Equal("steam", result.PluginId);
            Assert.Equal(ContractVersion.Current, result.ContractVersion);
            var capability = Assert.Single(result.Capabilities);
            Assert.Equal("game.catalog", capability.Id);
            Assert.Equal("supported", capability.Support);
        }

        [Fact]
        public async Task GetStatusAsync_ReportsUnavailable_WhenSteamIsMissing()
        {
            var result = await new SteamPlugin(new StubLocator(null))
                .GetStatusAsync(TestContext.Current.CancellationToken);

            Assert.Equal("unavailable", result.Availability);
            Assert.Equal("steam-not-installed", result.Reason!.Code);
        }

        [Fact]
        public async Task ListCatalogAsync_RejectsNonGameKinds()
        {
            var result = await new SteamPlugin(new StubLocator(null)).ListCatalogAsync(
                new CatalogListParameters { Kind = "effect" }, TestContext.Current.CancellationToken);

            Assert.Equal("unsupported", result.Support);
            Assert.Empty(result.Items);
        }

        [Fact]
        public async Task BindingMethods_ReportUnsupported()
        {
            var plugin = new SteamPlugin(new StubLocator(null));
            var applied = await plugin.ApplyBindingAsync(new BindingApplyParameters
            {
                Profile = "default",
                BindingId = "main",
                RenderingAuthority = "provider",
                DeviceScope = new PluginDeviceScope { Kind = "provider-managed" }
            }, TestContext.Current.CancellationToken);
            var released = await plugin.ReleaseBindingAsync(new BindingReleaseParameters
            {
                Profile = "default",
                BindingId = "main"
            }, TestContext.Current.CancellationToken);

            Assert.Equal("unsupported", applied.Outcome);
            Assert.Equal("unsupported", released.Outcome);
        }

        [Fact]
        public async Task ListCatalogAsync_ReportsIncompleteWithoutFreshTimestamp_WhenDiskScanIsBlocked()
        {
            var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var plugin = new SteamPlugin(new BlockingLocator(release.Task), TimeSpan.FromMilliseconds(100));
            try
            {
                var catalog = await plugin.ListCatalogAsync(
                    new CatalogListParameters { Kind = "game" }, TestContext.Current.CancellationToken);

                Assert.Equal("supported", catalog.Support);
                Assert.Empty(catalog.Items);
                Assert.Null(catalog.RefreshedAt);
                Assert.Equal("catalog-scan-incomplete", catalog.Reason!.Code);
            }
            finally
            {
                await plugin.ShutdownAsync(TestContext.Current.CancellationToken);
                release.TrySetResult(null);
            }
        }

        private sealed class BlockingLocator(Task<string?> result) : ISteamRootLocator
        {
            public string? FindSteamRoot() => result.GetAwaiter().GetResult();
        }

        private sealed class StubLocator(string? path) : ISteamRootLocator
        {
            public string? FindSteamRoot() => path;
        }
    }
}
