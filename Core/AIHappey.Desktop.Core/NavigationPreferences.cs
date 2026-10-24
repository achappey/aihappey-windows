namespace AIHappey.Desktop.Core;

/// <summary>Local sidebar choices. Keys are stable across languages and match the browser where available.</summary>
public sealed class NavigationPreferences
{
    public static IReadOnlyList<string> PrimaryKeys { get; } = Array.AsReadOnly<string>(["new", "realtime", "search-conversations", "images", "videos", "speech", "transcriptions"]);
    public static IReadOnlyList<string> AgentKeys { get; } = Array.AsReadOnly<string>(["agents", "plugins", "servers", "skills"]);
    public static IReadOnlyList<string> ResourceKeys { get; } = Array.AsReadOnly<string>(["files"]);

    private List<string> hiddenItemKeys = ["videos", "speech"];
    public List<string> HiddenItemKeys
    {
        get => hiddenItemKeys;
        // An empty list is intentional: never reapply defaults to a user's Show all choice.
        set => hiddenItemKeys = value?.Where(IsCustomizable).Distinct(StringComparer.Ordinal).ToList() ?? ["videos", "speech"];
    }

    public static bool IsCustomizable(string key) => PrimaryKeys.Contains(key) || AgentKeys.Contains(key) || ResourceKeys.Contains(key);
    public bool IsHidden(string key) => HiddenItemKeys.Contains(key, StringComparer.Ordinal);
    public void SetHidden(string key, bool hidden)
    {
        if (!IsCustomizable(key)) return;
        HiddenItemKeys.RemoveAll(item => item == key);
        if (hidden) HiddenItemKeys.Add(key);
    }
    public NavigationPreferences Clone() => new() { HiddenItemKeys = HiddenItemKeys.ToList() };
}
