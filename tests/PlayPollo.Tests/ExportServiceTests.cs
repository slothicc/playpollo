using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK.Models;
using PlayPollo.Mapping;
using PlayPollo.Models;
using PlayPollo.Server;
using Xunit;

namespace PlayPollo.Tests
{
    public class ExportServiceTests : IDisposable
    {
        // 1x1 transparent PNG.
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

        private readonly string dir = Path.Combine(Path.GetTempPath(), "playpollo-tests-" + Guid.NewGuid().ToString("N"));
        private readonly Game game = new Game("Quake") { Id = Guid.NewGuid() };

        private ExportService NewService(string coverPath = null)
        {
            // Treat the server as remote by default so cover tests go through the upload path.
            return new ExportService(new ExportStore(Path.Combine(dir, "exports.json")), Path.Combine(dir, "covers"), g => coverPath, (g, t) => t)
            {
                IsLocalServer = s => false
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public async Task FirstExportAddsAndRemembersTheGame()
        {
            var service = NewService();
            var client = new FakeClient();

            var outcome = await service.ExportAsync(client, TestSettings.Create(), game, null, false);

            Assert.Equal(ExportStatus.Added, outcome.Status);
            Assert.Single(client.Upserts);
            var entry = service.Store.Get(game.Id);
            Assert.False(string.IsNullOrEmpty(entry.Uuid));
            Assert.Equal(entry.Uuid, (string)client.Upserts[0]["uuid"]);
        }

        [Fact]
        public async Task ReExportUpdatesTheSameEntryWithoutDuplicating()
        {
            var service = NewService();
            var client = new FakeClient();
            var settings = TestSettings.Create();

            await service.ExportAsync(client, settings, game, null, false);
            game.Name = "Quake (renamed)";
            var outcome = await service.ExportAsync(client, settings, game, null, false);

            Assert.Equal(ExportStatus.Updated, outcome.Status);
            Assert.Equal(2, client.Upserts.Count);
            Assert.Equal((string)client.Upserts[0]["uuid"], (string)client.Upserts[1]["uuid"]);
            Assert.NotNull(client.PreviousSeen[1]);
            Assert.Equal("Quake (renamed)", (string)client.Upserts[1]["name"]);
        }

        [Fact]
        public async Task AutoSyncSkipsGamesThatHaveNotChanged()
        {
            var service = NewService();
            var client = new FakeClient();
            var settings = TestSettings.Create();
            await service.ExportAsync(client, settings, game, null, false);

            var outcome = await service.ExportAsync(client, settings, game, null, true);

            Assert.Equal(ExportStatus.Unchanged, outcome.Status);
            Assert.Single(client.Upserts);
        }

        [Fact]
        public async Task AutoSyncPushesWhenMetadataChanged()
        {
            var service = NewService();
            var client = new FakeClient();
            var settings = TestSettings.Create();
            await service.ExportAsync(client, settings, game, null, false);
            game.Name = "Quake II";

            var outcome = await service.ExportAsync(client, settings, game, null, true);

            Assert.Equal(ExportStatus.Updated, outcome.Status);
            Assert.Equal(2, client.Upserts.Count);
        }

        [Fact]
        public async Task LaunchModeIsRememberedPerGame()
        {
            var service = NewService();
            var client = new FakeClient();
            var settings = TestSettings.Create();
            game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction>
            {
                new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = @"C:\Quake\q.exe" }
            };

            await service.ExportAsync(client, settings, game, LaunchMode.Executable, false);
            await service.ExportAsync(client, settings, game, null, false);

            Assert.All(client.Upserts, u => Assert.Equal(@"C:\Quake\q.exe", (string)u["cmd"]));
            Assert.Equal(LaunchMode.Executable, service.Store.Get(game.Id).LaunchMode);
        }

        [Fact]
        public async Task ExecutableRequestWithoutFileActionIsFlaggedAsFallback()
        {
            var service = NewService();

            var outcome = await service.ExportAsync(new FakeClient(), TestSettings.Create(), game, LaunchMode.Executable, false);

            Assert.True(outcome.FellBackToUri);
        }

        [Fact]
        public async Task CoverIsConvertedUploadedAndReferencedByTheApp()
        {
            Directory.CreateDirectory(dir);
            var coverFile = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(coverFile, TinyPng);
            var service = NewService(coverFile);
            var client = new FakeClient();
            var settings = TestSettings.Create();
            settings.ExportCovers = true;

            await service.ExportAsync(client, settings, game, null, false);
            await service.ExportAsync(client, settings, game, null, false);

            Assert.Equal(@"C:\Apollo\covers\cover.png", (string)client.Upserts[0]["image-path"]);
            Assert.Equal(1, client.CoverUploads);
        }

        private Game InstalledGame(string name, params string[] exes)
        {
            var install = Path.Combine(dir, "install-" + Guid.NewGuid().ToString("N"));
            foreach (var exe in exes)
            {
                var path = Path.Combine(install, exe);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, new byte[2048]);
            }

            return new Game(name) { Id = Guid.NewGuid(), InstallDirectory = install };
        }

