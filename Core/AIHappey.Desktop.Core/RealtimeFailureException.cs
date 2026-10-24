namespace AIHappey.Desktop.Core;

/// <summary>Only application-authored descriptions and numeric protocol codes may reach the UI.
/// Never attach upstream exception text, request URIs, raw provider messages or inner exceptions.</summary>
public sealed class RealtimeFailureException(string code, string safeMessage) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public static RealtimeFailureException Safe(Exception error, string code, string message) => error as RealtimeFailureException ?? new(code, message);
    public override string ToString() => Code + ": " + Message;
}
