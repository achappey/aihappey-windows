using System.Security.Cryptography;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey_Desktop_HeaderAuth;

public sealed class HeaderAuthentication : IDesktopHost
{
    private static readonly byte[] Entropy = "AIHappey.Desktop.HeaderAuth.v1"u8.ToArray();
    private readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHappey", "Desktop", "HeaderAuth", "credentials.bin");
    private Dictionary<string, string>? headers;
    public string ProfileId => "HeaderAuth";
    public bool AllowLocal => true;
    public string AccountLabel => "API keys";
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
        { throw new InvalidOperationException("The protected API-key store could not be read. Restore or remove credentials.bin in the HeaderAuth data directory."); }
    }

    public static void Validate(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (name, value) in values)
        {
            if (!name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) || name.Length < 3
                || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || value.Contains('\r') || value.Contains('\n') || string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Use valid X- provider header names and non-empty, single-line API keys.");
        }
    }

    public async Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(new TextBlock { Text = "Keys are protected for your Windows account. They are sent as HTTP headers to the selected local or remote gateways. Add the provider's exact header name, for example X-OpenAI-Key or X-Anthropic-Key. No keys are included in chat history.", TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        var rows = new StackPanel { Spacing = 8 };
        var entries = new List<(TextBox Name, PasswordBox Key, Grid Row)>();
        void Add(string name, string key)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var field = new TextBox { Text = name, PlaceholderText = "X-OpenAI-Key" };
            var secret = new PasswordBox { Password = key, PlaceholderText = "API key" };
            var remove = new Button { Content = "Remove" };
            row.Children.Add(field); Grid.SetColumn(secret, 1); row.Children.Add(secret); Grid.SetColumn(remove, 2); row.Children.Add(remove);
            entries.Add((field, secret, row)); rows.Children.Add(row);
            remove.Click += (_, _) => { entries.RemoveAll(x => x.Row == row); rows.Children.Remove(row); };
        }
        foreach (var item in headers!) Add(item.Key, item.Value);
        if (entries.Count == 0) Add("X-OpenAI-Key", "");
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 320 });
        var add = new Button { Content = "Add provider key" }; add.Click += (_, _) => Add("", ""); panel.Children.Add(add);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap }; panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = (XamlRoot)xamlRoot, Title = "Provider API keys", Content = panel, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        SystemAppearance.PrepareDialog(dialog);
        Dictionary<string, string>? next = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                next = new(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Key.Password)) continue;
                    if (!next.TryAdd(entry.Name.Text.Trim(), entry.Key.Password.Trim())) throw new ArgumentException("Provider header names must be unique.");
                }
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
