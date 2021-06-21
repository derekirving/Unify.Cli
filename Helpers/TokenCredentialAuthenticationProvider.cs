#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Graph;

namespace Unify.Cli.Helpers
{
    public class TokenCredentialAuthenticationProvider : IAuthenticationProvider
    {
        public TokenCredentialAuthenticationProvider(
            TokenCredential tokenCredentials,
            IEnumerable<string>? initialScopes = null)
        {
            _tokenCredentials = tokenCredentials;
            _initialScopes = initialScopes ?? new string[] { "https://graph.microsoft.com/.default" };
        }

        readonly TokenCredential _tokenCredentials;
        readonly IEnumerable<string> _initialScopes;

        public async Task AuthenticateRequestAsync(HttpRequestMessage request)
        {
            // Try with the Shared token cache credentials

            var context = new TokenRequestContext(_initialScopes.ToArray());
            var token = await _tokenCredentials.GetTokenAsync(context, CancellationToken.None);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
    }
}