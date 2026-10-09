namespace AIHappey.Desktop.Core;

/// <summary>Client preferences, independent of installed servers and chat history.</summary>
public sealed class ModelContextPreferences
{
    public bool EnableFormElicitation { get; set; } = true;
    private int toolTimeoutMinutes = 5;
    public int ToolTimeoutMinutes
    {
        get => toolTimeoutMinutes;
        set => toolTimeoutMinutes = Math.Clamp(value, 1, 60);
    }
    public bool ResetTimeoutOnProgress { get; set; } = true;
    // Apps remain a future extension; Skills gates discovery and all retained skill readers.
    public bool EnableApps { get; set; } = true;
    public bool EnableSkills { get; set; } = true;
    public ModelContextPreferences Clone() => (ModelContextPreferences)MemberwiseClone();
}
