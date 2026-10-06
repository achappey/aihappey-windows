using Microsoft.UI.Xaml;

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { if (!instance.WaitOne(0)) { Exit(); return; } }
        catch (AbandonedMutexException) { }
        window = new MainWindow();
        window.Activate();
    }
}
