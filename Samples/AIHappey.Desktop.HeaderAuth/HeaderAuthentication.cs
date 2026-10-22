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
    public bool HasProviderApiKeys => true;
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

    public Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken) =>
        ManageProviderApiKeysAsync(xamlRoot, _ => Task.CompletedTask, cancellationToken);

    public async Task ManageProviderApiKeysAsync(object xamlRoot, Func<IReadOnlyDictionary<string, bool>, Task> savePreferences,
        CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var panel = new ProviderApiKeysView(headers!);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = (XamlRoot)xamlRoot, Title = DesktopResources.Get("ProviderApiKeys"), Content = panel, PrimaryButtonText = DesktopResources.Get("Save"), CloseButtonText = DesktopResources.Get("Cancel") };
        SystemAppearance.PrepareDialog(dialog);
        var saving = false;
        dialog.Closing += (_, args) => { if (saving && !cancellationToken.IsCancellationRequested) args.Cancel = true; };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            saving = true; dialog.IsPrimaryButtonEnabled = false; error.Text = "";
            try
            {
                var next = panel.ReadHeaders();
                Validate(next);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var previous = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
                var plain = JsonSerializer.SerializeToUtf8Bytes(next);
                byte[] encrypted;
                try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(plain); }
                await File.WriteAllBytesAsync(path + ".tmp", encrypted, cancellationToken);
                File.Move(path + ".tmp", path, true);
                try { await savePreferences(ProviderPreferences.CredentialChanges(headers!, next)); }
                catch
                {
                    // Core publishes preferences only after its atomic settings save succeeds.
                    // Restore credentials on failure so the open draft can be retried safely.
                    if (previous is null) File.Delete(path);
                    else { await File.WriteAllBytesAsync(path + ".tmp", previous); File.Move(path + ".tmp", path, true); }
                    throw;
                }
                headers = next;
            }
            catch (ArgumentException e) { args.Cancel = true; error.Text = e.Message; }
            catch (Exception) { args.Cancel = true; error.Text = DesktopResources.Get("ProviderKeysSaveFailed"); }
            finally { saving = false; dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        using var canceled = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
