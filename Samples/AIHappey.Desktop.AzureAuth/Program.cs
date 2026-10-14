using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_AzureAuth;

internal static class Program
{
    [STAThread]
    private static int Main() => DesktopActivation.Run("AzureAuth", () => new App());
}
