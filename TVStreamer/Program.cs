
using com.lightstreamer.client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using TVStreamer.Models;
using TVStreamer.Streaming;

namespace TVStreamer;

internal class Program
{
    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var config = builder.Configuration.AddJsonFile("appsettings.json", optional: false).Build();
        var host = builder.Build();
        JsonNode? saxoRestNode = null;

        // -----------------------------
        // Ingest configuration
        // -----------------------------
        var ingest = config.GetSection("WebIngest");
        var ingestUrl = ingest["Url"]!;
        var ingestKey = ingest["IngestKey"]!;

        // -----------------------------
        // Discover all broker JSONs
        // -----------------------------
        var baseDir = AppContext.BaseDirectory;
        var brokerFiles = BrokerLoader.EnumerateBrokerFiles(baseDir).ToList();
        if (brokerFiles.Count == 0)
        {
            Console.WriteLine("[BOOT] No broker configs found in /Brokers.");
            return;
        }

        Console.WriteLine($"[BOOT] Found {brokerFiles.Count} broker file(s).");

        // Start all brokers concurrently
        var tasks = new List<Task>();

        foreach (var file in brokerFiles)
        {
            try
            {
                // Optional "Enabled": false bypass (read directly from JSON)
                bool enabled = true;
                using (var raw = JsonDocument.Parse(File.ReadAllText(file)))
                {
                    if (raw.RootElement.TryGetProperty("Enabled", out var eProp) &&
                        eProp.ValueKind == JsonValueKind.False)
                    {
                        enabled = false;
                    }
                }
                if (!enabled)
                {
                    Console.WriteLine($"[BOOT] Skipping (disabled): {Path.GetFileName(file)}");
                    continue;
                }

                var cfg = BrokerLoader.LoadConfig(file);
                Console.WriteLine($"[BOOT] Loaded broker config: {cfg.Name}");

                // Authenticate generically (TemplateExecutor dispatches by BrokerTemplate)
                var session = await TemplateExecutor.AuthenticateAsync(cfg, CancellationToken.None);
                Console.WriteLine($"[{cfg.Name}] Auth OK.");

                // IG-like (has lightstreamerEndpoint): run IG streaming flow
                if (!string.IsNullOrWhiteSpace(session.LightstreamerEndpoint))
                {
                    tasks.Add(RunIgFlowAsync(cfg, session, ingestUrl, ingestKey));
                }
                else
                {
                    // Generic REST-only (and maybe WS streaming if configured in JSON)
                    tasks.Add(RunGenericRestFlowAsync(cfg, session, ingestUrl, ingestKey, file));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BOOT] {Path.GetFileName(file)} failed: {ex.Message}");
            }
        }

