using System;
using System.Net.Http;
using System.Threading.Tasks;
using PlayPollo.Models;

namespace PlayPollo.Server
{
    public static class ServerClientFactory
    {
        public static Uri BuildBaseUri(PlayPolloSettings settings)
        {
            var host = string.IsNullOrWhiteSpace(settings.Host) ? "localhost" : settings.Host.Trim();
            return new Uri($"https://{host}:{settings.Port}/");
        }

        /// <summary>
        /// Opens a client for the configured server. When the kind is Auto, one request tells Sunshine and Apollo
        /// apart, since only Apollo's app list carries uuids and a host_uuid.
        /// </summary>
        public static async Task<IStreamingServerClient> CreateAsync(PlayPolloSettings settings, bool trustUnpinned = false, HttpMessageHandler handler = null)
        {
            var connection = new ServerConnection(
                BuildBaseUri(settings), settings.Username, settings.Password, settings.PinnedCertThumbprint, trustUnpinned, handler);

            try
            {
                var kind = settings.ServerKind;
                if (kind == ServerKind.Auto)
                {
                    var root = await connection.GetJsonAsync("/api/apps").ConfigureAwait(false);
                    kind = AppJson.LooksLikeApollo(root) ? ServerKind.Apollo : ServerKind.Sunshine;
                }

                return kind == ServerKind.Apollo
                    ? (IStreamingServerClient)new ApolloClient(connection)
                    : new SunshineClient(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }
    }
}