        [Fact]
        public async Task ExecutableModeFindsTheExeInTheInstallFolderWhenThereIsNoPlayAction()
        {
            var steamGame = InstalledGame("Valheim", "valheim.exe", "unins000.exe");
            var service = NewService();
            var client = new FakeClient();

            await service.ExportAsync(client, TestSettings.Create(), steamGame, LaunchMode.Executable, false);

            Assert.Equal(Path.Combine(steamGame.InstallDirectory, "valheim.exe"), (string)client.Upserts[0]["cmd"]);
            Assert.Equal(steamGame.InstallDirectory, (string)client.Upserts[0]["working-dir"]);
            Assert.Equal(Path.Combine(steamGame.InstallDirectory, "valheim.exe"), service.Store.Get(steamGame.Id).ExecutablePath);
        }

        [Fact]
        public async Task ExecutableInASubfolderUsesThatFolderAsWorkingDirectory()
        {
            var steamGame = InstalledGame("Baldur's Gate 3", @"bin\bg3.exe");
            var client = new FakeClient();

            await NewService().ExportAsync(client, TestSettings.Create(), steamGame, LaunchMode.Executable, false);

            Assert.Equal(Path.Combine(steamGame.InstallDirectory, "bin"), (string)client.Upserts[0]["working-dir"]);
        }

        [Fact]
        public async Task AmbiguousExecutablesAskTheUserAndRememberTheAnswer()
        {
            var bg3 = InstalledGame("Baldur's Gate 3", @"bin\bg3.exe", @"bin\bg3_dx11.exe");
            var service = NewService();
            var asked = 0;
            service.ExecutableChooser = (g, candidates) =>
            {
                asked++;
                return candidates.First(c => c.RelativePath.EndsWith("bg3_dx11.exe"));
            };
            var client = new FakeClient();
            var settings = TestSettings.Create();

            await service.ExportAsync(client, settings, bg3, LaunchMode.Executable, false);
            await service.ExportAsync(client, settings, bg3, null, false);

            Assert.Equal(1, asked);
            Assert.All(client.Upserts, u => Assert.EndsWith("bg3_dx11.exe", (string)u["cmd"]));
        }

        [Fact]
        public async Task CancellingTheExecutableChoiceSkipsTheGame()
        {
            var bg3 = InstalledGame("Baldur's Gate 3", @"bin\bg3.exe", @"bin\bg3_dx11.exe");
            var service = NewService();
            service.ExecutableChooser = (g, candidates) => null;
            var client = new FakeClient();

            var outcome = await service.ExportAsync(client, TestSettings.Create(), bg3, LaunchMode.Executable, false);

            Assert.Equal(ExportStatus.Skipped, outcome.Status);
            Assert.Empty(client.Upserts);
            Assert.False(service.Store.Contains(bg3.Id));
        }

        [Fact]
        public async Task ChoosingTheExecutableAgainOverridesTheRememberedOne()
        {
            var bg3 = InstalledGame("Baldur's Gate 3", @"bin\bg3.exe", @"bin\bg3_dx11.exe");
            var service = NewService();
            var answers = new System.Collections.Generic.Queue<string>(new[] { "bg3.exe", "bg3_dx11.exe" });
            service.ExecutableChooser = (g, candidates) =>
            {
                var wanted = answers.Dequeue();
                return candidates.First(c => c.RelativePath.EndsWith(wanted));
            };
            var client = new FakeClient();
            var settings = TestSettings.Create();

            await service.ExportAsync(client, settings, bg3, LaunchMode.Executable, false);
            await service.ExportAsync(client, settings, bg3, LaunchMode.Executable, false, pickExecutable: true);

            Assert.EndsWith(@"bin\bg3.exe", (string)client.Upserts[0]["cmd"]);
            Assert.EndsWith(@"bin\bg3_dx11.exe", (string)client.Upserts[1]["cmd"]);
        }

