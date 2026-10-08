using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_AzureAuth;

public partial class App : Application
{
    private Window? window;
    private readonly Mutex instance = new(false, "Local\\AIHappey.Desktop.AzureAuth");

    public App()
    {
        RequestedTheme = AIHappey.Desktop.Core.SystemAppearance.CurrentTheme;
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { if (!instance.WaitOne(0)) { Exit(); return; } }
        catch (AbandonedMutexException) { }
        try
        {
            var config = EnterpriseConfiguration.Load();
            var session = new DesktopSession(new EntraAuthentication(config), new RemoteRuntimeResolver(), config.Settings);
            await DesktopStartup.InitializeAsync(session);
            window = new MainWindow(session);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"AzureAuth startup failed before the window was activated: {error}");
            // Emergency startup reporting must work even when native resources are missing.
            window = new Window { Title = DesktopBranding.AppName, Content = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) } };
        }
        window.Activate();
    }
}
