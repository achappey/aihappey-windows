using System.Globalization;
using Microsoft.Windows.Globalization;
using Windows.System.UserProfile;

namespace AIHappey.Desktop.Core;

public static class DesktopStartup
{
    public static async Task InitializeAsync(DesktopSession session)
    {
        // Use the Windows display-language preference, not the number/date-format regional setting.
        var windowsLanguage = GlobalizationPreferences.Languages.FirstOrDefault() ?? "en";
        ApplyLanguage(DesktopLanguage.Resolve(null, windowsLanguage));
        var settings = await SettingsStore.LoadAsync(session.DataDirectory, session.Settings);
        var language = DesktopLanguage.Resolve(settings.Language, windowsLanguage);
        var rememberInitialChoice = settings.Language != language;
        settings.Language = language;
        session.Settings = settings;
        session.ActiveLanguage = language;
        ApplyLanguage(language);
        settings.Validate(session.Host.AllowLocal);
        if (rememberInitialChoice) await SettingsStore.SaveAsync(session.DataDirectory, settings);
    }

    private static void ApplyLanguage(string language)
    {
        ApplicationLanguages.PrimaryLanguageOverride = language;
        var culture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}
