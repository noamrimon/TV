
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TVStreamer.Models;
using static TVStreamer.Models.BrokerConfig;

namespace TVStreamer.Streaming
{
    public static class TemplateExecutor
    {

        public static string Resolve(string template, IReadOnlyDictionary<string, string>? vars)
        {
            return ResolveTemplates(template, vars);
        }


        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private static bool IsHtml(string s) =>
            !string.IsNullOrWhiteSpace(s) &&
            (s.TrimStart().StartsWith("<") ||
             s.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
             s.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase));

        private static string Snip(string s, int max = 400)
            => string.IsNullOrWhiteSpace(s) ? "(empty)" : (s.Length <= max ? s : s[..max] + "... [truncated]");

        private static string NormalizeBaseUrl(string baseUrl)
            => (baseUrl ?? string.Empty).Trim().TrimEnd('/');

        private static string NormalizePath(string path)
            => (path ?? string.Empty).TrimStart('/');

        private static Uri BuildAbsoluteUri(string baseUrl, string path)
            => new Uri($"{NormalizeBaseUrl(baseUrl)}/{NormalizePath(path)}", UriKind.Absolute);

        /// <summary>
        /// Generic entry point: authenticates and returns an AuthSession that
        /// includes a normalized BaseUrl, a pre-configured HttpClient, and extracted Vars.
        /// </summary>
        public static async Task<AuthSession> AuthenticateAsync(BrokerConfig cfg, CancellationToken cancel)
        {
            if (cfg.Http == null || string.IsNullOrWhiteSpace(cfg.Http.BaseUrl))
                throw new InvalidOperationException("Http.BaseUrl missing in broker config.");

            // Normalize base URL. This lets JSON use with/without trailing slash interchangeably.
            var baseUrl = NormalizeBaseUrl(cfg.Http.BaseUrl);

            // === HTTP CLIENT (HTTP/1.1 + TLS) ===

            var handler = new HttpClientHandler
            {
                // IG requires modern TLS; this prevents downgrade weirdness
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                               System.Security.Authentication.SslProtocols.Tls13,

                // Don’t follow 3xx to https://www.ig.com/en
                AllowAutoRedirect = false,

                // Avoid corporate proxy meddling if present
                UseProxy = false,

                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                         System.Net.DecompressionMethods.Deflate
            };


            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
                DefaultRequestVersion = new Version(1, 1),
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Timeout = TimeSpan.FromSeconds(cfg.Http.TimeoutSeconds > 0 ? cfg.Http.TimeoutSeconds : 30)
            };

            // Apply default headers now
            ApplyHeaders(http.DefaultRequestHeaders, Resolve(cfg.Http.DefaultHeaders, cfg.Vars));

            // Work dictionary for variables extracted along the way

            var workingVars = new Dictionary<string, string>(cfg.Vars ?? new(), StringComparer.OrdinalIgnoreCase);

            // ---- NEW: BearerToken template (Saxo SIM) ----

