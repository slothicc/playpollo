using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using PlayPollo.Models;

namespace PlayPollo.Server
{
    public class DetectedServer
    {
        public ServerKind Kind { get; set; }

        /// <summary>Folder that contains sunshine.exe (Apollo ships the same executable name).</summary>
        public string InstallPath { get; set; }

        public string ConfigDir => Path.Combine(InstallPath, "config");

        /// <summary>Web UI / API port from sunshine.conf (base port + 1), or the default 47990.</summary>
        public int WebPort { get; set; } = 47990;
    }

    public static class ServerDetector
    {
        private const string ExeName = "sunshine.exe";
        private const int DefaultBasePort = 47989;

        /// <summary>Finds a server using the custom path first, then the Windows services, then the default install folders.</summary>
        public static DetectedServer Detect(string customPath)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                candidates.Add(customPath);
            }

            candidates.AddRange(FromServices());

            // Playnite is a 32-bit process, so %ProgramFiles% points at "Program Files (x86)" for it. The servers install
            // as 64-bit, so check the native folder (ProgramW6432) first.
            var programFolders = new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" }
                .Select(Environment.GetEnvironmentVariable)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var programFiles in programFolders)
            {
                candidates.Add(Path.Combine(programFiles, "Apollo"));
                candidates.Add(Path.Combine(programFiles, "Sunshine"));
            }

            return DetectFromPaths(candidates);
        }

        /// <summary>The first candidate that contains sunshine.exe wins. Separate from Detect so it can be tested.</summary>
        public static DetectedServer DetectFromPaths(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                var path = candidate.Trim().Trim('"');
                if (File.Exists(Path.Combine(path, ExeName)))
                {
                    var server = new DetectedServer
                    {
                        InstallPath = path,
                        Kind = GuessKind(path)
                    };
                    server.WebPort = ReadWebPort(server.ConfigDir);
                    return server;
                }
            }

            return null;
        }

        /// <summary>Folder name is the only offline hint. The API probe settles it for ServerKind.Auto.</summary>
        public static ServerKind GuessKind(string installPath)
        {
            var name = new DirectoryInfo(installPath.TrimEnd('\\', '/')).Name;
            if (name.IndexOf("apollo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return ServerKind.Apollo;
            }

            return name.IndexOf("sunshine", StringComparison.OrdinalIgnoreCase) >= 0 ? ServerKind.Sunshine : ServerKind.Auto;
        }

        /// <summary>Reads "port" from sunshine.conf. The web UI listens on the base port + 1.</summary>
        public static int ReadWebPort(string configDir)
        {
            var basePort = DefaultBasePort;
            try
            {
                var conf = Path.Combine(configDir, "sunshine.conf");
                if (File.Exists(conf))
                {
                    foreach (var line in File.ReadAllLines(conf))
                    {
                        var parts = line.Split(new[] { '=' }, 2);
                        if (parts.Length == 2 && parts[0].Trim() == "port" && int.TryParse(parts[1].Trim(), out var parsed))
                        {
                            basePort = parsed;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // An unreadable config just means we use the default port.
            }
            catch (UnauthorizedAccessException)
            {
            }

            return basePort + 1;
        }

        private static IEnumerable<string> FromServices()
        {
            foreach (var service in new[] { "ApolloService", "SunshineService" })
            {
                string imagePath = null;
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service))
                    {
                        imagePath = key?.GetValue("ImagePath") as string;
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException)
                {
                }

                var folder = InstallFolderFromServiceImagePath(imagePath);
                if (folder != null)
                {
                    yield return folder;
                }
            }
        }

        /// <summary>
        /// The service runs tools\sunshinesvc.exe, so the install folder is two levels up. Windows stores the path
        /// unquoted when the service has no arguments, even when it contains spaces ("C:\Program Files\Apollo\...").
        /// </summary>
        public static string InstallFolderFromServiceImagePath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            var exe = imagePath.Trim();
            if (exe.StartsWith("\""))
            {
                var close = exe.IndexOf('"', 1);
                exe = close > 0 ? exe.Substring(1, close - 1) : exe.Substring(1);
            }
            else
            {
                // Unquoted: the executable path ends at the first ".exe", and anything after it is arguments.
                var end = exe.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end >= 0)
                {
                    exe = exe.Substring(0, end + 4);
                }
            }

            var tools = Path.GetDirectoryName(exe);
            return string.IsNullOrEmpty(tools) ? null : Path.GetDirectoryName(tools);
        }
    }
}
