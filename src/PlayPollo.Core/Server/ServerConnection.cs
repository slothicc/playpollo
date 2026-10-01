using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlayPollo.Server
{
    public enum ServerErrorKind
    {
        Unreachable,
        UntrustedCertificate,
        Unauthorized,
        Rejected
    }

    public class ServerException : Exception
    {
        public ServerErrorKind Kind { get; }

        public ServerException(ServerErrorKind kind, string message, Exception inner = null)
            : base(message, inner)
        {
            Kind = kind;
        }
    }

    /// <summary>
    /// HTTP transport for the admin API. Handles the self-signed certificate (pinned by thumbprint) and
    /// both authentication styles: Sunshine accepts HTTP Basic, Apollo only accepts the session cookie
    /// from POST /api/login. Basic is tried first, and a 401 falls back to the login flow.
    /// </summary>
    public class ServerConnection : IDisposable
    {
        private enum AuthMode { Unknown, Basic, Cookie }

        private readonly HttpClient http;
        private readonly string username;
        private readonly string password;
        private readonly string pinnedThumbprint;
        private readonly bool trustUnpinned;
        private AuthMode mode = AuthMode.Unknown;

        /// <summary>Thumbprint of the certificate presented by the server, once a TLS handshake has happened.</summary>
        public string ObservedThumbprint { get; private set; }

        public Uri BaseUri { get; }

        /// <param name="trustUnpinned">Accept any certificate when none is pinned. Only "Test connection" sets this.</param>
        public ServerConnection(Uri baseUri, string username, string password, string pinnedThumbprint, bool trustUnpinned)
            : this(baseUri, username, password, pinnedThumbprint, trustUnpinned, null)
        {
        }

        /// <summary>Test seam: supply a message handler instead of the real network.</summary>
        public ServerConnection(Uri baseUri, string username, string password, string pinnedThumbprint, bool trustUnpinned, HttpMessageHandler handler)
        {
            BaseUri = baseUri;
            this.username = username ?? string.Empty;
            this.password = password ?? string.Empty;
            this.pinnedThumbprint = Normalize(pinnedThumbprint);
            this.trustUnpinned = trustUnpinned;

            http = new HttpClient(handler ?? CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        }

        private HttpMessageHandler CreateHandler()
        {
            var handler = new WebRequestHandler
            {
                AllowAutoRedirect = false,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                ServerCertificateValidationCallback = ValidateCertificate
            };
            return handler;
        }

        private bool ValidateCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (cert != null)
            {
                ObservedThumbprint = Normalize(new X509Certificate2(cert).Thumbprint);
            }

            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            if (pinnedThumbprint.Length > 0)
            {
                return string.Equals(ObservedThumbprint, pinnedThumbprint, StringComparison.OrdinalIgnoreCase);
            }

            return trustUnpinned;
        }

        public static string Normalize(string thumbprint)
        {
            return (thumbprint ?? string.Empty).Replace(" ", string.Empty).Replace(":", string.Empty).ToUpperInvariant();
        }

        public Task<JToken> GetJsonAsync(string path)
        {
            return SendJsonAsync(HttpMethod.Get, path, null);
        }

        public Task<JToken> PostJsonAsync(string path, JToken body)
        {
            return SendJsonAsync(HttpMethod.Post, path, body);
        }

        public Task<JToken> DeleteAsync(string path)
        {
            return SendJsonAsync(HttpMethod.Delete, path, null);
        }

        private async Task<JToken> SendJsonAsync(HttpMethod method, string path, JToken body)
        {
            var response = await SendWithAuthAsync(method, path, body).ConfigureAwait(false);
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new ServerException(ServerErrorKind.Rejected,
                        $"Server rejected {method} {path}: {(int)response.StatusCode} {response.ReasonPhrase} {Truncate(text)}".Trim());
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    return new JObject();
                }

                try
                {
                    return JToken.Parse(text);
                }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException)
                {
                    throw new ServerException(ServerErrorKind.Rejected, $"Unexpected response from {path}: {Truncate(text)}", ex);
                }
            }
        }

        private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string path, JToken body)
        {
            var response = await SendRawAsync(method, path, body, mode != AuthMode.Cookie).ConfigureAwait(false);
            if (!IsUnauthorized(response))
            {
                if (mode == AuthMode.Unknown)
                {
                    mode = AuthMode.Basic;
                }

                return response;
            }

            response.Dispose();
            if (mode == AuthMode.Basic)
            {
                throw Unauthorized();
            }

            await LoginAsync().ConfigureAwait(false);
            mode = AuthMode.Cookie;

            response = await SendRawAsync(method, path, body, false).ConfigureAwait(false);
            if (IsUnauthorized(response))
            {
                response.Dispose();
                throw Unauthorized();
            }

            return response;
        }

        // Servers answer 401, or redirect an unauthenticated request to the login page.
        private static bool IsUnauthorized(HttpResponseMessage response)
        {
            var code = (int)response.StatusCode;
            return code == 401 || code == 403 || (code >= 300 && code < 400);
        }

        private static ServerException Unauthorized()
        {
            return new ServerException(ServerErrorKind.Unauthorized,
                "The server refused the username or password. Check the admin credentials in the plugin settings.");
        }

        private async Task LoginAsync()
        {
            var body = new JObject { ["username"] = username, ["password"] = password };
            using (var response = await SendRawAsync(HttpMethod.Post, "/api/login", body, false).ConfigureAwait(false))
            {
                if (response.IsSuccessStatusCode)
                {
                    return;
                }

                if (IsUnauthorized(response) || response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw Unauthorized();
                }

                throw new ServerException(ServerErrorKind.Rejected, $"Login failed: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }

        private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, JToken body, bool useBasic)
        {
            var request = new HttpRequestMessage(method, new Uri(BaseUri, path));
            if (useBasic)
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
            }

            if (body != null)
            {
                request.Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            }

            try
            {
                return await http.SendAsync(request).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw Translate(ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new ServerException(ServerErrorKind.Unreachable, $"Timed out talking to {BaseUri.Authority}.", ex);
            }
        }

        private ServerException Translate(HttpRequestException ex)
        {
            if (ex.InnerException is WebException web && web.Status == WebExceptionStatus.TrustFailure)
            {
                var detail = string.IsNullOrEmpty(ObservedThumbprint) ? string.Empty : $" (thumbprint {ObservedThumbprint})";
                return new ServerException(ServerErrorKind.UntrustedCertificate,
                    "The server's certificate isn't trusted" + detail + ". Use \"Test connection\" in the plugin settings to trust it.", ex);
            }

            return new ServerException(ServerErrorKind.Unreachable, $"Couldn't reach {BaseUri.Authority}: {ex.InnerException?.Message ?? ex.Message}", ex);
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= 200 ? text : text.Substring(0, 200) + "...";
        }

        public void Dispose()
        {
            http.Dispose();
        }
    }
}