        Console.WriteLine("Streaming … press Ctrl+C to stop.");
        _ = Task.WhenAll(tasks);
        await host.RunAsync();
    }

    // ------------------------------------------------------------
    // IG: REST snapshot + Lightstreamer streaming (non-blocking)
    // ------------------------------------------------------------
    private static async Task RunIgFlowAsync(BrokerConfig cfg, AuthSession session, string ingestUrl, string ingestKey)
    {
        var accountId = session.AccountId ?? "(unknown)";
        var lsEndpoint = session.LightstreamerEndpoint;

        Console.WriteLine($"[{cfg.Name}] AccountId  = {accountId}");
        Console.WriteLine($"[{cfg.Name}] REST Base  = {session.BaseUrl}");
        Console.WriteLine($"[{cfg.Name}] LS Endpoint= {lsEndpoint ?? "(none)"}");

        // Absolute REST URI builder
        static Uri RestUri(AuthSession s, string path) =>
            new Uri($"{s.BaseUrl}/{path.TrimStart('/')}", UriKind.Absolute);

        // Safe GET: allow only same-host HTTPS redirects
        static async Task<HttpResponseMessage> GetWithSafeRedirect(AuthSession s, string path, CancellationToken ct = default)
        {
            var baseUri = new Uri(s.BaseUrl);
            var resp = await s.Http.GetAsync(RestUri(s, path), ct);

            if ((int)resp.StatusCode >= 300 && (int)resp.StatusCode < 400)
            {
                var location = resp.Headers.Location;
                Uri? target = null;
                if (location != null)
                    target = location.IsAbsoluteUri ? location : new Uri(baseUri, location);

                Console.WriteLine($"[REST] Redirect {(int)resp.StatusCode} -> {target}");
                if (target != null &&
                    target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                    target.Host.Equals(baseUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    resp.Dispose();
                    return await s.Http.GetAsync(target, ct);
                }
                throw new HttpRequestException($"Blocked redirect to '{target?.ToString() ?? "(null)"}'");
            }
            return resp;
        }

        // 1) Snapshot: /positions
        var posResp = await GetWithSafeRedirect(session, "/positions");
        posResp.EnsureSuccessStatusCode();

        using var posJson = JsonDocument.Parse(await posResp.Content.ReadAsStringAsync());
        var positions = posJson.RootElement.GetProperty("positions")
            .EnumerateArray()
            .Select(p => new PositionInfo(
                DealId: p.GetProperty("position").GetProperty("dealId").GetString()!,
                Epic: p.GetProperty("market").GetProperty("epic").GetString()!,
                Direction: p.GetProperty("position").GetProperty("direction").GetString()!,
                Size: p.GetProperty("position").GetProperty("size").GetDecimal(),
                OpenLevel: p.GetProperty("position").GetProperty("level").GetDecimal()
            ))
            .ToList();

        Console.WriteLine($"[{cfg.Name}] Open positions: {positions.Count}");

        // 2) ValuePerPoint
        var valuePerPointByEpic = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var epic in positions.Select(p => p.Epic).Distinct())
        {
            try
            {
                var mdResp = await GetWithSafeRedirect(session, $"/markets/{epic}");
                mdResp.EnsureSuccessStatusCode();

                using var md = JsonDocument.Parse(await mdResp.Content.ReadAsStringAsync());
                var instrument = md.RootElement.GetProperty("instrument");

                var unit = instrument.TryGetProperty("unit", out var uEl) ? uEl.GetString() : null;
                var contractSize = instrument.TryGetProperty("contractSize", out var csEl) ? csEl.GetString() : "1";

                decimal vpp = 1m;
                if (string.Equals(unit, "CONTRACTS", StringComparison.OrdinalIgnoreCase) &&
                    decimal.TryParse(contractSize, NumberStyles.Any, CultureInfo.InvariantCulture, out var cs))
                {
                    vpp = cs;
                }
                valuePerPointByEpic[epic] = vpp;
            }
            catch
            {
                valuePerPointByEpic[epic] = 1m;
            }
        }

        // 3) Send initial snapshots
        var snapshotsPayload = positions.Select(p => new
        {
            type = "snapshot",
            dealId = p.DealId,
            epic = p.Epic,
            direction = p.Direction,
            size = p.Size,
            openLevel = p.OpenLevel,
            bid = (decimal?)null,
            ask = (decimal?)null,
            valuePerPoint = valuePerPointByEpic.GetValueOrDefault(p.Epic, 1m),
            currency = p.Currency,
            broker = "IG",
            account = accountId
        });
        await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, snapshotsPayload);

        // 4) Lightstreamer streaming (per IG docs)
        var cst = session.Http.DefaultRequestHeaders.GetValues("CST").First();
        var xst = session.Http.DefaultRequestHeaders.GetValues("X-SECURITY-TOKEN").First();
        var lsPassword = $"CST-{cst}|XST-{xst}";

        var ls = new LightstreamerClient(lsEndpoint!, "DEFAULT");
        ls.connectionDetails.User = accountId;
        ls.connectionDetails.Password = lsPassword;
        ls.connect();
        Console.WriteLine("[LS] Connected. Starting streams...");

        var priceStream = new PriceStreaming(ls, ingestUrl, ingestKey);
        priceStream.Subscribe(positions, accountId);

        var tradeStream = new TradeStreaming(ls, priceStream, positions, accountId);
        tradeStream.Subscribe(ingestUrl, ingestKey);

        // 5) Reconciliation loop
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var resp = await GetWithSafeRedirect(session, "/positions");
                    resp.EnsureSuccessStatusCode();

                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var fresh = doc.RootElement.GetProperty("positions")
                        .EnumerateArray()
                        .Select(p => new PositionInfo(
                            DealId: p.GetProperty("position").GetProperty("dealId").GetString()!,
                            Epic: p.GetProperty("market").GetProperty("epic").GetString()!,
                            Direction: p.GetProperty("position").GetProperty("direction").GetString()!,
                            Size: p.GetProperty("position").GetProperty("size").GetDecimal(),
                            OpenLevel: p.GetProperty("position").GetProperty("level").GetDecimal()
                        ))
                        .ToList();

                    var knownIds = positions.Select(x => x.DealId).ToHashSet();
                    var freshIds = fresh.Select(x => x.DealId).ToHashSet();

                    var toAdd = fresh.Where(f => !knownIds.Contains(f.DealId)).ToList();
                    var toRemove = positions.Where(k => !freshIds.Contains(k.DealId)).ToList();

                    if (toAdd.Count > 0 || toRemove.Count > 0)
                    {
                        if (toAdd.Count > 0)
                        {
                            var payload = toAdd.Select(p => new
                            {
                                type = "snapshot",
                                dealId = p.DealId,
                                epic = p.Epic,
                                direction = p.Direction,
                                size = p.Size,
                                openLevel = p.OpenLevel,
                                bid = (decimal?)null,
                                ask = (decimal?)null,
                                valuePerPoint = valuePerPointByEpic.GetValueOrDefault(p.Epic, 1m)
                            });

                            await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, payload);
                        }

                        foreach (var r in toRemove)
                        {
                            await HttpPoster.PostJsonAsync(ingestUrl, ingestKey,
                                new { type = "closed", dealId = r.DealId, epic = r.Epic });
                        }

                        positions = fresh;
                        priceStream.Resubscribe(positions, accountId);
                        Console.WriteLine($"[RECON] Position list updated: {positions.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RECON] Error: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        });
    }

    // ------------------------------------------------------------
    // Generic REST flow (+ start WS streaming if Streaming section exists in JSON)
    // ------------------------------------------------------------

    private static async Task RunGenericRestFlowAsync(
        BrokerConfig cfg, AuthSession session,
        string ingestUrl, string ingestKey, string brokerFilePath)
    {
        JsonNode? saxoRestNode = null;   // NEW

        Console.WriteLine($"[{cfg.Name}] Running generic REST operations (no Lightstreamer).");

        var op = cfg.Operations?.FirstOrDefault(o =>
                     string.Equals(o.Method, "GET", StringComparison.OrdinalIgnoreCase))
                     ?? cfg.Operations?.FirstOrDefault();

        if (op is not null && !string.IsNullOrWhiteSpace(op.Path))
        {
            var uri = new Uri($"{session.BaseUrl}/{op.Path.TrimStart('/')}", UriKind.Absolute);
            var resp = await session.Http.GetAsync(uri);
            Console.WriteLine($"[{cfg.Name}] {op.Name} -> {(int)resp.StatusCode} {resp.StatusCode}");

            var content = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    int count = doc.RootElement.TryGetProperty("Data", out var data)
                                && data.ValueKind == JsonValueKind.Array
                                ? data.GetArrayLength()
                                : 0;

                    var pretty = System.Text.Json.JsonSerializer.Serialize(
                        doc.RootElement,
                        new JsonSerializerOptions { WriteIndented = true });

                    Console.WriteLine("[SAXO:REST] Full positions JSON:");
                    Console.WriteLine(pretty);

                    Console.WriteLine($"[{cfg.Name}] Items: {count}");

                    if (cfg.Name.Equals("Saxo-SIM", StringComparison.OrdinalIgnoreCase))
                    {
                        saxoRestNode = JsonNode.Parse(content);
                    }
                }
                catch
                {
                    Console.WriteLine($"[{cfg.Name}] Body length: {content.Length}");
                }
            }
        }

        // Start Streamer
        try
        {
            using var fullDoc = JsonDocument.Parse(File.ReadAllText(brokerFilePath));
            if (fullDoc.RootElement.TryGetProperty("Streaming", out var streaming) &&
                streaming.ValueKind == JsonValueKind.Object &&
                streaming.TryGetProperty("Ws", out _))
            {
                var streamer = new GenericWebSocketStreamer(
                    session.Http,
                    session.BaseUrl,
                    session.Vars,
                    streaming,
                    ingestUrl,
                    ingestKey,
                    saxoRestNode   // NEW parameter
                );

                _ = streamer.StartAsync();
                Console.WriteLine($"[{cfg.Name}] Generic WebSocket streaming started.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{cfg.Name}] Streaming init failed: {ex.Message}");
        }
    }

}
