using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Playnite.SDK.Models;
using PlayPollo.Models;
using PlayPollo.Server;

namespace PlayPollo.Mapping
{
    public enum ExportStatus
    {
        Added,
        Updated,
        Unchanged,
        Removed,
        NotExported,

        /// <summary>The user was asked which executable to use and cancelled, so nothing was exported.</summary>
        Skipped
    }

    public class ExportOutcome
    {
        public ExportStatus Status { get; set; }

        /// <summary>Executable launch was requested but no executable could be found, so the Playnite URI was used.</summary>
        public bool FellBackToUri { get; set; }

        /// <summary>The cover couldn't be converted or stored; the app was still exported.</summary>
        public string CoverWarning { get; set; }
    }

    public class ExportService
    {
        private readonly ExportStore store;
        private readonly Func<Game, string> resolveCoverPath;
        private readonly Func<Game, string, string> expandVariables;
        private readonly string coverDirectory;

        public ExportService(ExportStore store, string coverDirectory, Func<Game, string> resolveCoverPath, Func<Game, string, string> expandVariables)
        {
            this.store = store;
            this.coverDirectory = coverDirectory;
            this.resolveCoverPath = resolveCoverPath;
            this.expandVariables = expandVariables;
        }

        public ExportStore Store => store;

        /// <summary>
        /// Asked which executable to use when the scan can't tell. Return null to skip the game. Never called for
        /// background syncs. With no chooser set, the best-ranked executable is used.
        /// </summary>
        public Func<Game, IReadOnlyList<ExecutableCandidate>, ExecutableCandidate> ExecutableChooser { get; set; }

        /// <summary>Whether the server can read files from this PC. Replaceable so tests don't depend on the network setup.</summary>
        public Func<PlayPolloSettings, bool> IsLocalServer { get; set; } = s => ServerLocation.IsLocal(s.Host);

        /// <param name="modeOverride">Launch mode to use. Null keeps the game's remembered mode, or the default for a new game.</param>
        /// <param name="onlyIfChanged">Skip the push when nothing differs from the last export. Auto-sync uses this, and it never prompts.</param>
        /// <param name="pickExecutable">Ask which executable to use even if one is remembered or obvious.</param>
        public async Task<ExportOutcome> ExportAsync(
            IStreamingServerClient client, PlayPolloSettings settings, Game game, LaunchMode? modeOverride, bool onlyIfChanged, bool pickExecutable = false)
        {
            var previous = store.Get(game.Id);
            var requestedMode = modeOverride ?? previous?.LaunchMode ?? settings.DefaultLaunchMode;
            var outcome = new ExportOutcome();

            string executablePath = null;
            if (requestedMode == LaunchMode.Executable && (pickExecutable || !GameAppMapper.HasFilePlayAction(game)))
            {
                if (!TryResolveExecutable(game, previous, pickExecutable, !onlyIfChanged, out executablePath))
                {
                    outcome.Status = ExportStatus.Skipped;
                    return outcome;
                }
            }

            var imagePath = previous?.LastImagePath;
            var coverHash = previous?.LastCoverHash;
            if (settings.ExportCovers)
            {
                var cover = CoverExporter.ToPng(resolveCoverPath(game));
                if (cover == null)
                {
                    outcome.CoverWarning = "No usable cover image.";
                }
                else
                {
                    try
                    {
                        var result = await PublishCoverAsync(client, settings, game, previous, cover).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(result))
                        {
                            imagePath = result;
                            coverHash = cover.Hash;
                        }
                    }
                    catch (ServerException ex) when (ex.Kind == ServerErrorKind.Rejected)
                    {
                        outcome.CoverWarning = ex.Message;
                    }
                    catch (IOException ex)
                    {
                        outcome.CoverWarning = ex.Message;
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        outcome.CoverWarning = ex.Message;
                    }
                }
            }
            else
            {
                imagePath = string.Empty;
                coverHash = null;
            }

            var mapped = GameAppMapper.Map(game, new MapperOptions
            {
                LaunchMode = requestedMode,
                ExecutablePath = executablePath,
                ExitTimeoutSeconds = settings.ExitTimeoutSeconds,
                ImagePath = imagePath,
                ExpandVariables = expandVariables
            });
            outcome.FellBackToUri = requestedMode == LaunchMode.Executable && mapped.EffectiveMode == LaunchMode.Uri;

