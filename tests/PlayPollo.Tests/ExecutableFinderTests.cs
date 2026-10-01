using System;
using System.IO;
using System.Linq;
using PlayPollo.Mapping;
using Xunit;

namespace PlayPollo.Tests
{
    public class ExecutableFinderTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "playpollo-exe-" + Guid.NewGuid().ToString("N"));

        private void Exe(string relative, int kilobytes = 1)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[kilobytes * 1024]);
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void SingleExecutableInTheRootIsPickedWithoutAsking()
        {
            Exe("valheim.exe", 600);

            var ranked = ExecutableFinder.Find(root, "Valheim");

            Assert.Equal("valheim.exe", ExecutableFinder.PickConfident(ranked).RelativePath);
        }

        [Fact]
        public void FindsAnExecutableInASubfolder()
        {
            Exe(@"bin\x64\Cyberpunk2077.exe", 5000);
            Exe(@"bin\x64\REDEngineErrorReporter.exe", 300);
            Exe("REDprelauncher.exe", 1500);

            var ranked = ExecutableFinder.Find(root, "Cyberpunk 2077");

            Assert.Equal(@"bin\x64\Cyberpunk2077.exe", ExecutableFinder.PickConfident(ranked).RelativePath);
        }

        [Fact]
        public void TwoVariantsOfTheSameGameAreLeftForTheUserToChoose()
        {
            Exe(@"bin\bg3.exe", 100000);
            Exe(@"bin\bg3_dx11.exe", 99000);
            Exe(@"Launcher\LariLauncher.exe", 800);

            var ranked = ExecutableFinder.Find(root, "Baldur's Gate 3");

            Assert.Equal(@"bin\bg3.exe", ranked[0].RelativePath);
            Assert.Equal(@"bin\bg3_dx11.exe", ranked[1].RelativePath);
            Assert.Null(ExecutableFinder.PickConfident(ranked));
        }

        [Fact]
        public void InstallersRuntimesAndCrashHandlersAreIgnored()
        {
            Exe("game.exe", 2000);
            Exe("unins000.exe", 3000);
            Exe("vc_redist.x64.exe", 20000);
            Exe(@"DotNetCore\windowsdesktop-runtime-6.0.11-win-x64.exe", 55000);
            Exe(@"_CommonRedist\DirectX\DXSETUP.exe", 500);
            Exe(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", 900);
            Exe("UnityCrashHandler64.exe", 1000);

            var ranked = ExecutableFinder.Find(root, "Game");

            Assert.Equal(new[] { "game.exe" }, ranked.Select(c => c.RelativePath).ToArray());
        }

        [Fact]
        public void LauncherLikeNamesRankBelowTheGame()
        {
            Exe("Settings.exe", 5000);
            Exe("MyGame.exe", 5000);

            var ranked = ExecutableFinder.Find(root, "My Game");

            Assert.Equal("MyGame.exe", ranked[0].RelativePath);
        }

        [Fact]
        public void NothingFoundForAMissingOrEmptyFolder()
        {
            Assert.Empty(ExecutableFinder.Find(Path.Combine(root, "missing"), "x"));
            Assert.Empty(ExecutableFinder.Find(null, "x"));
            Directory.CreateDirectory(root);
            Assert.Empty(ExecutableFinder.Find(root, "x"));
            Assert.Null(ExecutableFinder.PickConfident(ExecutableFinder.Find(root, "x")));
        }

        [Fact]
        public void DoesNotDescendIntoVeryDeepFolders()
        {
            Exe(@"a\b\c\d\e\f\deep.exe", 5000);

            Assert.Empty(ExecutableFinder.Find(root, "Deep"));
        }

        [Theory]
        [InlineData("Baldur's Gate 3", "bg3")]
        [InlineData("Half-Life 2", "hl2")]
        [InlineData("ELDEN RING", "er")]
        [InlineData("", "")]
        public void InitialsKeepNumbersWhole(string name, string expected)
        {
            Assert.Equal(expected, ExecutableFinder.Initials(name));
        }

        [Fact]
        public void NormalizeKeepsLettersAndDigitsOnly()
        {
            Assert.Equal("baldursgate3", ExecutableFinder.Normalize("Baldur's Gate 3™"));
        }
    }
}
