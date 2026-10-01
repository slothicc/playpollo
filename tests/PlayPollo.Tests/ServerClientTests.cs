using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayPollo.Models;
using PlayPollo.Server;
using Xunit;

namespace PlayPollo.Tests
{
    public class ServerClientTests
    {
        private static ServerConnection Connect(FakeHandler handler)
        {
            return new ServerConnection(new Uri("https://localhost:47990/"), "admin", "secret", null, false, handler);
        }

        private static PlayPolloSettings Settings(ServerKind kind = ServerKind.Auto)
        {
            var s = TestSettings.Create();
            s.ServerKind = kind;
            s.Password = "secret";
            return s;
        }

        // A Sunshine-like server: Basic auth, apps without uuids.
        private static FakeHandler SunshineServer(JArray apps)
        {
            return new FakeHandler(r =>
            {
                if (r.AuthorizationScheme != "Basic")
                {
                    return FakeHandler.Status(HttpStatusCode.Unauthorized);
                }

                if (r.Path == "/api/apps" && r.Method == HttpMethod.Get)
                {
                    return FakeHandler.Json(new { apps });
                }

                return FakeHandler.Json(new { status = true });
            });
        }

        // An Apollo-like server: only the session cookie from /api/login is accepted.
        private static FakeHandler ApolloServer(JArray apps, string currentApp = "")
        {
            return new FakeHandler(r =>
            {
                if (r.Path == "/api/login")
                {
                    var ok = r.Json.Value<string>("username") == "admin" && r.Json.Value<string>("password") == "secret";
                    var response = FakeHandler.Status(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
                    return response;
                }

                if (r.AuthorizationScheme == "Basic")
                {
                    return FakeHandler.Status(HttpStatusCode.Unauthorized);
                }

                if (r.Path == "/api/apps" && r.Method == HttpMethod.Get)
                {
                    return FakeHandler.Json(new { apps, host_uuid = "H", host_name = "Gaming PC", current_app = currentApp });
                }

                if (r.Path == "/api/covers/upload")
                {
                    return FakeHandler.Json(new { status = true, path = @"C:\Apollo\covers\k.png" });
                }

                return FakeHandler.Json(new { status = true });
            });
        }

        [Fact]
        public async Task Auto_detectsApolloFromHostUuid()
        {
            using (var client = await ServerClientFactory.CreateAsync(Settings(), false, ApolloServer(new JArray())))
            {
                Assert.Equal(ServerKind.Apollo, client.Kind);
            }
        }

        [Fact]
        public async Task Auto_detectsSunshineWhenAppsHaveNoUuid()
        {
            var apps = new JArray(new JObject { ["name"] = "Desktop" });
            using (var client = await ServerClientFactory.CreateAsync(Settings(), false, SunshineServer(apps)))
            {
                Assert.Equal(ServerKind.Sunshine, client.Kind);
            }
        }

        [Fact]
        public async Task Connection_usesBasicAuthWhenAccepted()
        {
            var handler = SunshineServer(new JArray());
            using (var client = await ServerClientFactory.CreateAsync(Settings(ServerKind.Sunshine), false, handler))
            {
                await client.GetInfoAsync();
            }

            Assert.All(handler.Requests, r => Assert.Equal("Basic", r.AuthorizationScheme));
            Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/login");
        }

        [Fact]
        public async Task Connection_fallsBackToLoginWhenBasicIsRefused()
        {
            var handler = ApolloServer(new JArray());
            using (var client = await ServerClientFactory.CreateAsync(Settings(ServerKind.Apollo), false, handler))
            {
                var info = await client.GetInfoAsync();
                Assert.Equal("Gaming PC", info.HostName);
            }

            // Basic first, then login, then the retried request without Basic.
            Assert.Equal(new[] { "/api/apps", "/api/login", "/api/apps" }, handler.Requests.Take(3).Select(r => r.Path).ToArray());
            Assert.Null(handler.Requests[2].AuthorizationScheme);
        }

        [Fact]
        public async Task Connection_remembersCookieModeAndSkipsBasicAfterwards()
        {
            var handler = ApolloServer(new JArray());
            using (var client = await ServerClientFactory.CreateAsync(Settings(ServerKind.Apollo), false, handler))
            {
                await client.GetInfoAsync();
                await client.GetInfoAsync();
            }

            var last = handler.Requests.Last();
            Assert.Equal("/api/apps", last.Path);
            Assert.Null(last.AuthorizationScheme);
            Assert.Equal(1, handler.Requests.Count(r => r.Path == "/api/login"));
        }

        [Fact]
        public async Task Connection_wrongPasswordRaisesUnauthorized()
        {
            var settings = Settings(ServerKind.Apollo);
            settings.Password = "wrong";

            var ex = await Assert.ThrowsAsync<ServerException>(async () =>
            {
                using (var client = await ServerClientFactory.CreateAsync(settings, false, ApolloServer(new JArray())))
                {
                    await client.GetInfoAsync();
                }
            });

            Assert.Equal(ServerErrorKind.Unauthorized, ex.Kind);
        }

        [Fact]
        public async Task Connection_treatsLoginRedirectAsUnauthorized()
        {
            var handler = new FakeHandler(r => FakeHandler.Status(HttpStatusCode.Found));

            var ex = await Assert.ThrowsAsync<ServerException>(async () =>
            {
                using (var client = await ServerClientFactory.CreateAsync(Settings(ServerKind.Sunshine), false, handler))
                {
                    await client.GetInfoAsync();
                }
            });

            Assert.Equal(ServerErrorKind.Unauthorized, ex.Kind);
        }

        [Fact]
        public async Task Apollo_upsertPostsAppWithUuid()
        {
            var handler = ApolloServer(new JArray());
            using (var client = new ApolloClient(Connect(handler)))
            {
                await client.UpsertAppAsync(new JObject { ["name"] = "Quake", ["cmd"] = "q.exe" }, null);
            }

            var post = handler.Requests.Last(r => r.Path == "/api/apps" && r.Method == HttpMethod.Post);
            Assert.Equal("Quake", post.Json.Value<string>("name"));
            Assert.True(Guid.TryParse(post.Json.Value<string>("uuid"), out _));
        }

        [Fact]
        public async Task Apollo_upsertReusesPreviousUuid()
        {
            var handler = ApolloServer(new JArray());
            using (var client = new ApolloClient(Connect(handler)))
            {
                await client.UpsertAppAsync(new JObject { ["name"] = "Quake" }, new ExportEntry { Uuid = "AAAA-1" });
            }

            Assert.Equal("AAAA-1", handler.Requests.Last().Json.Value<string>("uuid"));
        }

        [Fact]
        public async Task Apollo_deletePostsUuid()
        {
            var handler = ApolloServer(new JArray());
            using (var client = new ApolloClient(Connect(handler)))
            {
                await client.DeleteAppAsync(new ExportEntry { Uuid = "AAAA-1" });
            }

            var post = handler.Requests.Last();
            Assert.Equal("/api/apps/delete", post.Path);
            Assert.Equal("AAAA-1", post.Json.Value<string>("uuid"));
        }

        [Fact]
        public async Task Apollo_coverUploadReturnsServerPath()
        {
            var handler = ApolloServer(new JArray());
            using (var client = new ApolloClient(Connect(handler)))
            {
                var path = await client.UploadCoverAsync("k", new byte[] { 1, 2, 3 });

                Assert.Equal(@"C:\Apollo\covers\k.png", path);
            }

            var post = handler.Requests.Last();
            Assert.Equal("k", post.Json.Value<string>("key"));
            Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3 }), post.Json.Value<string>("data"));
        }

        [Fact]
        public async Task Apollo_endStreamClosesTheAppWhenItIsTheRunningOne()
        {
            var handler = ApolloServer(new JArray(), currentApp: "AAAA-1");
            using (var client = new ApolloClient(Connect(handler)))
            {
                Assert.True(await client.EndStreamIfRunningAsync(new ExportEntry { Uuid = "aaaa-1" }));
            }

            var close = handler.Requests.Last();
            Assert.Equal("/api/apps/close", close.Path);
            Assert.Equal(HttpMethod.Post, close.Method);
            // Apollo rejects the call without a JSON content type, even though it ignores the body.
            Assert.Equal("application/json", close.ContentType);
        }

        [Fact]
        public async Task Apollo_endStreamLeavesADifferentRunningAppAlone()
        {
            var handler = ApolloServer(new JArray(), currentApp: "SOMETHING-ELSE");
            using (var client = new ApolloClient(Connect(handler)))
            {
                Assert.False(await client.EndStreamIfRunningAsync(new ExportEntry { Uuid = "AAAA-1" }));
            }

            Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/apps/close");
        }

        [Fact]
        public async Task Apollo_endStreamDoesNothingWhenNothingIsRunning()
        {
            var handler = ApolloServer(new JArray(), currentApp: "");
            using (var client = new ApolloClient(Connect(handler)))
            {
                Assert.False(await client.EndStreamIfRunningAsync(new ExportEntry { Uuid = "AAAA-1" }));
            }

            Assert.DoesNotContain(handler.Requests, r => r.Path == "/api/apps/close");
        }

        [Fact]
        public async Task Sunshine_endStreamClosesTheRunningApp()
        {
            var handler = SunshineServer(new JArray());
            using (var client = new SunshineClient(Connect(handler)))
            {
                Assert.True(await client.EndStreamIfRunningAsync(new ExportEntry { LastName = "Quake" }));
            }

            var close = handler.Requests.Last();
            Assert.Equal("/api/apps/close", close.Path);
            Assert.Equal("application/json", close.ContentType);
        }

        [Fact]
        public async Task Sunshine_newAppIsAppendedWithIndexMinusOne()
        {
            var handler = SunshineServer(new JArray(new JObject { ["name"] = "Desktop", ["cmd"] = "" }));
            using (var client = new SunshineClient(Connect(handler)))
            {
                await client.UpsertAppAsync(new JObject { ["name"] = "Quake", ["cmd"] = "q.exe" }, null);
            }

            Assert.Equal(-1, handler.Requests.Last().Json.Value<int>("index"));
        }

        [Fact]
        public async Task Sunshine_existingAppIsReplacedAtItsCurrentPosition()
        {
            var apps = new JArray(
                new JObject { ["name"] = "Desktop", ["cmd"] = "" },
                new JObject { ["name"] = "Quake", ["cmd"] = "q.exe" });
            var handler = SunshineServer(apps);
            using (var client = new SunshineClient(Connect(handler)))
            {
                var previous = new ExportEntry { LastName = "Quake", LastCmd = "q.exe" };
                await client.UpsertAppAsync(new JObject { ["name"] = "Quake II", ["cmd"] = "q2.exe" }, previous);
            }

            Assert.Equal(1, handler.Requests.Last().Json.Value<int>("index"));
        }

        [Fact]
        public async Task Sunshine_deleteUsesTheCurrentIndex()
        {
            var apps = new JArray(
                new JObject { ["name"] = "Desktop", ["cmd"] = "" },
                new JObject { ["name"] = "Other", ["cmd"] = "o.exe" },
                new JObject { ["name"] = "Quake", ["cmd"] = "q.exe" });
            var handler = SunshineServer(apps);
            using (var client = new SunshineClient(Connect(handler)))
            {
                await client.DeleteAppAsync(new ExportEntry { LastName = "Quake", LastCmd = "q.exe" });
            }

            var delete = handler.Requests.Last();
            Assert.Equal(HttpMethod.Delete, delete.Method);
            Assert.Equal("/api/apps/2", delete.Path);
        }

        [Fact]
        public async Task Sunshine_deleteOfAnAppThatIsAlreadyGoneDoesNothing()
        {
            var handler = SunshineServer(new JArray(new JObject { ["name"] = "Desktop", ["cmd"] = "" }));
            using (var client = new SunshineClient(Connect(handler)))
            {
                await client.DeleteAppAsync(new ExportEntry { LastName = "Quake", LastCmd = "q.exe" });
            }

            Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Delete);
        }
    }
}
