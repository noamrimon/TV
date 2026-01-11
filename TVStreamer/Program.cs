
using com.lightstreamer.client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
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

        var ig = config.GetSection("IG");
        var apiKey = ig["ApiKey"]!;
        var baseUrl = ig["BaseUrl"]!.TrimEnd('/');
        var username = ig["Username"]!;
        var password = ig["Password"]!;

        var ingest = config.GetSection("WebIngest");
        var ingestUrl = ingest["Url"]!;
        var ingestKey = ingest["IngestKey"]!;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 1) Login & tokens
        var auth = await IgAuth.LoginAsync(http, baseUrl, apiKey, username, password);
        Console.WriteLine($"[IG] Auth OK. AccountId={auth.AccountId}, ClientId={auth.ClientId}");

        // Default headers for subsequent REST calls
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.Add("X-IG-API-KEY", apiKey);
        http.DefaultRequestHeaders.Add("Accept", "application/json; charset=UTF-8");
        http.DefaultRequestHeaders.Add("Version", "2");
        http.DefaultRequestHeaders.Add("CST", auth.Cst);
        http.DefaultRequestHeaders.Add("X-SECURITY-TOKEN", auth.Xst);

        // 2) Fetch positions
        var posResp = await http.GetAsync($"{baseUrl}/positions");
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

        Console.WriteLine($"[IG] Open positions: {positions.Count}");

        // snapshots for TVWeb
        var snapshotsPayload = positions.Select(p => new
        {
            type = "snapshot",
            dealId = p.DealId,
            epic = p.Epic,
            direction = p.Direction,
            size = p.Size,
            openLevel = p.OpenLevel,
            bid = (decimal?)null,
            ask = (decimal?)null
        });
        await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, snapshotsPayload);

        // 3) Lightstreamer client
        var client = new LightstreamerClient(auth.LightstreamerEndpoint, "DEFAULT");
        client.connectionDetails.User = auth.AccountId;
        client.connectionDetails.Password = $"CST-{auth.Cst}|XST-{auth.Xst}"; // IG streaming password format [4](https://labs.ig.com/streaming-api-guide.html)
        client.connectionOptions.ForcedTransport = "WS-STREAMING"; // optional; comment if you prefer auto

        client.connect();
        Console.WriteLine($"[LS] Connecting to {auth.LightstreamerEndpoint} …");

        // 4) Start PRICE + TRADE streaming
        var price = new PriceStreaming(client, ingestUrl, ingestKey);
        price.Subscribe(positions, auth.AccountId);

        var trade = new TradeStreaming(client, price, positions, auth.AccountId);
        trade.Subscribe(ingestUrl, ingestKey);

        // 5) Reconciliation loop (snapshot safety net)
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var resp = await http.GetAsync($"{baseUrl}/positions");
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
                            var payload = toAdd.Select(p => new {
                                type = "snapshot",
                                dealId = p.DealId,
                                epic = p.Epic,
                                direction = p.Direction,
                                size = p.Size,
                                openLevel = p.OpenLevel,
                                bid = (decimal?)null,
                                ask = (decimal?)null
                            });
                            await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, payload);
                        }
                        foreach (var r in toRemove)
                            await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, new { type = "closed", dealId = r.DealId, epic = r.Epic });

                        positions = fresh;
                        price.Resubscribe(positions, auth.AccountId);
                        Console.WriteLine($"[RECON] Resynced positions: {positions.Count}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RECON] reconcile error: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
        });

        Console.WriteLine("Streaming … press Ctrl+C to stop.");
        await host.RunAsync();
    }
}
