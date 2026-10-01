using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayPollo.Mapping;
using PlayPollo.Models;
using PlayPollo.Server;
using PlayPollo.Settings;

namespace PlayPollo
{
    public class PlayPolloPlugin : GenericPlugin
    {
        private const string Section = "Sunshine / Apollo";
        private const string AutoSyncNotificationId = "PlayPollo-autosync";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly PlayPolloSettingsViewModel settingsViewModel;
        private readonly ExportService exportService;
        private readonly DebouncedQueue<Guid> autoSyncQueue;

        public override Guid Id { get; } = Guid.Parse("ad560681-6efd-4f0d-97ac-8f7e63038684");

        private PlayPolloSettings Settings => settingsViewModel.Settings;

        public PlayPolloPlugin(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };

            var saved = LoadPluginSettings<PlayPolloSettings>() ?? new PlayPolloSettings();
            settingsViewModel = new PlayPolloSettingsViewModel(this, saved);
            DetectServerOnFirstRun();

            var store = new ExportStore(Path.Combine(GetPluginUserDataPath(), "exports.json"));
            exportService = new ExportService(
                store,
                Path.Combine(GetPluginUserDataPath(), "covers"),
                game => string.IsNullOrEmpty(game.CoverImage) ? null : PlayniteApi.Database.GetFullFilePath(game.CoverImage),
                (game, text) => PlayniteApi.ExpandGameVariables(game, text))
            {
                ExecutableChooser = ChooseExecutable
            };

            autoSyncQueue = new DebouncedQueue<Guid>(TimeSpan.FromSeconds(2), SyncGamesAsync);
        }

