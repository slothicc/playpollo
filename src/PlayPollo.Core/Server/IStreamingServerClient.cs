using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayPollo.Models;

namespace PlayPollo.Server
{
    public class ServerInfo
    {
        public ServerKind Kind { get; set; }

        public string HostName { get; set; }

        public int AppCount { get; set; }

        public string CertThumbprint { get; set; }
    }

    /// <summary>What we remember about a game we've pushed to the server.</summary>
    public class ExportEntry
    {
        /// <summary>Apollo identifies apps by uuid. Unused for Sunshine.</summary>
        public string Uuid { get; set; }

        /// <summary>Sunshine identifies apps by list position, so we match on the name and cmd we last wrote.</summary>
        public string LastName { get; set; }

        public string LastCmd { get; set; }

        public LaunchMode LaunchMode { get; set; }

        /// <summary>The executable found by scanning the install folder or picked by the user. Null when the game's own play action is used.</summary>
        public string ExecutablePath { get; set; }

        /// <summary>Hash of the app JSON and cover, so auto-sync skips pushes that wouldn't change anything.</summary>
        public string LastHash { get; set; }

        public string LastCoverHash { get; set; }

        public string LastImagePath { get; set; }
    }

    public interface IStreamingServerClient : IDisposable
    {
        ServerKind Kind { get; }

        Task<ServerInfo> GetInfoAsync();

        /// <summary>Adds the app, or replaces the entry previously exported for it.</summary>
        Task UpsertAppAsync(JObject app, ExportEntry previous);

        Task DeleteAppAsync(ExportEntry entry);

        /// <summary>
        /// Closes the running app, which also ends the stream, when it's the one described by <paramref name="entry"/>.
        /// Returns whether a close was sent.
        /// </summary>
        Task<bool> EndStreamIfRunningAsync(ExportEntry entry);

        /// <summary>Stores a PNG on the server and returns the server-side path for the app's image-path.</summary>
        Task<string> UploadCoverAsync(string key, byte[] png);
    }

    internal static class AppJson
    {
        public static List<JObject> ReadApps(JToken root)
        {
            var apps = new List<JObject>();
            var array = (root as JObject)?["apps"] as JArray ?? root as JArray;
            if (array == null)
            {
                return apps;
            }

            foreach (var item in array)
            {
                if (item is JObject obj)
                {
                    apps.Add(obj);
                }
            }

            return apps;
        }

        public static string Str(JObject app, string name)
        {
            return app.Value<string>(name) ?? string.Empty;
        }

        public static string ReadHostName(JToken root)
        {
            return (root as JObject)?.Value<string>("host_name") ?? string.Empty;
        }

        public static bool LooksLikeApollo(JToken root)
        {
            var obj = root as JObject;
            if (obj == null)
            {
                return false;
            }

            if (obj["host_uuid"] != null)
            {
                return true;
            }

            foreach (var app in ReadApps(root))
            {
                if (app["uuid"] != null)
                {
                    return true;
                }
            }

            return false;
        }

        public static string ReadCoverPath(JToken response, string fallback)
        {
            var path = (response as JObject)?.Value<string>("path");
            return string.IsNullOrEmpty(path) ? fallback : path;
        }

        public static JObject CoverBody(string key, byte[] png)
        {
            return new JObject { ["key"] = key, ["data"] = Convert.ToBase64String(png) };
        }
    }
}
