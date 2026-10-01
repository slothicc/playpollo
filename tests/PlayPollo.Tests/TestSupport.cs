using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayPollo.Models;
using PlayPollo.Server;

namespace PlayPollo.Tests
{
    public class RecordedRequest
    {
        public HttpMethod Method { get; set; }

        public string Path { get; set; }

        public string AuthorizationScheme { get; set; }

        public string Cookie { get; set; }

        public string ContentType { get; set; }

        public string Body { get; set; }

        public JObject Json => string.IsNullOrEmpty(Body) ? null : JObject.Parse(Body);
    }

    /// <summary>An HttpMessageHandler that answers from a delegate and keeps a log of what it was asked.</summary>
    public class FakeHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> respond;

        public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

        public FakeHandler(Func<RecordedRequest, HttpResponseMessage> respond)
        {
            this.respond = respond;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest
            {
                Method = request.Method,
                Path = request.RequestUri.AbsolutePath,
                AuthorizationScheme = request.Headers.Authorization?.Scheme,
                ContentType = request.Content?.Headers.ContentType?.MediaType,
                Body = request.Content == null ? null : await request.Content.ReadAsStringAsync().ConfigureAwait(false)
            };
            if (request.Headers.TryGetValues("Cookie", out var cookies))
            {
                recorded.Cookie = string.Join(";", cookies);
            }

            Requests.Add(recorded);
            return respond(recorded);
        }

        public static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(JObject.FromObject(body).ToString(), Encoding.UTF8, "application/json")
            };
        }

        public static HttpResponseMessage Status(HttpStatusCode status)
        {
            return new HttpResponseMessage(status);
        }
    }

    public class FakeClient : IStreamingServerClient
    {
        public ServerKind Kind { get; set; } = ServerKind.Apollo;

        public List<JObject> Upserts { get; } = new List<JObject>();

        public List<ExportEntry> PreviousSeen { get; } = new List<ExportEntry>();

        public List<ExportEntry> Deleted { get; } = new List<ExportEntry>();

        public int CoverUploads { get; private set; }

        public List<ExportEntry> StreamEnds { get; } = new List<ExportEntry>();

        public Task<bool> EndStreamIfRunningAsync(ExportEntry entry)
        {
            StreamEnds.Add(entry);
            return Task.FromResult(true);
        }

        public string CoverPath { get; set; } = @"C:\Apollo\covers\cover.png";

        public Task<ServerInfo> GetInfoAsync()
        {
            return Task.FromResult(new ServerInfo { Kind = Kind });
        }

        public Task UpsertAppAsync(JObject app, ExportEntry previous)
        {
            Upserts.Add((JObject)app.DeepClone());
            PreviousSeen.Add(previous);
            return Task.CompletedTask;
        }

        public Task DeleteAppAsync(ExportEntry entry)
        {
            Deleted.Add(entry);
            return Task.CompletedTask;
        }

        public Task<string> UploadCoverAsync(string key, byte[] png)
        {
            CoverUploads++;
            return Task.FromResult(CoverPath);
        }

        public void Dispose()
        {
        }
    }

    public static class TestSettings
    {
        public static PlayPolloSettings Create()
        {
            return new PlayPolloSettings { Host = "localhost", Port = 47990, Username = "admin", ExportCovers = false };
        }
    }
}
