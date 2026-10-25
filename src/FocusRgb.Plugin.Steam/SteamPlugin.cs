using FocusRgb.Contracts;
using FocusRgb.PluginSdk;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class SteamPlugin : IFocusRgbPlugin
    {
        private readonly SteamCatalogReader _catalog;

        public SteamPlugin(ISteamRootLocator rootLocator)
        {
            _catalog = new SteamCatalogReader(rootLocator);
        }

        public Task<InitializeResult> InitializeAsync(
            InitializeParameters parameters,
            CancellationToken cancellationToken) => Task.FromResult(new InitializeResult
            {
                PluginId = "steam",
                PluginVersion = typeof(SteamPlugin).Assembly.GetName().Version!.ToString(3),
                ContractVersion = ContractVersion.Current,
                Capabilities =
                [
                    new PluginCapability { Id = "game.catalog", Support = "supported" }
                ]
            });

        public Task<PluginStatusResult> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_catalog.IsInstalled
                ? new PluginStatusResult { Availability = "available" }
                : new PluginStatusResult
                {
                    Availability = "unavailable",
                    Reason = new PluginReason
                    {
                        Code = "steam-not-installed",
                        Message = "Steam was not found for the current Windows user."
                    }
                });

        public async Task<PluginCatalogResult> ListCatalogAsync(
            CatalogListParameters parameters,
            CancellationToken cancellationToken)
        {
            if (!string.Equals(parameters.Kind, "game", StringComparison.Ordinal))
            {
                return Unsupported("Steam provides only an installed-game catalog.");
            }

            return new PluginCatalogResult
            {
                Support = "supported",
                RefreshedAt = DateTimeOffset.UtcNow,
                Items = await _catalog.ReadAsync(cancellationToken).ConfigureAwait(false)
            };
        }

        public Task<BindingApplyResult> ApplyBindingAsync(
            BindingApplyParameters parameters,
            CancellationToken cancellationToken) => Task.FromResult(new BindingApplyResult
            {
                Outcome = "unsupported",
                Reason = UnsupportedReason("Steam discovery does not control lighting output.")
            });

        public Task<BindingReleaseResult> ReleaseBindingAsync(
            BindingReleaseParameters parameters,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BindingReleaseResult { Outcome = "unsupported" });

        public Task ShutdownAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static PluginCatalogResult Unsupported(string message) => new()
        {
            Support = "unsupported",
            Reason = UnsupportedReason(message)
        };

        private static PluginReason UnsupportedReason(string message) => new()
        {
            Code = "capability-unsupported",
            Message = message
        };
    }
}
