
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace TVStreamer.Models
{
    public sealed class AuthSession : IDisposable
    {
        public HttpClient Http { get; }
        public string BaseUrl { get; }
        public IReadOnlyDictionary<string, string> Vars { get; }

        public string? AccountId => Vars.TryGetValue("currentAccountId", out var v) ? v : null;
        public string? LightstreamerEndpoint => Vars.TryGetValue("lightstreamerEndpoint", out var v) ? v : null;

        public AuthSession(HttpClient http, string baseUrl, IReadOnlyDictionary<string, string> vars)
        {
            Http = http ?? throw new ArgumentNullException(nameof(http));
            BaseUrl = (baseUrl ?? throw new ArgumentNullException(nameof(baseUrl))).TrimEnd('/');
            Vars = vars ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private string NormalizePath(string path) => (path ?? string.Empty).TrimStart('/');

        public Task<HttpResponseMessage> GetAsync(string path, System.Threading.CancellationToken ct = default)
            => Http.GetAsync(NormalizePath(path), ct);

        public void Dispose() => Http.Dispose();

        // --- Lightstreamer helper (optional) ---
        public (string Server, string User, string Password) GetLightstreamerCredentials()
        {
            if (LightstreamerEndpoint is null || AccountId is null)
                throw new InvalidOperationException("LightstreamerEndpoint or AccountId missing.");

            var cst = Http.DefaultRequestHeaders.TryGetValues("CST", out var cstVals) ? cstVals.First() : null;
            var xst = Http.DefaultRequestHeaders.TryGetValues("X-SECURITY-TOKEN", out var xstVals) ? xstVals.First() : null;
            if (string.IsNullOrWhiteSpace(cst) || string.IsNullOrWhiteSpace(xst))
                throw new InvalidOperationException("CST or X-SECURITY-TOKEN missing on HttpClient headers.");

            var password = $"CST-{cst}|XST-{xst}";
            return (LightstreamerEndpoint, AccountId, password);
        }
    }
}
