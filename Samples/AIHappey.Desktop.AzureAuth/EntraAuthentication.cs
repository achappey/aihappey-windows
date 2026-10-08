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
    public string? ChatbotInstructions { get; set; }

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
    private DesktopUserContext? userContext;
    public string ProfileId => "AzureAuth";
    public bool AllowLocal => false;
    public string AccountLabel => account?.Username ?? DesktopResources.Get("SignIn");
    public string HistoryIdentity => account?.HomeAccountId.Identifier ?? "signed-out";
    public DesktopUserContext? UserContext => account is null ? null : userContext
        ?? new(account.Username, Id: account.HomeAccountId.ObjectId, TenantId: account.HomeAccountId.TenantId);

    private void CaptureIdentity(AuthenticationResult result)
    {
        userContext = new(result.Account.Username, result.ClaimsPrincipal?.FindFirst("name")?.Value,
            result.ClaimsPrincipal?.FindFirst("oid")?.Value ?? result.Account.HomeAccountId.ObjectId,
            result.TenantId);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (app is not null) return;
        if (!Guid.TryParse(config.TenantId, out _) || !Guid.TryParse(config.ClientId, out _)
            || config.AiScopes.Length == 0 || config.AgentsScopes.Length == 0)
            throw new InvalidOperationException(DesktopResources.Get("EnterpriseConfigurationRequired"));
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
            { throw new InvalidOperationException(DesktopResources.Get("TokenCacheReadFailed")); }
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
        if (account is not null)
        {
            try { CaptureIdentity(await app.AcquireTokenSilent(config.AiScopes, account).ExecuteAsync(cancellationToken)); }
            catch (MsalException) { /* Cached account fields remain available; sign-in is handled explicitly. */ }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        // Tokens may only go to the service configured by the enterprise distributor, never an arbitrary user-entered host.
        var allowed = DesktopSettings.RemoteUri(service == ServiceKind.Ai ? config.AiUrl : config.AgentsUrl);
        if (request.RequestUri is null || !allowed.IsBaseOf(request.RequestUri))
            throw new InvalidOperationException(DesktopResources.Get("EnterpriseDestinationRejected"));
        if (account is null) throw new InvalidOperationException(DesktopResources.Get("EnterpriseSignInRequired"));
        try
        {
            var result = await app!.AcquireTokenSilent(service == ServiceKind.Ai ? config.AiScopes : config.AgentsScopes, account).ExecuteAsync(cancellationToken);
            CaptureIdentity(result);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        }
        catch (MsalUiRequiredException) { throw new InvalidOperationException(DesktopResources.Get("InteractiveSignInRequired")); }
        catch (MsalException) { throw new InvalidOperationException(DesktopResources.Get("TokenAcquisitionFailed")); }
    }

    public async Task ManageAccountAsync(object xamlRoot, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        var dialog = new ContentDialog
        {
            XamlRoot = (XamlRoot)xamlRoot, Title = DesktopResources.Get("EnterpriseAccount"),
            Content = account?.Username ?? DesktopResources.Get("EnterpriseSignInHint"),
            PrimaryButtonText = account is null ? DesktopResources.Get("SignIn") : DesktopResources.Get("SignInAgain"),
            SecondaryButtonText = account is null ? "" : DesktopResources.Get("SignOut"), CloseButtonText = DesktopResources.Get("Cancel")
        };
        SystemAppearance.PrepareDialog(dialog);
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary)
        {
            foreach (var cached in await app!.GetAccountsAsync()) await app.RemoveAsync(cached);
            account = null;
            userContext = null;
            return;
        }
        if (choice != ContentDialogResult.Primary) return;
        try
        {
            var result = await app!.AcquireTokenInteractive(config.AiScopes)
                .WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
            account = result.Account;
            CaptureIdentity(result);
            // The second resource can require its own consent. Obtain it explicitly before starting inference.
            try { await app.AcquireTokenSilent(config.AgentsScopes, account).ExecuteAsync(cancellationToken); }
            catch (MsalUiRequiredException)
            {
                var agentsResult = await app.AcquireTokenInteractive(config.AgentsScopes).WithAccount(account)
                    .WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
                if (agentsResult.Account.HomeAccountId.Identifier != account.HomeAccountId.Identifier)
                    throw new InvalidOperationException(DesktopResources.Get("SameEnterpriseAccount"));
            }
        }
        catch (MsalException) { throw new InvalidOperationException(DesktopResources.Get("EnterpriseSignInFailed")); }
    }
}
