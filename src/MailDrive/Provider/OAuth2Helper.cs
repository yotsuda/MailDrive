using Microsoft.Identity.Client;

namespace MailDrive.Provider;

internal static class OAuth2Helper
{
    // Default public client for Office 365 IMAP/SMTP
    // Users can override via ClientId in config
    private const string DefaultClientId = "d3590ed6-52b3-4102-aeff-aad2292ab01c"; // Microsoft Office
    private const string DefaultTenantId = "common";

    private static readonly string[] ImapScopes =
    [
        "https://outlook.office365.com/IMAP.AccessAsUser.All",
        "https://outlook.office365.com/SMTP.Send",
        "offline_access",
    ];

    private static readonly string TokenCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MailDrive", "msal_cache.bin");

    internal static string AcquireToken(string username, string? tenantId, string? clientId,
        bool useDeviceCode = false)
    {
        var tid = tenantId ?? DefaultTenantId;
        var cid = clientId ?? DefaultClientId;
        var authority = $"https://login.microsoftonline.com/{tid}";

        var app = PublicClientApplicationBuilder
            .Create(cid)
            .WithAuthority(authority)
            .WithDefaultRedirectUri()
            .Build();

        // Device Code: persist tokens to file (re-auth is cumbersome)
        // PKCE: memory-only cache (re-auth is easy via browser)
        if (useDeviceCode)
            EnableFileTokenCache(app);

        // Try silent first (cached token)
        try
        {
            var accounts = app.GetAccountsAsync().GetAwaiter().GetResult();
            var account = accounts.FirstOrDefault(a =>
                a.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (account != null)
            {
                var silent = app.AcquireTokenSilent(ImapScopes, account)
                    .ExecuteAsync().GetAwaiter().GetResult();
                return silent.AccessToken;
            }
        }
        catch (MsalUiRequiredException) { }

        AuthenticationResult result;
        if (useDeviceCode)
        {
            // Device Code Flow — for headless/SSH environments
            result = app.AcquireTokenWithDeviceCode(ImapScopes, callback =>
            {
                Console.Error.WriteLine(callback.Message);
                return Task.CompletedTask;
            }).ExecuteAsync().GetAwaiter().GetResult();
        }
        else
        {
            // Authorization Code Flow + PKCE — default, browser-based
            result = app.AcquireTokenInteractive(ImapScopes)
                .WithLoginHint(username)
                .ExecuteAsync().GetAwaiter().GetResult();
        }

        return result.AccessToken;
    }

    private static void EnableFileTokenCache(IPublicClientApplication app)
    {
        var dir = Path.GetDirectoryName(TokenCachePath)!;
        Directory.CreateDirectory(dir);

        app.UserTokenCache.SetBeforeAccess(args =>
        {
            if (File.Exists(TokenCachePath))
                args.TokenCache.DeserializeMsalV3(File.ReadAllBytes(TokenCachePath));
        });

        app.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
                File.WriteAllBytes(TokenCachePath, args.TokenCache.SerializeMsalV3());
        });
    }
}
