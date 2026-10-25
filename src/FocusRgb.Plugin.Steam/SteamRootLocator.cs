using Microsoft.Win32;

namespace FocusRgb.Plugin.Steam
{
    internal interface ISteamRootLocator
    {
        string? FindSteamRoot();
    }

    internal sealed class WindowsSteamRootLocator : ISteamRootLocator
    {
        public string? FindSteamRoot()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return Path.GetFullPath(path);
            }

            var executable = key?.GetValue("SteamExe") as string;
            return string.IsNullOrWhiteSpace(executable)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(executable));
        }
    }
}
