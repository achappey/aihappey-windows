using Microsoft.UI.Xaml;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_AzureAuth;

public sealed class MainWindow : Window
{
    private readonly ChatShell shell;
    private readonly SystemAppearance appearance;
    private bool allowClose;
    public MainWindow()
    {
        Title = DesktopBranding.AppName + " — Enterprise";
        var config = EnterpriseConfiguration.Load();
        var session = new DesktopSession(new EntraAuthentication(config), new RemoteRuntimeResolver(), config.Settings);
        shell = new ChatShell(session);
        appearance = new SystemAppearance(shell);
        Content = shell;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        AppWindow.Closing += async (_, args) =>
        {
            if (allowClose) return;
            args.Cancel = true;
            await shell.ShutdownAsync();
            allowClose = true; Close();
        };
        Closed += (_, _) => appearance.Dispose();
    }
}
