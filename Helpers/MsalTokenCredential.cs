#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using Spectre.Console;

namespace Unify.Cli.Helpers
{
    public class MsalTokenCredential : TokenCredential
    {
        private IPublicClientApplication? App { get; set; }
        private const string RedirectUri = "http://localhost";
        
        private string? TenantId { get; set; }
        private string Instance { get; set; }
        private string? Username { get; set; }
        
        public MsalTokenCredential(string? tenantId, string? username, string instance = "https://login.microsoftonline.com")
        {
            TenantId = tenantId ?? "organizations"; // MSA-passthrough
            Username = username;
            Instance = instance;
        }
        
        private async Task<IPublicClientApplication> GetOrCreateApp()
        {
            if (App == null)
            {
                string userProfile;
                string cacheDir;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    userProfile = Environment.GetEnvironmentVariable("USERPROFILE")!;
                    cacheDir = Path.Combine(userProfile, @"AppData\Local\.IdentityService");
                }
                else
                {
                    userProfile = Environment.GetEnvironmentVariable("HOME")!;
                    cacheDir = Path.Combine(userProfile, @".IdentityService");
                }

                // https://www.schaeflein.net/use-a-cli-to-get-an-access-token-for-your-aad-protected-web-api/
                // https://github.com/Azure/azure-cli/blob/24e0b9ef8716e16b9e38c9bb123a734a6cf550eb/src/azure-cli-core/azure/cli/core/_profile.py#L65
                const string clientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
                
                var storageProperties =
                    new StorageCreationPropertiesBuilder(
                            "msal.cache",
                            cacheDir)
                    
                        .WithMacKeyChain(
                            "myapp_msal_service",
                            "myapp_msal_account")
                        
                        .Build();

                App = PublicClientApplicationBuilder.Create(clientId)
                    .WithRedirectUri(RedirectUri)
                    .Build();

                // This hooks up the cross-platform cache into MSAL
                var cacheHelper = await MsalCacheHelper.CreateAsync(storageProperties).ConfigureAwait(false);
                cacheHelper.RegisterCache(App.UserTokenCache);
            }
            return App;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            return GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var app = await GetOrCreateApp();
            AuthenticationResult? result = null;
            var accounts = await app.GetAccountsAsync()!;
            var account = accounts.FirstOrDefault();
            
            try
            {
                result = await app.AcquireTokenSilent(requestContext.Scopes, account)
                    .WithAuthority(Instance, TenantId)
                    .ExecuteAsync(cancellationToken);
            }
            catch (MsalUiRequiredException ex)
            {
                if (account == null && !string.IsNullOrEmpty(Username))
                {
                    AnsiConsole.MarkupLine($"{Emoji.Known.CrossMark}[bold red3]No valid tokens found in the cache.\nPlease sign-in to Visual Studio with this account:\n\n{Username}.\n\nAfter signing-in, re-run the tool.[/]");
                }
                result = await app.AcquireTokenInteractive(requestContext.Scopes)
                    .WithAccount(account)
                    .WithClaims(ex.Claims)
                    .WithAuthority(Instance, TenantId)
                    .ExecuteAsync(cancellationToken);
            }
            catch (MsalServiceException ex)
            {
                if (ex.Message.Contains("AADSTS70002")) // "The client does not exist or is not enabled for consumers"
                {
                    AnsiConsole.MarkupLine($"{Emoji.Known.CrossMark}[bold red3]An Azure AD tenant, and a user in that tenant, needs to be created for this account before an application can be created. See https://aka.ms/ms-identity-app/create-a-tenant.[/]");
                    Environment.Exit(1); // we want to exit here because this is probably an MSA without an AAD tenant.
                }

                AnsiConsole.MarkupLine("{0}[bold red3]Error encountered with sign-in. See error message for details:\n{1} [/]",
                    Emoji.Known.CrossMark, ex.Message);
                Environment.Exit(1); // we want to exit here. Re-sign in will not resolve the issue.
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine("{0}[bold red3]Error encountered with sign-in. See error message for details:\n{1} [/]",
                    Emoji.Known.CrossMark, ex.Message);
                Environment.Exit(1);
            }
            return new AccessToken(result.AccessToken, result.ExpiresOn);
        }
    }
}