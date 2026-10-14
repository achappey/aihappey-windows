using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_HeaderAuth;

internal static class Program
{
    [STAThread]
    private static int Main() => DesktopActivation.Run("HeaderAuth", () => new App());
}
