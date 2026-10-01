using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Playnite.SDK.Models;
using PlayPollo.Models;

namespace PlayPollo.Mapping
{
    public class MapperOptions
    {
        public LaunchMode LaunchMode { get; set; }

        public int ExitTimeoutSeconds { get; set; } = 5;

        /// <summary>Server-side path of the uploaded cover. Empty means no image.</summary>
        public string ImagePath { get; set; }

        /// <summary>
        /// Executable to run in Executable mode, found by <see cref="ExecutableFinder"/> or picked by the user. Wins over the
        /// game's own play action. Null means use the play action, if it has a file-based one.
        /// </summary>
        public string ExecutablePath { get; set; }

        /// <summary>Playnite's own variable expansion ({InstallDir} and friends).</summary>
        public Func<Game, string, string> ExpandVariables { get; set; } = (game, text) => text;
    }

    public class MappedApp
    {
        public JObject App { get; set; }

        /// <summary>The mode actually used. Differs from the requested one when the game has no file-based play action.</summary>
        public LaunchMode EffectiveMode { get; set; }
    }

    public static class GameAppMapper
    {
        public static MappedApp Map(Game game, MapperOptions options)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            var mode = options.LaunchMode;
            string cmd = null;
            string workingDir = string.Empty;

            if (mode == LaunchMode.Executable)
            {
                cmd = BuildExecutableCommand(game, options, out workingDir);
                if (cmd == null)
                {
                    mode = LaunchMode.Uri;
                }
            }

            if (mode == LaunchMode.Uri)
            {
                cmd = BuildUriCommand(game);
                workingDir = string.Empty;
            }

            var app = new JObject
            {
                ["name"] = game.Name ?? string.Empty,
                ["cmd"] = cmd,
                ["working-dir"] = workingDir,
                ["image-path"] = options.ImagePath ?? string.Empty,
                ["output"] = string.Empty,
                ["elevated"] = false,
                ["auto-detach"] = true,
                ["wait-all"] = true,
                ["exit-timeout"] = options.ExitTimeoutSeconds,
                ["exclude-global-prep-cmd"] = false,
                ["prep-cmd"] = new JArray(),
                ["detached"] = new JArray()
            };

            return new MappedApp { App = app, EffectiveMode = mode };
        }

        public static string BuildUriCommand(Game game)
        {
            return "cmd /c start \"\" \"playnite://playnite/start/" + game.Id + "\"";
        }

        /// <summary>True when the game itself has a play action that runs a file. Library plugins like Steam usually don't.</summary>
        public static bool HasFilePlayAction(Game game)
        {
            return FilePlayAction(game) != null;
        }

        private static GameAction FilePlayAction(Game game)
        {
            var action = game.GameActions?.FirstOrDefault(a => a.IsPlayAction && a.Type == GameActionType.File);
            return action != null && !string.IsNullOrWhiteSpace(action.Path) ? action : null;
        }

        private static string Quote(string path)
        {
            return path.Contains(" ") && !path.StartsWith("\"") ? "\"" + path + "\"" : path;
        }

        private static string BuildExecutableCommand(Game game, MapperOptions options, out string workingDir)
        {
            workingDir = string.Empty;
            if (!string.IsNullOrWhiteSpace(options.ExecutablePath))
            {
                workingDir = Path.GetDirectoryName(options.ExecutablePath) ?? string.Empty;
                return Quote(options.ExecutablePath);
            }

            var action = FilePlayAction(game);
            if (action == null || string.IsNullOrWhiteSpace(action.Path))
            {
                return null;
            }

            var path = options.ExpandVariables(game, action.Path);
            var args = string.IsNullOrWhiteSpace(action.Arguments) ? string.Empty : options.ExpandVariables(game, action.Arguments);

            var dir = string.IsNullOrWhiteSpace(action.WorkingDir) ? game.InstallDirectory : action.WorkingDir;
            workingDir = string.IsNullOrWhiteSpace(dir) ? string.Empty : options.ExpandVariables(game, dir);

            var quoted = Quote(path);
            return args.Length == 0 ? quoted : quoted + " " + args;
        }
    }
}
