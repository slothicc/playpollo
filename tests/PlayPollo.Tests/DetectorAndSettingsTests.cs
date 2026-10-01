using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PlayPollo.Mapping;
using PlayPollo.Models;
using PlayPollo.Server;
using Xunit;

namespace PlayPollo.Tests
{
    public class DetectorAndSettingsTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "playpollo-detect-" + Guid.NewGuid().ToString("N"));

        private string MakeInstall(string name, string conf = null)
        {
            var path = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(path, "config"));
            File.WriteAllText(Path.Combine(path, "sunshine.exe"), string.Empty);
            if (conf != null)
            {
                File.WriteAllText(Path.Combine(path, "config", "sunshine.conf"), conf);
            }

            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void DetectsApolloByFolderName()
        {
            var server = ServerDetector.DetectFromPaths(new[] { MakeInstall("Apollo") });

            Assert.Equal(ServerKind.Apollo, server.Kind);
            Assert.Equal(47990, server.WebPort);
        }

        [Fact]
        public void DetectsSunshineByFolderName()
        {
            var server = ServerDetector.DetectFromPaths(new[] { MakeInstall("Sunshine") });

            Assert.Equal(ServerKind.Sunshine, server.Kind);
        }

        [Fact]
        public void CustomFolderNameLeavesTheKindToTheApiProbe()
        {
            var server = ServerDetector.DetectFromPaths(new[] { MakeInstall("streaming") });

            Assert.Equal(ServerKind.Auto, server.Kind);
        }

        [Fact]
        public void FirstCandidateContainingTheExecutableWins()
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var apollo = MakeInstall("Apollo");

            var server = ServerDetector.DetectFromPaths(new[] { "", empty, Path.Combine(root, "missing"), apollo, MakeInstall("Sunshine") });

            Assert.Equal(apollo, server.InstallPath);
        }

        [Fact]
        public void ReturnsNullWhenNothingIsInstalled()
        {
            Assert.Null(ServerDetector.DetectFromPaths(new[] { Path.Combine(root, "nope") }));
        }

        [Fact]
        public void WebPortFollowsTheConfiguredBasePort()
        {
            var server = ServerDetector.DetectFromPaths(new[] { MakeInstall("Sunshine", "sunshine_name = x\nport = 50000\n") });

            Assert.Equal(50001, server.WebPort);
        }

        [Fact]
        public void QuotedPathsAreAccepted()
        {
            var path = MakeInstall("Apollo");

            Assert.NotNull(ServerDetector.DetectFromPaths(new[] { "\"" + path + "\"" }));
        }

        [Theory]
        [InlineData("\"C:\\Program Files\\Apollo\\tools\\sunshinesvc.exe\"", "C:\\Program Files\\Apollo")]
        [InlineData("C:\\Sun\\tools\\sunshinesvc.exe", "C:\\Sun")]
        // Windows stores a service's path unquoted, spaces and all, when it has no arguments. This is what Apollo registers.
        [InlineData("C:\\Program Files\\Apollo\\tools\\sunshinesvc.exe", "C:\\Program Files\\Apollo")]
        [InlineData("C:\\Program Files\\Sunshine\\tools\\sunshinesvc.exe --some-arg", "C:\\Program Files\\Sunshine")]
        [InlineData("\"C:\\Program Files\\Apollo\\tools\\sunshinesvc.exe\" --some-arg", "C:\\Program Files\\Apollo")]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void InstallFolderIsDerivedFromTheServiceImagePath(string imagePath, string expected)
        {
            Assert.Equal(expected, ServerDetector.InstallFolderFromServiceImagePath(imagePath));
        }

        [Theory]
        [InlineData("localhost", true)]
        [InlineData("LOCALHOST", true)]
        [InlineData("127.0.0.1", true)]
        [InlineData("[::1]", true)]
        [InlineData("", true)]
        [InlineData("8.8.8.8", false)]
        [InlineData("some-other-pc", false)]
        [InlineData("gaming.example.com", false)]
        public void ServerLocationRecognisesThisPc(string host, bool expected)
        {
            Assert.Equal(expected, ServerLocation.IsLocal(host));
        }

        [Fact]
        public void ServerLocationRecognisesTheMachineName()
        {
            Assert.True(ServerLocation.IsLocal(Environment.MachineName));
            Assert.True(ServerLocation.IsLocal(Environment.MachineName.ToLowerInvariant() + ".lan"));
        }

        [Fact]
        public void PasswordRoundTripsAndIsNotStoredInPlainText()
        {
            var settings = new PlayPolloSettings { Password = "hunter2" };

            Assert.Equal("hunter2", settings.Password);
            Assert.DoesNotContain("hunter2", settings.ProtectedPassword);
        }

        [Fact]
        public void UndecryptablePasswordReadsAsEmpty()
        {
            var settings = new PlayPolloSettings { ProtectedPassword = "not-a-valid-blob" };

            Assert.Equal(string.Empty, settings.Password);
        }

        [Fact]
        public void CloneCopiesValuesButNotSubscribers()
        {
            var original = new PlayPolloSettings { Host = "a", Port = 1 };
            var notified = 0;
            original.PropertyChanged += (s, e) => notified++;

            var clone = original.Clone();
            clone.Host = "b";

            Assert.Equal("a", original.Host);
            Assert.Equal(0, notified);
            Assert.Equal(1, clone.Port);
        }

        [Fact]
        public async Task DebouncedQueueBatchesAndDeduplicates()
        {
            var batches = new List<IReadOnlyList<int>>();
            var done = new TaskCompletionSource<bool>();
            using (var queue = new DebouncedQueue<int>(TimeSpan.FromMilliseconds(80), items =>
            {
                batches.Add(items);
                done.TrySetResult(true);
                return Task.CompletedTask;
            }))
            {
                queue.Enqueue(1);
                queue.Enqueue(2);
                queue.Enqueue(1);

                await Task.WhenAny(done.Task, Task.Delay(3000));
            }

            Assert.Single(batches);
            Assert.Equal(2, batches[0].Count);
        }
    }
}