        [Fact]
        public async Task BackgroundSyncNeverPrompts()
        {
            var bg3 = InstalledGame("Baldur's Gate 3", @"bin\bg3.exe", @"bin\bg3_dx11.exe");
            var service = NewService();
            service.ExecutableChooser = (g, candidates) => throw new InvalidOperationException("must not prompt");

            var outcome = await service.ExportAsync(new FakeClient(), TestSettings.Create(), bg3, LaunchMode.Executable, true);

            Assert.Equal(ExportStatus.Added, outcome.Status);
        }

        [Fact]
        public async Task ExecutableModeWithNothingToRunFallsBackToTheUri()
        {
            var empty = InstalledGame("Empty");
            var client = new FakeClient();

            var outcome = await NewService().ExportAsync(client, TestSettings.Create(), empty, LaunchMode.Executable, false);

            Assert.True(outcome.FellBackToUri);
            Assert.StartsWith("cmd /c start", (string)client.Upserts[0]["cmd"]);
        }

        [Fact]
        public async Task LocalServerGetsTheCoverAsAFileInsteadOfAnUpload()
        {
            Directory.CreateDirectory(dir);
            var coverFile = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(coverFile, TinyPng);
            var service = NewService(coverFile);
            service.IsLocalServer = s => true;
            var client = new FakeClient();
            var settings = TestSettings.Create();
            settings.ExportCovers = true;

            await service.ExportAsync(client, settings, game, null, false);

            var expected = Path.Combine(dir, "covers", game.Id + ".png");
            Assert.Equal(expected, (string)client.Upserts[0]["image-path"]);
            Assert.Equal(0, client.CoverUploads);
            Assert.True(File.Exists(expected));
            // The file must be a valid PNG, byte for byte, which the server's text-mode upload couldn't guarantee.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, File.ReadAllBytes(expected).Take(8).ToArray());
        }

        [Fact]
        public async Task LocalCoverIsRewrittenWhenTheEntryStillPointsAtAnOldServerUpload()
        {
            Directory.CreateDirectory(dir);
            var coverFile = Path.Combine(dir, "cover.png");
            File.WriteAllBytes(coverFile, TinyPng);
            var service = NewService(coverFile);
            var settings = TestSettings.Create();
            settings.ExportCovers = true;
            var client = new FakeClient();

            // First export while the server counts as remote: the image-path is the server's own (corrupted) upload.
            await service.ExportAsync(client, settings, game, null, false);
            service.IsLocalServer = s => true;
            await service.ExportAsync(client, settings, game, null, false);

            Assert.Equal(Path.Combine(dir, "covers", game.Id + ".png"), (string)client.Upserts[1]["image-path"]);
        }

        [Fact]
        public async Task MissingCoverStillExportsTheApp()
        {
            var service = NewService(Path.Combine(dir, "missing.jpg"));
            var settings = TestSettings.Create();
            settings.ExportCovers = true;

            var outcome = await service.ExportAsync(new FakeClient(), settings, game, null, false);

            Assert.Equal(ExportStatus.Added, outcome.Status);
            Assert.False(string.IsNullOrEmpty(outcome.CoverWarning));
        }

        [Fact]
        public async Task RemoveDeletesOnTheServerAndForgetsTheGame()
        {
            var service = NewService();
            var client = new FakeClient();
            await service.ExportAsync(client, TestSettings.Create(), game, null, false);

            var outcome = await service.RemoveAsync(client, game);

            Assert.Equal(ExportStatus.Removed, outcome.Status);
            Assert.Single(client.Deleted);
            Assert.False(service.Store.Contains(game.Id));
        }

        [Fact]
        public async Task RemoveOfAGameThatWasNeverExportedDoesNotTouchTheServer()
        {
            var service = NewService();
            var client = new FakeClient();

            var outcome = await service.RemoveAsync(client, game);

            Assert.Equal(ExportStatus.NotExported, outcome.Status);
            Assert.Empty(client.Deleted);
        }

        [Fact]
        public async Task StorePersistsAcrossInstances()
        {
            var service = NewService();
            await service.ExportAsync(new FakeClient(), TestSettings.Create(), game, null, false);

            var reloaded = new ExportStore(Path.Combine(dir, "exports.json"));

            Assert.Equal(new[] { game.Id }, reloaded.GameIds.ToArray());
            Assert.Equal(service.Store.Get(game.Id).Uuid, reloaded.Get(game.Id).Uuid);
        }

        [Fact]
        public void CorruptStoreFileIsTreatedAsEmpty()
        {
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "exports.json");
            File.WriteAllText(file, "{ not json");

            var store = new ExportStore(file);

            Assert.Empty(store.GameIds);
        }
    }
}
