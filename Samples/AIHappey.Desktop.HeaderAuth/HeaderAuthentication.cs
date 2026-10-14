using System.Security.Cryptography;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey_Desktop_HeaderAuth;

public sealed class HeaderAuthentication : IDesktopHost
{
    private static readonly byte[] Entropy = "AIHappey.Desktop.HeaderAuth.v1"u8.ToArray();
    private readonly string path = Path.Combine(DesktopPaths.ProfileDirectory("HeaderAuth"), "credentials.bin");
    private Dictionary<string, string>? headers;
    public string ProfileId => "HeaderAuth";
    public bool AllowLocal => true;
    public string AccountLabel => DesktopResources.Get("ApiKeys");
    public string HistoryIdentity => "public-user";

    public async Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        foreach (var (name, value) in headers!) request.Headers.Add(name, value);
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        if (headers is not null) return;
        if (!File.Exists(path)) { headers = new(StringComparer.OrdinalIgnoreCase); return; }
        try
        {
            var encrypted = await File.ReadAllBytesAsync(path, ct);
            var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? []; Validate(headers); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        { throw new InvalidOperationException(DesktopResources.Get("ApiKeysReadFailed")); }
    }

    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (name, value) in values)
        {
            if (!name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || name.Length < 3
                || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || value.Contains('\r') || value.Contains('\n') || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException(DesktopResources.Get("InvalidProviderHeaders"));
        }
    }

    public async Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var panel = new ProviderApiKeysView(headers!);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = (XamlRoot)xamlRoot, Title = DesktopResources.Get("ProviderApiKeys"), Content = panel, PrimaryButtonText = DesktopResources.Get("Save"), CloseButtonText = DesktopResources.Get("Cancel") };
        SystemAppearance.PrepareDialog(dialog);
        Dictionary<string, string>? next = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                next = panel.ReadHeaders();
                Validate(next);
            }
            catch (ArgumentException e) { args.Cancel = true; error.Text = e.Message; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || next is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(next);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(path + ".tmp", encrypted, cancellationToken);
            File.Move(path + ".tmp", path, true); headers = next;
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
