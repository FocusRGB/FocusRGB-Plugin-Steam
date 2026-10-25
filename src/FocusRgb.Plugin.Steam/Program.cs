using FocusRgb.PluginSdk;

namespace FocusRgb.Plugin.Steam
{
    internal static class Program
    {
        public static Task Main() => PluginServer.RunAsync(
            new SteamPlugin(new WindowsSteamRootLocator()),
            Console.In,
            Console.Out,
            Console.Error);
    }
}
