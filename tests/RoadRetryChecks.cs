using System.Net;
using KoreaTerrain;

static class RoadRetryChecks
{
    public static async Task Run()
    {
        int checks = 0;
        foreach (var code in new[] { 504, 503, 502, 408, 429, 400 })
        {
            var handler = new Responses(code, 200);
            using var http = new HttpClient(handler);
            var service = new MapLayerService(http, (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
            bool success = false;
            try { await service.RoadsAsync(new(36 + code / 10000.0, 127), 120, CancellationToken.None); success = true; }
            catch (InvalidOperationException) { }
            bool retryable = code is 504 or 503 or 502 or 408;
            if (success != retryable || handler.Count != (retryable ? 2 : 1)) throw new Exception("Retry policy failed " + code);
            checks++;
        }
        var repeated = new Responses(504, 504, 200);
        using (var http = new HttpClient(repeated))
        {
            bool failed = false;
            try { await new MapLayerService(http, (_, _) => Task.CompletedTask).RoadsAsync(new(35, 127), 120, CancellationToken.None); }
            catch (InvalidOperationException) { failed = true; }
            if (!failed || repeated.Count != 2) throw new Exception("Retries must be bounded");
            checks++;
        }
        Console.WriteLine($"PASS {checks} road retry cases (504 recovery, bounded retry, no retry on rate limit/client error)");
    }
    sealed class Responses(params int[] codes) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int status = codes[Count++];
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"elements\":[]}") });
        }
    }
}
