using System.Text.Json;
using FocusRgb.Contracts;

namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class PluginManifestTests
    {
        [Fact]
        public void Manifest_MatchesAssemblyAndContractVersions()
        {
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "focusrgb-plugin.json")));

            Assert.Equal(ContractVersion.Current,
                manifest.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal(typeof(SteamPlugin).Assembly.GetName().Version!.ToString(3),
                manifest.RootElement.GetProperty("version").GetString());
            Assert.Equal(["filesystem.read"], manifest.RootElement.GetProperty("resources")
                .EnumerateArray().Select(item => item.GetString()));
        }
    }
}
