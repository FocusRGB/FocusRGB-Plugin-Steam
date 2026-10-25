using System.Security;
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
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return ResolveRoot(
                    key?.GetValue("SteamPath") as string,
                    key?.GetValue("SteamExe") as string);
            }
            catch (Exception exception) when (IsLocalMetadataException(exception))
            {
                return null;
            }
        }

        internal static string? ResolveRoot(string? path, string? executable)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    return Path.GetFullPath(path);
                }

                return string.IsNullOrWhiteSpace(executable)
                    ? null
                    : Path.GetDirectoryName(Path.GetFullPath(executable));
            }
            catch (Exception exception) when (IsLocalMetadataException(exception))
            {
                return null;
            }
        }

        private static bool IsLocalMetadataException(Exception exception) =>
            exception is ArgumentException or NotSupportedException or SecurityException
                or IOException or UnauthorizedAccessException;
    }
}
