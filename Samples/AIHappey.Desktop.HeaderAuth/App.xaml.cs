using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AIHappey.Desktop.Core;

namespace AIHappey_Desktop_HeaderAuth;

public partial class App : Application
{
    private Window? window;
    private readonly Mutex instance = new(false, "Local\\AIHappey.Desktop.HeaderAuth");

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
            var host = new HeaderAuthentication();
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHappey", "Desktop", host.ProfileId);
            var session = new DesktopSession(host, new ManagedLocalRuntime(Path.Combine(AppContext.BaseDirectory, "runtimes"), directory), new DesktopSettings());
            session.ContextOptions = DesktopContextOptions.Load(Path.Combine(AppContext.BaseDirectory, "chat-context.json"));
            session.ContextOptions.AppName ??= DesktopBranding.AppName;
            session.DefaultAgents = AIHappey.Desktop.Samples.SampleAgents.Load();
            await DesktopStartup.InitializeAsync(session);
            window = new MainWindow(session);
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"HeaderAuth startup failed before the window was activated: {error}");
            // Emergency startup reporting must work even when native resources are missing.
            window = new Window { Title = DesktopBranding.AppName, Content = new TextBlock { Text = error.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24) } };
        }
        window.Activate();
    }
}
