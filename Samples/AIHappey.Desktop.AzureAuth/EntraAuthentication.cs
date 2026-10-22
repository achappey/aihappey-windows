using System.Net.Http.Headers;
using System.Security.Cryptography;
using AIHappey.Desktop.Core;
using Microsoft.Identity.Client;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIHappey_Desktop_AzureAuth;

/// <summary>Public-client OAuth and encrypted token cache live in this host, never in Core.</summary>
public sealed class EntraAuthentication(EnterpriseConfiguration config) : IDesktopHost
{
    private readonly string cachePath = Path.Combine(DesktopPaths.ProfileDirectory("AzureAuth"), "tokens.bin");
    private static readonly byte[] Entropy = "AIHappey.Desktop.AzureAuth.v1"u8.ToArray();
    private IPublicClientApplication? app;
    private IAccount? account;
    private DesktopUserContext? userContext;
    private bool requiresSignIn = true;
    public string ProfileId => "AzureAuth";
    public bool AllowLocal => false;
    public bool RequiresSignIn => requiresSignIn;
    public void ApplySettingsPolicy(DesktopSettings settings) => config.ApplySettingsPolicy(settings);
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
        requiresSignIn = account is null;
        if (account is not null)
        {
            try
            {
                CaptureIdentity(await app.AcquireTokenSilent(config.AiScopes, account).ExecuteAsync(cancellationToken));
                await app.AcquireTokenSilent(config.AgentsScopes, account).ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException) { requiresSignIn = true; }
            catch (MsalException) { /* Cached account fields remain available; sign-in is handled explicitly. */ }
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task AuthenticateAsync(HttpRequestMessage request, ServiceKind service, CancellationToken cancellationToken)
    {
        await InitializeAsync();
        // Tokens may only go to the service configured by the enterprise distributor, never an arbitrary user-entered host.
        config.ValidateDestination(request.RequestUri, service);
        if (account is null) throw new InvalidOperationException(DesktopResources.Get("EnterpriseSignInRequired"));
        try
        {
            var result = await app!.AcquireTokenSilent(service == ServiceKind.Ai ? config.AiScopes : config.AgentsScopes, account).ExecuteAsync(cancellationToken);
            CaptureIdentity(result);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        }
        catch (MsalUiRequiredException)
        {
            requiresSignIn = true;
            throw new InvalidOperationException(DesktopResources.Get("InteractiveSignInRequired"));
        }
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
        using var registration = cancellationToken.Register(() => dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        var choice = await dialog.ShowAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (choice == ContentDialogResult.Secondary)
        {
            foreach (var cached in await app!.GetAccountsAsync()) await app.RemoveAsync(cached);
            account = null;
            userContext = null;
            requiresSignIn = true;
            return;
        }
        if (choice != ContentDialogResult.Primary) return;
        try
        {
            var result = await app!.AcquireTokenInteractive(config.AiScopes)
                .WithPrompt(Prompt.SelectAccount).WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
            var signedInAccount = result.Account;
            // The second resource can require its own consent. Obtain it explicitly before starting inference.
            try { await app.AcquireTokenSilent(config.AgentsScopes, signedInAccount).ExecuteAsync(cancellationToken); }
            catch (MsalUiRequiredException)
            {
                var agentsResult = await app.AcquireTokenInteractive(config.AgentsScopes).WithAccount(signedInAccount)
                    .WithUseEmbeddedWebView(false).ExecuteAsync(cancellationToken);
                if (agentsResult.Account.HomeAccountId.Identifier != signedInAccount.HomeAccountId.Identifier)
                    throw new InvalidOperationException(DesktopResources.Get("SameEnterpriseAccount"));
            }
            account = signedInAccount;
            CaptureIdentity(result);
            requiresSignIn = false;
        }
        catch (MsalClientException e) when (e.ErrorCode == "authentication_canceled") { }
        catch (MsalException) { throw new InvalidOperationException(DesktopResources.Get("EnterpriseSignInFailed")); }
    }
}