        // On a fresh install, fill in the install folder and port if the server is in a default location.
        private void DetectServerOnFirstRun()
        {
            if (!string.IsNullOrEmpty(Settings.InstallPath))
            {
                return;
            }

            var server = ServerDetector.Detect(null);
            if (server == null)
            {
                return;
            }

            Settings.InstallPath = server.InstallPath;
            Settings.Port = server.WebPort;
            SavePluginSettings(Settings);
        }

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsViewModel;
        }

        public override UserControl GetSettingsView(bool firstRunSettings)
        {
            return new PlayPolloSettingsView { DataContext = settingsViewModel };
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            PlayniteApi.Database.Games.ItemUpdated += OnGamesUpdated;
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            var entry = exportService.Store.Get(args.Game.Id);
            if (entry == null || !Settings.EndStreamWhenGameCloses)
            {
                return;
            }

            // Don't make Playnite wait on the network. Use a snapshot of the settings so edits can't race the call.
            var settings = Settings.Clone();
            var name = args.Game.Name;
            System.Threading.Tasks.Task.Run(() => EndStreamAsync(settings, entry, name));
        }

        private async System.Threading.Tasks.Task EndStreamAsync(PlayPolloSettings settings, ExportEntry entry, string gameName)
        {
            try
            {
                using (var client = await ServerClientFactory.CreateAsync(settings).ConfigureAwait(false))
                {
                    if (await client.EndStreamIfRunningAsync(entry).ConfigureAwait(false))
                    {
                        Logger.Info($"Ended the stream because {gameName} was closed.");
                    }
                }
            }
            catch (ServerException ex)
            {
                // An unreachable server is normal (PC off, other network), so it's only logged. Bad credentials or a changed
                // certificate need the user's attention.
                Logger.Warn(ex, $"Couldn't end the stream after {gameName} closed.");
                if (ex.Kind == ServerErrorKind.Unauthorized || ex.Kind == ServerErrorKind.UntrustedCertificate)
                {
                    PlayniteApi.Notifications.Add(
                        "PlayPollo-endstream", "PlayPollo couldn't end the stream: " + ex.Message, NotificationType.Error);
                }
            }
            catch (Exception ex)
            {
                // Nothing awaits this task, so an unexpected failure would otherwise vanish without a trace.
                Logger.Error(ex, $"Unexpected error ending the stream after {gameName} closed.");
            }
        }

        public override void Dispose()
        {
            PlayniteApi.Database.Games.ItemUpdated -= OnGamesUpdated;
            autoSyncQueue.Dispose();
            base.Dispose();
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var selected = args.Games.ToList();
            if (selected.Count == 0)
            {
                yield break;
            }

            // The menu is rebuilt on every right-click, so it always reflects what's been exported so far.
            var notAdded = selected.Where(g => !exportService.Store.Contains(g.Id)).ToList();
            var added = selected.Where(g => exportService.Store.Contains(g.Id)).ToList();

            // With several games selected, say how many each action applies to.
            string Label(string text, List<Game> games)
            {
                return selected.Count == 1 ? text : $"{text} ({games.Count} game{(games.Count == 1 ? string.Empty : "s")})";
            }

            GameMenuItem Item(string text, List<Game> games, Action<List<Game>> action)
            {
                return new GameMenuItem { MenuSection = Section, Description = Label(text, games), Action = _ => action(games) };
            }

            if (notAdded.Count > 0)
            {
                yield return Item("Add", notAdded, g => ExportGames(g, null, false));
                yield return Item("Add (launch through Playnite)", notAdded, g => ExportGames(g, LaunchMode.Uri, false));
                yield return Item("Add (launch the game's executable)", notAdded, g => ExportGames(g, LaunchMode.Executable, false));
            }

            if (added.Count > 0)
            {
                yield return Item("Update", added, g => ExportGames(g, null, false));
                yield return Item("Update (launch through Playnite)", added, g => ExportGames(g, LaunchMode.Uri, false));
                yield return Item("Update (launch the game's executable)", added, g => ExportGames(g, LaunchMode.Executable, false));
                yield return Item("Update (choose the executable...)", added, g => ExportGames(g, LaunchMode.Executable, true));
                yield return Item("Remove", added, RemoveGames);
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@" + Section,
                Description = "Test connection",
                Action = _ => TestConnection()
            };
            yield return new MainMenuItem
            {
                MenuSection = "@" + Section,
                Description = "Sync all exported games",
                Action = _ => SyncAllExported()
            };
        }

        private void ExportGames(List<Game> games, LaunchMode? mode, bool pickExecutable)
        {
            RunBatch(
                "Sending games to the streaming server...",
                games,
                (client, game) => exportService.ExportAsync(client, Settings, game, mode, false, pickExecutable));
        }

        /// <summary>Lets the user choose between several likely executables. Runs on the UI thread, as the export itself runs on a worker.</summary>
        private ExecutableCandidate ChooseExecutable(Game game, IReadOnlyList<ExecutableCandidate> candidates)
        {
            return PlayniteApi.MainView.UIDispatcher.Invoke(() =>
            {
                var options = candidates.Select(c => new GenericItemOption(c.RelativePath, DescribeExecutable(c))).ToList();
                var picked = PlayniteApi.Dialogs.ChooseItemWithSearch(
                    options,
                    query => options.Where(o => string.IsNullOrEmpty(query) || o.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList(),
                    null,
                    $"Choose the executable that starts {game.Name}");

                return picked == null ? null : candidates.FirstOrDefault(c => c.RelativePath == picked.Name);
            });
        }

        private static string DescribeExecutable(ExecutableCandidate candidate)
        {
            var bits = $"{candidate.Size / 1048576.0:0.#} MB";
            if (candidate.Is64Bit.HasValue)
            {
                bits += candidate.Is64Bit.Value ? ", 64-bit" : ", 32-bit";
            }

            return bits;
        }

        private void RemoveGames(List<Game> games)
        {
            RunBatch(
                "Removing games from the streaming server...",
                games,
                (client, game) => exportService.RemoveAsync(client, game));
        }

        private void SyncAllExported()
        {
            var games = exportService.Store.GameIds
                .Select(id => PlayniteApi.Database.Games.Get(id))
                .Where(g => g != null)
                .ToList();
            if (games.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("No games have been exported yet.", "PlayPollo");
                return;
            }

            RunBatch(
                "Syncing exported games...",
                games,
                (client, game) => exportService.ExportAsync(client, Settings, game, null, false, false));
        }

        private void TestConnection()
        {
            ServerInfo info = null;
            Exception failure = null;
            var snapshot = Settings.Clone();
            PlayniteApi.Dialogs.ActivateGlobalProgress(_ =>
            {
                try
                {
                    using (var client = ServerClientFactory.CreateAsync(snapshot).GetAwaiter().GetResult())
                    {
                        info = client.GetInfoAsync().GetAwaiter().GetResult();
                    }
                }
                catch (ServerException ex)
                {
                    failure = ex;
                }
            }, new GlobalProgressOptions("Connecting to the streaming server...", false) { IsIndeterminate = true });

            if (failure != null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(failure.Message, "PlayPollo");
                return;
            }

            PlayniteApi.Dialogs.ShowMessage($"Connected to {info.Kind}: {info.AppCount} apps.", "PlayPollo");
        }

        /// <summary>
        /// Runs one operation per game against a single server connection, with a cancellable progress dialog.
        /// A server-wide problem (bad credentials, server down) stops the batch; a rejection of one game doesn't.
        /// </summary>
        private void RunBatch(
            string title, List<Game> games, Func<IStreamingServerClient, Game, System.Threading.Tasks.Task<ExportOutcome>> operation)
        {
            var outcomes = new List<ExportOutcome>();
            var failures = new List<string>();
            ServerException fatal = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.ProgressMaxValue = games.Count;
                try
                {
                    using (var client = ServerClientFactory.CreateAsync(Settings).GetAwaiter().GetResult())
                    {
                        for (var i = 0; i < games.Count; i++)
                        {
                            if (progress.CancelToken.IsCancellationRequested)
                            {
                                break;
                            }

                            progress.Text = $"{title}\n{games[i].Name} ({i + 1}/{games.Count})";
                            progress.CurrentProgressValue = i;
                            try
                            {
                                outcomes.Add(operation(client, games[i]).GetAwaiter().GetResult());
                            }
                            catch (ServerException ex) when (ex.Kind == ServerErrorKind.Rejected)
                            {
                                failures.Add($"{games[i].Name}: {ex.Message}");
                            }
                        }
                    }
                }
                catch (ServerException ex)
                {
                    fatal = ex;
                }
            }, new GlobalProgressOptions(title, true) { IsIndeterminate = false });

            if (fatal != null)
            {
                Logger.Error(fatal, "Streaming server operation failed.");
                PlayniteApi.Dialogs.ShowErrorMessage(fatal.Message, "PlayPollo");
                return;
            }

            PlayniteApi.Dialogs.ShowMessage(Summarize(outcomes, failures, games), "PlayPollo");
        }

        internal static string Summarize(List<ExportOutcome> outcomes, List<string> failures, List<Game> games)
        {
            int Count(ExportStatus status) => outcomes.Count(o => o.Status == status);

            var lines = new List<string>();
            var parts = new List<string>();
            void AddPart(int count, string label)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {label}");
                }
            }

            AddPart(Count(ExportStatus.Added), "added");
            AddPart(Count(ExportStatus.Updated), "updated");
            AddPart(Count(ExportStatus.Removed), "removed");
            AddPart(Count(ExportStatus.NotExported), "weren't exported, so there was nothing to remove");
            AddPart(Count(ExportStatus.Skipped), "skipped because no executable was chosen");
            AddPart(Count(ExportStatus.Unchanged), "already up to date");
            AddPart(failures.Count, "failed");

            lines.Add(parts.Count == 0 ? "Nothing was changed." : string.Join(", ", parts) + ".");

            var fellBack = outcomes.Count(o => o.FellBackToUri);
            if (fellBack > 0)
            {
                lines.Add($"{fellBack} game(s) have no executable that could be found (not installed, or none in the install folder), so they launch through Playnite instead.");
            }

            var coverIssues = outcomes.Where(o => !string.IsNullOrEmpty(o.CoverWarning)).ToList();
            if (coverIssues.Count > 0)
            {
                lines.Add($"{coverIssues.Count} game(s) were exported without a new cover image: {coverIssues[0].CoverWarning}");
            }

            if (failures.Count > 0)
            {
                lines.Add(string.Empty);
                lines.AddRange(failures.Take(5));
            }

            var skipped = games.Count - outcomes.Count - failures.Count;
            if (skipped > 0)
            {
                lines.Add($"{skipped} game(s) were skipped because the operation was cancelled.");
            }

            return string.Join("\n", lines);
        }

        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            if (!Settings.AutoSync)
            {
                return;
            }

            foreach (var update in e.UpdatedItems)
            {
                if (exportService.Store.Contains(update.NewData.Id))
                {
                    autoSyncQueue.Enqueue(update.NewData.Id);
                }
            }
        }

        private async System.Threading.Tasks.Task SyncGamesAsync(IReadOnlyList<Guid> ids)
        {
            try
            {
                using (var client = await ServerClientFactory.CreateAsync(Settings).ConfigureAwait(false))
                {
                    foreach (var id in ids)
                    {
                        var game = PlayniteApi.Database.Games.Get(id);
                        if (game != null)
                        {
                            await exportService.ExportAsync(client, Settings, game, null, true).ConfigureAwait(false);
                        }
                    }
                }

                PlayniteApi.Notifications.Remove(AutoSyncNotificationId);
            }
            catch (ServerException ex)
            {
                Logger.Warn(ex, "Auto-sync to the streaming server failed.");
                PlayniteApi.Notifications.Add(
                    AutoSyncNotificationId, "PlayPollo couldn't update the streaming server: " + ex.Message, NotificationType.Error);
            }
        }
    }
}

