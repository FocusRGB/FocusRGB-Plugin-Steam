namespace FocusRgb.Plugin.Steam.Tests
{
    public sealed class SteamRootLocatorTests
    {
        [Fact]
        public void ResolveRoot_ReturnsNull_WhenRegistryPathIsMalformed()
        {
            Assert.Null(WindowsSteamRootLocator.ResolveRoot("invalid\0path", null));
            Assert.Null(WindowsSteamRootLocator.ResolveRoot(null, "invalid\0path"));
        }

        [Fact]
        public void ResolveRoot_UsesExecutableDirectory_WhenSteamPathIsAbsent()
        {
            var executable = Path.Combine(Path.GetTempPath(), "Steam", "steam.exe");

            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(executable)),
                WindowsSteamRootLocator.ResolveRoot(null, executable));
        }
    }
}
