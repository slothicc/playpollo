using System;
using System.Collections.ObjectModel;
using Playnite.SDK.Models;
using PlayPollo.Mapping;
using PlayPollo.Models;
using Xunit;

namespace PlayPollo.Tests
{
    public class GameAppMapperTests
    {
        private static Game NewGame(params GameAction[] actions)
        {
            return new Game("Half-Life")
            {
                Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                InstallDirectory = @"C:\Games\HL",
                GameActions = new ObservableCollection<GameAction>(actions)
            };
        }

        private static GameAction FileAction(string path, string args = null, string workingDir = null)
        {
            return new GameAction { Type = GameActionType.File, IsPlayAction = true, Path = path, Arguments = args, WorkingDir = workingDir };
        }

        [Fact]
        public void UriMode_launchesThroughPlaynite()
        {
            var mapped = GameAppMapper.Map(NewGame(), new MapperOptions { LaunchMode = LaunchMode.Uri });

            Assert.Equal(LaunchMode.Uri, mapped.EffectiveMode);
            Assert.Equal("Half-Life", (string)mapped.App["name"]);
            Assert.Equal("cmd /c start \"\" \"playnite://playnite/start/11111111-2222-3333-4444-555555555555\"", (string)mapped.App["cmd"]);
            Assert.Equal(string.Empty, (string)mapped.App["working-dir"]);
            Assert.True((bool)mapped.App["auto-detach"]);
        }

        [Fact]
        public void ExecutableMode_usesPlayActionAndInstallDirectory()
        {
            var game = NewGame(FileAction(@"C:\Games\HL\hl.exe", "-windowed"));

            var mapped = GameAppMapper.Map(game, new MapperOptions { LaunchMode = LaunchMode.Executable });

            Assert.Equal(LaunchMode.Executable, mapped.EffectiveMode);
            Assert.Equal(@"C:\Games\HL\hl.exe -windowed", (string)mapped.App["cmd"]);
            Assert.Equal(@"C:\Games\HL", (string)mapped.App["working-dir"]);
        }

        [Fact]
        public void ExecutableMode_quotesPathsWithSpacesAndPrefersActionWorkingDir()
        {
            var game = NewGame(FileAction(@"C:\Program Files\HL\hl.exe", null, @"C:\Program Files\HL\bin"));

            var mapped = GameAppMapper.Map(game, new MapperOptions { LaunchMode = LaunchMode.Executable });

            Assert.Equal("\"C:\\Program Files\\HL\\hl.exe\"", (string)mapped.App["cmd"]);
            Assert.Equal(@"C:\Program Files\HL\bin", (string)mapped.App["working-dir"]);
        }

        [Fact]
        public void ExecutableMode_expandsPlayniteVariables()
        {
            var game = NewGame(FileAction("{InstallDir}\\hl.exe"));

            var mapped = GameAppMapper.Map(game, new MapperOptions
            {
                LaunchMode = LaunchMode.Executable,
                ExpandVariables = (g, text) => text.Replace("{InstallDir}", g.InstallDirectory)
            });

            Assert.Equal(@"C:\Games\HL\hl.exe", (string)mapped.App["cmd"]);
        }

        [Fact]
        public void ExecutableMode_fallsBackToUriWithoutFileAction()
        {
            var emulated = new GameAction { Type = GameActionType.Emulator, IsPlayAction = true };

            var mapped = GameAppMapper.Map(NewGame(emulated), new MapperOptions { LaunchMode = LaunchMode.Executable });

            Assert.Equal(LaunchMode.Uri, mapped.EffectiveMode);
            Assert.StartsWith("cmd /c start", (string)mapped.App["cmd"]);
        }

        [Fact]
        public void ExecutableMode_ignoresNonPlayFileActions()
        {
            var extra = new GameAction { Type = GameActionType.File, IsPlayAction = false, Path = @"C:\Games\HL\editor.exe" };

            var mapped = GameAppMapper.Map(NewGame(extra), new MapperOptions { LaunchMode = LaunchMode.Executable });

            Assert.Equal(LaunchMode.Uri, mapped.EffectiveMode);
        }

        [Fact]
        public void ImagePathAndExitTimeoutAreCopiedThrough()
        {
            var mapped = GameAppMapper.Map(NewGame(), new MapperOptions { ImagePath = @"C:\covers\x.png", ExitTimeoutSeconds = 12 });

            Assert.Equal(@"C:\covers\x.png", (string)mapped.App["image-path"]);
            Assert.Equal(12, (int)mapped.App["exit-timeout"]);
        }
    }
}
