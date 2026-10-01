using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PlayPollo.Mapping
{
    public class ExecutableCandidate
    {
        public string FullPath { get; set; }

        /// <summary>Path relative to the install folder, which is what we show the user.</summary>
        public string RelativePath { get; set; }

        public long Size { get; set; }

        /// <summary>Null when the PE header couldn't be read.</summary>
        public bool? Is64Bit { get; set; }

        public int Score { get; set; }
    }

    /// <summary>
    /// Finds the executable that starts a game by scanning its install folder. Library plugins such as Steam supply the
    /// play action themselves, so the game has no file action for us to read. The scan ranks every plausible .exe and
    /// reports whether the best one is clearly ahead of the rest, so the caller only asks the user when it has to.
    /// </summary>
    public static class ExecutableFinder
    {
        /// <summary>The best candidate must lead the runner-up by at least this much to be picked without asking.</summary>
        public const int ConfidentLead = 30;

        private const int MaxDepth = 4;
        private const int MaxDirectories = 3000;

        // Folders that hold installers, runtimes and anti-cheat rather than the game.
        private static readonly string[] SkippedDirectories =
        {
            "_commonredist", "commonredist", "redist", "redistributable", "redistributables", "directx", "vcredist", "dotnet", "dotnetcore",
            "installer", "__installer", "easyanticheat", "battleye", "eac", "crashreport", "crashpad", "crashreporter",
            "_isetup", "support", "prerequisites", "prereqs", "__macosx"
        };

        // Names of helper programs that are never the game itself.
        private static readonly Regex JunkName = new Regex(
            @"^(unins.*|uninstall.*|setup.*|install.*|dxsetup|dxwebsetup|vc_?redist.*|vcredist.*|dotnet.*|ndp.*|oalinst|" +
            @"windowsdesktop-runtime.*|aspnetcore-runtime.*|crashpad\w*|crashreport\w*|crashhandler\w*|unitycrashhandler\w*|easyanticheat\w*|" +
            @"beservice\w*|beclient\w*|notification_helper|cefsharp\w*|cefprocess|ue4prereqsetup\w*|ueprereqsetup\w*|" +
            @"createdump|bugsplat\w*|sendreport|reporter|dumpuploader|wermgr)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] SuspectWords =
            { "launcher", "config", "settings", "editor", "benchmark", "server", "updater", "patcher", "modmanager", "tool", "setup" };

        private static readonly HashSet<string> BinaryFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bin", "bin64", "binaries", "win64", "x64", "win32", "game" };

        /// <summary>All plausible executables, best first.</summary>
        public static List<ExecutableCandidate> Find(string installDirectory, string gameName)
        {
            var found = new List<ExecutableCandidate>();
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return found;
            }

            var root = installDirectory.TrimEnd('\\', '/');
            var budget = MaxDirectories;
            Scan(root, root, 0, found, ref budget);

            var name = Normalize(gameName);
            var initials = Initials(gameName);
            foreach (var candidate in found)
            {
                candidate.Score = ScoreOf(candidate, name, initials);
            }

            return found.OrderByDescending(c => c.Score).ThenBy(c => c.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The top candidate if it's clearly the one, otherwise null (the caller should ask).</summary>
        public static ExecutableCandidate PickConfident(IList<ExecutableCandidate> ranked)
        {
            if (ranked == null || ranked.Count == 0)
            {
                return null;
            }

            if (ranked.Count == 1 || ranked[0].Score - ranked[1].Score >= ConfidentLead)
            {
                return ranked[0];
            }

            return null;
        }

        private static void Scan(string root, string dir, int depth, List<ExecutableCandidate> found, ref int budget)
        {
            if (budget-- <= 0)
            {
                return;
            }

            try
            {
                foreach (var file in Directory.GetFiles(dir, "*.exe"))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    if (JunkName.IsMatch(baseName))
                    {
                        continue;
                    }

                    found.Add(new ExecutableCandidate
                    {
                        FullPath = file,
                        RelativePath = file.Substring(root.Length).TrimStart('\\', '/'),
                        Size = new FileInfo(file).Length,
                        Is64Bit = ReadIs64Bit(file)
                    });
                }

                if (depth >= MaxDepth)
                {
                    return;
                }

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Scan(root, sub, depth + 1, found, ref budget);
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }

        private static int ScoreOf(ExecutableCandidate candidate, string gameName, string initials)
        {
            var score = 0;
            var exeName = Normalize(Path.GetFileNameWithoutExtension(candidate.FullPath));
            var parts = candidate.RelativePath.Split('\\', '/');
            var depth = parts.Length - 1;

            if (exeName.Length > 0 && gameName.Length > 0)
            {
                if (exeName == gameName || (initials.Length >= 2 && exeName == initials))
                {
                    score += 60;
                }
                else if ((exeName.Length >= 4 && gameName.Contains(exeName)) ||
                         (exeName.Length >= 4 && exeName.StartsWith(gameName)) ||
                         (initials.Length >= 3 && exeName.StartsWith(initials)))
                {
                    score += 40;
                }
            }

            score -= 4 * depth;

            if (parts.Take(parts.Length - 1).Any(p => BinaryFolders.Contains(p)))
            {
                score += 10;
            }

            // Bigger programs are more likely to be the game than a helper. 1 MB gives 4 points, capped at 20.
            score += (int)Math.Min(20, Math.Log(candidate.Size / 1048576.0 + 1, 2) * 4);

            if (candidate.Is64Bit == true)
            {
                score += 8;
            }

            var lowered = Path.GetFileNameWithoutExtension(candidate.FullPath).ToLowerInvariant();
            if (SuspectWords.Any(w => lowered.Contains(w)))
            {
                score -= 25;
            }

            return score;
        }

        /// <summary>Lowercase letters and digits only, so "Baldur's Gate 3" and "baldurs_gate_3" compare equal.</summary>
        public static string Normalize(string text)
        {
            var sb = new StringBuilder();
            foreach (var c in text ?? string.Empty)
            {
                if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }

            return sb.ToString();
        }

        /// <summary>First letter of each word, with numbers kept whole: "Baldur's Gate 3" gives "bg3".</summary>
        public static string Initials(string gameName)
        {
            var sb = new StringBuilder();
            foreach (var word in Regex.Split(gameName ?? string.Empty, @"[^A-Za-z0-9]+").Where(w => w.Length > 0))
            {
                // "Baldur's" splits into "Baldur" and "s", so an apostrophe shouldn't add an initial.
                if (word.Length == 1 && char.IsLetter(word[0]))
                {
                    continue;
                }

                sb.Append(char.IsDigit(word[0]) ? word : word.Substring(0, 1));
            }

            return sb.ToString().ToLowerInvariant();
        }

        private static bool? ReadIs64Bit(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new BinaryReader(stream))
                {
                    stream.Position = 0x3C;
                    var peOffset = reader.ReadInt32();
                    if (peOffset <= 0 || peOffset > stream.Length - 6)
                    {
                        return null;
                    }

                    stream.Position = peOffset;
                    if (reader.ReadUInt32() != 0x00004550)
                    {
                        return null;
                    }

                    var machine = reader.ReadUInt16();
                    if (machine == 0x8664 || machine == 0xAA64)
                    {
                        return true;
                    }

                    return machine == 0x14C ? (bool?)false : null;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is EndOfStreamException)
            {
                return null;
            }
        }
    }
}