            if (string.Equals(cfg.BrokerTemplate, "BearerToken", StringComparison.OrdinalIgnoreCase))
            {
                if (!workingVars.TryGetValue("AccessToken", out var token) || string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("BearerToken template requires Vars.AccessToken.");

                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Ensure UTF-8 accept for Saxo responses (practice)
                if (!http.DefaultRequestHeaders.TryGetValues("Accept", out _))
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");

                return new AuthSession(http, baseUrl, workingVars);
            }


            // ---- existing: CustomLogin (IG) ----
            if (string.Equals(cfg.BrokerTemplate, "CustomLogin", StringComparison.OrdinalIgnoreCase))


                // === Run template steps when CustomLogin is used ===
                if (string.Equals(cfg.BrokerTemplate, "CustomLogin", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var step in cfg.Auth?.Steps ?? Enumerable.Empty<BrokerConfig.AuthStep>())
                {

                    var absoluteUri = BuildAbsoluteUri(baseUrl, step.Request.Path);
                    var request = new HttpRequestMessage(new HttpMethod(step.Request.Method ?? "POST"), absoluteUri);


                    // Request headers except Content-Type
                    var headers = Resolve(step.Request.Headers, workingVars);
                    ApplyHeaders(request.Headers, headers);

                    // If body exists, set Content-Type on content only
                    string? contentType = null;
                    if (headers != null && headers.TryGetValue("Content-Type", out var ct))
                        contentType = ct;

                    if (step.Request.Body != null)
                    {
                        var bodyJson = ResolveTemplates(JsonSerializer.Serialize(step.Request.Body, JsonOpts), workingVars);
                        request.Content = new StringContent(bodyJson, Encoding.UTF8);
                        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json; charset=UTF-8");
                    }

                    // Send
                    var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
                    Console.WriteLine($"[AUTH] HTTP {(int)response.StatusCode} {response.StatusCode}");

                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        var location = response.Headers.Location?.ToString() ?? "(none)";
                        Console.WriteLine("[AUTH] REDIRECT BLOCKED");
                        Console.WriteLine($"[AUTH] Location: {location}");
                        throw new InvalidOperationException(
                            $"Auth endpoint returned redirect to '{location}'. " +
                            "This usually means the request was malformed or a network device is rewriting it. " +
                            "We blocked the redirect so you remain on the API host.");
                    }

                    // Proxy/host rewrite detection
                    if (response.RequestMessage!.RequestUri!.Host != new Uri(baseUrl).Host)
                    {
                        Console.WriteLine("[PROXY WARNING] The request host was modified by a proxy/MITM.");
                        Console.WriteLine($"[PROXY WARNING] Sent to: {response.RequestMessage.RequestUri}");
                    }

                    var raw = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

                    // HTML instead of JSON is the typical symptom of missing/invalid headers,
                    // wrong endpoint, or upstream interception.
                    if (IsHtml(raw))
                    {
                        Console.WriteLine("[AUTH] HTML returned instead of JSON.");
                        Console.WriteLine(Snip(raw));
                        throw new InvalidOperationException("Auth endpoint returned HTML. Check headers, endpoint, or proxy.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[AUTH] Error body:");
                        Console.WriteLine(Snip(raw));
                        throw new HttpRequestException($"Auth step failed with {(int)response.StatusCode} {response.StatusCode}");
                    }

                    // Extract header values (e.g., CST/XST)
                    var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (step.Extract?.Headers != null)
                    {
                        foreach (var kv in step.Extract.Headers)
                        {
                            if (response.Headers.TryGetValues(kv.Value, out var vals))
                                extracted[kv.Key] = vals.FirstOrDefault() ?? string.Empty;
                        }
                    }

                    // Extract JSON values (e.g., currentAccountId, lightstreamerEndpoint)
                    if (step.Extract?.Json != null)
                    {
                        try
                        {
                            var node = JsonNode.Parse(raw);
                            foreach (var kv in step.Extract.Json)
                            {
                                var token = node.SelectToken(kv.Value);
                                if (token != null)
                                    extracted[kv.Key] = token.ToString();
                            }
                            if (extracted.TryGetValue("currentAccountId", out var accId) && !string.IsNullOrWhiteSpace(accId))
                            {
                                extracted["AccountId"] = accId;
                                extracted["AccountName"] = accId;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[AUTH] JSON parse failed:");
                            Console.WriteLine(Snip(raw));
                            throw new InvalidOperationException("Failed to parse JSON in auth step.", ex);
                        }
                    }

                    // Bind defaults (move extracted header values to default headers on HttpClient)
                    if (step.BindDefaults?.Headers != null)
                    {
                        var bound = Resolve(step.BindDefaults.Headers, Merge(workingVars, extracted));
                        ApplyHeaders(http.DefaultRequestHeaders, bound);
                    }

                    // Keep new vars
                    workingVars = Merge(workingVars, extracted);
                }
            }

            // Return a generic BROKER SESSION
            return new AuthSession(http, baseUrl, workingVars);
        }

        // ---------------- helpers ----------------

        private static void ApplyHeaders(HttpRequestHeaders headers, IDictionary<string, string>? dict)
        {
            if (dict == null) return;

            foreach (var kv in dict)
            {
                if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue; // never set content-type on request headers

                if (kv.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var p = kv.Value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    headers.Authorization = p.Length == 2
                        ? new AuthenticationHeaderValue(p[0], p[1])
                        : new AuthenticationHeaderValue("Bearer", kv.Value);
                }
                else
                {
                    if (headers.Contains(kv.Key)) headers.Remove(kv.Key);
                    headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }
            }
        }


        private static IDictionary<string, string>? Resolve(Dictionary<string, string>? map, IReadOnlyDictionary<string, string>? vars)
        {
            if (map == null) return null;

            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in map)
                r[kv.Key] = ResolveTemplates(kv.Value, vars);

            return r;
        }


        private static string ResolveTemplates(string input, IReadOnlyDictionary<string, string>? vars)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            if (vars != null)
            {
                foreach (var kv in vars)
                {
                    var k = kv.Key;
                    var v = kv.Value;

                    input = input.Replace($"{{{{Vars.{k}}}}}", v)
                                 .Replace($"{{{{{k}}}}}", v);
                }
            }
            return input;
        }


        private static Dictionary<string, string> Merge(Dictionary<string, string>? a, Dictionary<string, string>? b)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (a != null) foreach (var kv in a) d[kv.Key] = kv.Value;
            if (b != null) foreach (var kv in b) d[kv.Key] = kv.Value;
            return d;
        }
    }

    internal static class JsonNodeExtensions
    {
        public static JsonNode? SelectToken(this JsonNode node, string path)
        {
            var cur = node;
            foreach (var seg in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
                cur = cur?[seg];
            return cur;
        }
    }
}
