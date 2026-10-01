using System;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayPollo.Models;

namespace PlayPollo.Server
{
    /// <summary>Apollo: apps are keyed by uuid, POST /api/apps upserts, POST /api/apps/delete removes.</summary>
    public class ApolloClient : IStreamingServerClient
    {
        private readonly ServerConnection connection;

        public ServerKind Kind => ServerKind.Apollo;

        public ApolloClient(ServerConnection connection)
        {
            this.connection = connection;
        }

        public async Task<ServerInfo> GetInfoAsync()
        {
            var root = await connection.GetJsonAsync("/api/apps").ConfigureAwait(false);
            return new ServerInfo
            {
                Kind = Kind,
                HostName = AppJson.ReadHostName(root),
                AppCount = AppJson.ReadApps(root).Count,
                CertThumbprint = connection.ObservedThumbprint
            };
        }

        public async Task UpsertAppAsync(JObject app, ExportEntry previous)
        {
            if (string.IsNullOrEmpty(AppJson.Str(app, "uuid")))
            {
                app["uuid"] = previous?.Uuid ?? Guid.NewGuid().ToString().ToUpperInvariant();
            }

            await connection.PostJsonAsync("/api/apps", app).ConfigureAwait(false);
        }

        public async Task DeleteAppAsync(ExportEntry entry)
        {
            if (string.IsNullOrEmpty(entry?.Uuid))
            {
                return;
            }

            await connection.PostJsonAsync("/api/apps/delete", new JObject { ["uuid"] = entry.Uuid }).ConfigureAwait(false);
        }

        /// <summary>Apollo reports the running app's uuid as current_app, so we only close it when it's this game.</summary>
        public async Task<bool> EndStreamIfRunningAsync(ExportEntry entry)
        {
            if (string.IsNullOrEmpty(entry?.Uuid))
            {
                return false;
            }

            var root = await connection.GetJsonAsync("/api/apps").ConfigureAwait(false);
            var running = (root as JObject)?.Value<string>("current_app");
            if (!string.Equals(running, entry.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // The server insists on a JSON content type even though it ignores the body.
            await connection.PostJsonAsync("/api/apps/close", new JObject()).ConfigureAwait(false);
            return true;
        }

        public async Task<string> UploadCoverAsync(string key, byte[] png)
        {
            var response = await connection.PostJsonAsync("/api/covers/upload", AppJson.CoverBody(key, png)).ConfigureAwait(false);
            return AppJson.ReadCoverPath(response, null);
        }

        public void Dispose()
        {
            connection.Dispose();
        }
    }
}
