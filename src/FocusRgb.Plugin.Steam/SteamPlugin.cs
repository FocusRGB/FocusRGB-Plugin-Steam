using FocusRgb.Contracts;
using FocusRgb.PluginSdk;

namespace FocusRgb.Plugin.Steam
{
    internal sealed class SteamPlugin : IFocusRgbPlugin, IDisposable
    {
        private readonly SteamCatalogReader _catalog;
        private readonly string? _configurationError;

        public SteamPlugin(ISteamRootLocator rootLocator, TimeSpan? scanBudget = null, string? dataDirectory = null)
        {
            dataDirectory ??= Environment.GetEnvironmentVariable("FOCUSRGB_PLUGIN_DATA_DIRECTORY");
            var settings = SteamLibrarySettings.Load(dataDirectory);
            _configurationError = settings.Error;
            _catalog = new SteamCatalogReader(new ConfiguredRootLocator(rootLocator, settings.SteamRoot, dataDirectory), scanBudget,
                dataDirectory, settings.ExtraLibraryFolders);
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
                    new PluginCapability
                    {
                        Id = "game.catalog", Support = _configurationError is null ? "supported" : "unsupported",
                        Reason = _configurationError is null ? null : UnsupportedReason(_configurationError)
                    }
                ]
            });

        public Task<PluginStatusResult> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_configurationError is not null
                ? new PluginStatusResult { Availability = "unavailable", Reason = UnsupportedReason(_configurationError) }
                : _catalog.IsInstalled
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
            if (_configurationError is not null) return Unsupported(_configurationError);
            if (!string.Equals(parameters.Kind, "game", StringComparison.Ordinal))
            {
                return Unsupported("Steam provides only an installed-game catalog.");
            }

            var items = await _catalog.ReadAsync(cancellationToken).ConfigureAwait(false);
            var incomplete = _catalog.IsScanIncomplete;
            return new PluginCatalogResult
            {
                Support = "supported",
                RefreshedAt = incomplete ? null : DateTimeOffset.UtcNow,
                Items = items,
                Reason = incomplete ? new PluginReason
                {
                    Code = "catalog-scan-incomplete",
                    Message = "Steam discovery is incomplete; showing cached or partial results while background scanning continues."
                } : null
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

        public Task ShutdownAsync(CancellationToken cancellationToken)
        {
            Dispose();
            return Task.CompletedTask;
        }

        public void Dispose() => _catalog.Dispose();

        private sealed class ConfiguredRootLocator(ISteamRootLocator fallback, string? configured, string? dataDirectory) : ISteamRootLocator
        {
            private int _defaultsWritten;

            public string? FindSteamRoot()
            {
                var root = configured ?? fallback.FindSteamRoot();
                if (Interlocked.Exchange(ref _defaultsWritten, 1) == 0)
                {
                    SteamLibrarySettings.WriteDetectedDefaults(dataDirectory, root);
                }
                return root;
            }
        }

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
