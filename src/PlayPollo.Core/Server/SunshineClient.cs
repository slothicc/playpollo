using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayPollo.Models;

namespace PlayPollo.Server
{
    /// <summary>
    /// LizardByte Sunshine: apps have no id, only a list position. POST /api/apps with index -1 appends and any
    /// other index replaces that slot, DELETE /api/apps/{index} removes one. Positions shift after a delete, so
    /// we re-read the list and match on the name and cmd we wrote last time.
    /// </summary>
    public class SunshineClient : IStreamingServerClient
    {
        private readonly ServerConnection connection;

        public ServerKind Kind => ServerKind.Sunshine;

        public SunshineClient(ServerConnection connection)
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
            var root = await connection.GetJsonAsync("/api/apps").ConfigureAwait(false);
            var apps = AppJson.ReadApps(root);

            var index = FindIndex(apps, previous?.LastName, previous?.LastCmd);
            if (index < 0)
            {
                index = FindIndex(apps, AppJson.Str(app, "name"), AppJson.Str(app, "cmd"));
            }

            app["index"] = index;
            await connection.PostJsonAsync("/api/apps", app).ConfigureAwait(false);
        }

        public async Task DeleteAppAsync(ExportEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            var root = await connection.GetJsonAsync("/api/apps").ConfigureAwait(false);
            var index = FindIndex(AppJson.ReadApps(root), entry.LastName, entry.LastCmd);
            if (index >= 0)
            {
                await connection.DeleteAsync("/api/apps/" + index).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sunshine doesn't say which app is running, so this closes whatever is. That's a no-op when nothing is running,
        /// but it would also end a stream of a different app if an exported game is closed while another is streaming.
        /// </summary>
        public async Task<bool> EndStreamIfRunningAsync(ExportEntry entry)
        {
            if (entry == null)
            {
                return false;
            }

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

        private static int FindIndex(System.Collections.Generic.List<JObject> apps, string name, string cmd)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            return apps.FindIndex(a => AppJson.Str(a, "name") == name && AppJson.Str(a, "cmd") == (cmd ?? string.Empty));
        }
    }
}
