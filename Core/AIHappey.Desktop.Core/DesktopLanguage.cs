namespace AIHappey.Desktop.Core;

public static class DesktopLanguage
{
    // The saved choice is deliberately independent of the running session's native resource context.
    public static string Resolve(string? saved, string windowsLanguage)
        => saved is "en" or "nl" ? saved
            : windowsLanguage.Equals("nl", StringComparison.OrdinalIgnoreCase)
                || windowsLanguage.StartsWith("nl-", StringComparison.OrdinalIgnoreCase) ? "nl" : "en";
}