            var hash = ComputeHash(client, settings, mapped.App.ToString());
            if (onlyIfChanged && previous != null && previous.LastHash == hash)
            {
                outcome.Status = ExportStatus.Unchanged;
                return outcome;
            }

            var uuid = previous?.Uuid;
            if (client.Kind == ServerKind.Apollo)
            {
                uuid = string.IsNullOrEmpty(uuid) ? Guid.NewGuid().ToString().ToUpperInvariant() : uuid;
                mapped.App["uuid"] = uuid;
            }

            await client.UpsertAppAsync(mapped.App, previous).ConfigureAwait(false);

            store.Set(game.Id, new ExportEntry
            {
                Uuid = uuid,
                LastName = mapped.App.Value<string>("name"),
                LastCmd = mapped.App.Value<string>("cmd"),
                LaunchMode = requestedMode,
                ExecutablePath = mapped.EffectiveMode == LaunchMode.Executable ? executablePath : null,
                LastHash = hash,
                LastCoverHash = coverHash,
                LastImagePath = imagePath
            });

            outcome.Status = previous == null ? ExportStatus.Added : ExportStatus.Updated;
            return outcome;
        }

        public async Task<ExportOutcome> RemoveAsync(IStreamingServerClient client, Game game)
        {
            var entry = store.Get(game.Id);
            if (entry == null)
            {
                return new ExportOutcome { Status = ExportStatus.NotExported };
            }

            await client.DeleteAppAsync(entry).ConfigureAwait(false);
            store.Remove(game.Id);
            return new ExportOutcome { Status = ExportStatus.Removed };
        }

        /// <summary>
        /// Picks the executable: the remembered one, an obvious one from the scan, or whatever the user chooses.
        /// Returns false when the user cancelled the choice. A null path with true means nothing was found.
        /// </summary>
        private bool TryResolveExecutable(Game game, ExportEntry previous, bool forceChoice, bool mayAsk, out string path)
        {
            path = null;
            var remembered = previous?.ExecutablePath;
            if (!forceChoice && !string.IsNullOrEmpty(remembered) && File.Exists(remembered))
            {
                path = remembered;
                return true;
            }

            var ranked = ExecutableFinder.Find(game.InstallDirectory, game.Name);
            if (ranked.Count == 0)
            {
                return true;
            }

            var chosen = forceChoice ? null : ExecutableFinder.PickConfident(ranked);
            if (chosen == null)
            {
                chosen = mayAsk && ExecutableChooser != null ? ExecutableChooser(game, ranked) : ranked[0];
                if (chosen == null)
                {
                    return false;
                }
            }

            path = chosen.FullPath;
            return true;
        }

        /// <summary>
        /// Gets the cover to where the server can read it and returns the path for the app's image-path.
        /// Sunshine and Apollo write uploaded covers in text mode on Windows, which turns every 0x0A byte into 0x0D 0x0A and
        /// corrupts the PNG. When the server is on this PC we skip the upload and point it at our own copy instead.
        /// </summary>
        private async Task<string> PublishCoverAsync(IStreamingServerClient client, PlayPolloSettings settings, Game game, ExportEntry previous, CoverImage cover)
        {
            if (!IsLocalServer(settings))
            {
                if (cover.Hash == previous?.LastCoverHash && !string.IsNullOrEmpty(previous?.LastImagePath))
                {
                    return previous.LastImagePath;
                }

                return await client.UploadCoverAsync(game.Id.ToString(), cover.Png).ConfigureAwait(false);
            }

            var target = Path.Combine(coverDirectory, game.Id + ".png");
            var upToDate = cover.Hash == previous?.LastCoverHash && previous?.LastImagePath == target && File.Exists(target);
            if (!upToDate)
            {
                Directory.CreateDirectory(coverDirectory);
                File.WriteAllBytes(target, cover.Png);
            }

            return target;
        }

        // The server address is part of the hash so switching servers makes auto-sync push again.
        private static string ComputeHash(IStreamingServerClient client, PlayPolloSettings settings, string appJson)
        {
            var text = client.Kind + "|" + settings.Host + ":" + settings.Port + "|" + appJson;
            return CoverExporter.Sha256(Encoding.UTF8.GetBytes(text));
        }
    }
}
