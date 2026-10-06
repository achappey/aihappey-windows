using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AIHappey.Desktop.Core;
using Microsoft.Identity.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey_Desktop_AzureAuth;

public sealed class EnterpriseConfiguration
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string AiUrl { get; set; } = "";
    public string AgentsUrl { get; set; } = "";
    public string[] AiScopes { get; set; } = [];
    public string[] AgentsScopes { get; set; } = [];

    public static EnterpriseConfiguration Load()
    {
        try { return JsonSerializer.Deserialize<EnterpriseConfiguration>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "desktop.json")), JsonSerializerOptions.Web) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }

    public DesktopSettings Settings => new()
    {
        Ai = new() { Location = RuntimeLocation.Remote, RemoteUrl = AiUrl },
        Agents = new() { Location = RuntimeLocation.Remote, RemoteUrl = AgentsUrl }
    };
}

/// <summary>Public-client OAuth and encrypted token cache live in this host, never in Core.</summary>
public sealed class EntraAuthentication(EnterpriseConfiguration config) : IDesktopHost
{
    private readonly string cachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIHappey", "Desktop", "AzureAuth", "tokens.bin");
    private static readonly byte[] Entropy = "AIHappey.Desktop.AzureAuth.v1"u8.ToArray();
    private IPublicClientApplication? app;
    private IAccount? account;
    public string ProfileId => "AzureAuth";
    public bool AllowLocal => false;
    public string AccountLabel => account?.Username ?? "Sign in";
    public string HistoryIdentity => account?.HomeAccountId.Identifier ?? "signed-out";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (app is not null) return;
        if (!Guid.TryParse(config.TenantId, out _) || !Guid.TryParse(config.ClientId, out _)
            || config.AiScopes.Length == 0 || config.AgentsScopes.Length == 0)
            throw new InvalidOperationException("Configure desktop.json with your Entra tenant ID, desktop public-client ID, and gateway scopes, then restart the app.");
        _ = DesktopSettings.RemoteUri(config.AiUrl); _ = DesktopSettings.RemoteUri(config.AgentsUrl);
        app = PublicClientApplicationBuilder.Create(config.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{config.TenantId}")
            .WithRedirectUri("http://localhost")
            .Build();
        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (!File.Exists(cachePath)) return;
            try
            {
                var plain = ProtectedData.Unprotect(File.ReadAllBytes(cachePath), Entropy, DataProtectionScope.CurrentUser);
                try { args.TokenCache.DeserializeMsalV3(plain); }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("The protected Entra cache cannot be read. Restore or remove tokens.bin in the AzureAuth data directory."); }
        });
        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var plain = args.TokenCache.SerializeMsalV3();
            try
            {
                File.WriteAllBytes(cachePath + ".tmp", ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
                File.Move(cachePath + ".tmp", cachePath, true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        });
        account = (await app.GetAccountsAsync()).FirstOrDefault();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        // Tokens may only go to the service configured by the enterprise distributor, never an arbitrary user-entered host.
        var allowed = DesktopSettings.RemoteUri(service == ServiceKind.Ai ? config.AiUrl : config.AgentsUrl);
        if (request.RequestUri is null || !allowed.IsBaseOf(request.RequestUri))
            throw new InvalidOperationException("This destination is not approved by the enterprise host configuration. Update desktop.json and restart to change gateways.");
        if (account is null) throw new InvalidOperationException("Sign in with your enterprise account using the account button.");
        try
        {
            var result = await app!.AcquireTokenSilent(service == ServiceKind.Ai ? config.AiScopes : config.AgentsScopes, account).ExecuteAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        }
        catch (MsalUiRequiredException) { throw new InvalidOperationException("Your account needs interactive authentication or consent. Use the account button to sign in again."); }
        catch (MsalException) { throw new InvalidOperationException("Entra token acquisition failed. Check the desktop registration, gateway scopes, and tenant policy."); }
    }

    public async Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = (XamlRoot)xamlRoot, Title = "Enterprise account",
            Content = account?.Username ?? "Sign in through your system browser. No client secret or provider API key is stored in this application.",
            PrimaryButtonText = account is null ? "Sign in" : "Sign in again",
            SecondaryButtonText = account is null ? "" : "Sign out", CloseButtonText = "Cancel"
        };
        SystemAppearance.PrepareDialog(dialog);
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            foreach (var cached in await app!.GetAccountsAsync()) await app.RemoveAsync(cached);
            account = null;
            return;
        }
        if (choice != ContentDialogResult.Primary) return;
        try
        {
            var result = await app!.AcquireTokenInteractive(config.AiScopes)
                .WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
            account = result.Account;
            // The second resource can require its own consent. Obtain it explicitly before starting inference.
            try { await app.AcquireTokenSilent(config.AgentsScopes, account).ExecuteAsync(cancellationToken); }
            catch (MsalUiRequiredException)
            {
                var agentsResult = await app.AcquireTokenInteractive(config.AgentsScopes).WithAccount(account)
                    .WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
                if (agentsResult.Account.HomeAccountId.Identifier != account.HomeAccountId.Identifier)
                    throw new InvalidOperationException("Use the same enterprise account for both services.");
            }
        }
        catch (MsalException) { throw new InvalidOperationException("Entra sign-in failed or was canceled. Check your desktop registration, consent, and tenant policy."); }
    }
}
