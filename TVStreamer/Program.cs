
using com.lightstreamer.client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TVStreamer.Models;
using TVStreamer.Streaming;

namespace TVStreamer;

internal class Program
{
    // The single source of truth for all positions
    private static readonly PositionService _positionService = new();

    static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        var config = builder.Configuration.AddJsonFile("appsettings.json", optional: false).Build();
        var host = builder.Build();
        var runtimes = new List<BrokerRuntime>();

        var ingest = config.GetSection("WebIngest");
        var ingestUrl = ingest["Url"]!;
        var ingestKey = ingest["IngestKey"]!;

        var baseDir = AppContext.BaseDirectory;
        var brokerFiles = BrokerLoader.EnumerateBrokerFiles(baseDir).ToList();
        

        if (brokerFiles.Count == 0) return;

        var tasks = new List<Task>();

        foreach (var file in brokerFiles)
        {
            try
            {
                var cfg = BrokerLoader.LoadConfig(file);
                var session = await TemplateExecutor.AuthenticateAsync(cfg, CancellationToken.None);

                // --- STEP A: Create the runtime and keep a reference to it ---
                var currentRuntime = new BrokerRuntime { Config = cfg, Session = session };
                runtimes.Add(currentRuntime);

                await RunGenericRestFlowAsync(cfg, session, ingestUrl, ingestKey, file, _positionService);

                var rootNode = JsonNode.Parse(File.ReadAllText(file));
                var streamingNode = rootNode?["Streaming"];

                if (streamingNode != null)
                {
                    var streamer = StreamerFactory.Create(cfg, session, streamingNode, _positionService, ingestUrl, ingestKey);

                    // --- STEP B: LINK THEM TOGETHER ---
                    currentRuntime.Streamer = streamer;

                    _ = Task.Run(async () => await streamer.StartAsync());
                }
            }
            catch (Exception ex) { Console.WriteLine($"Error booting {file}: {ex.Message}"); }
        }

        // Start the background loader for reconciliation (detecting closed trades)
        var baseLoader = new BasePositionsLoader(_positionService, runtimes, ingestUrl, ingestKey);
        _ = baseLoader.RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Console.WriteLine("Streaming … press Ctrl+C to stop.");
        _ = Task.WhenAll(tasks);
        await host.RunAsync();
    }


    private static async Task RunGenericRestFlowAsync(BrokerConfig cfg, AuthSession session, string ingestUrl, string ingestKey, string file, IPositionService service)
    {
        Console.WriteLine($"[{cfg.Name}] Running generic REST flow.");

        var bpCfg = cfg.BasePositions;
        var url = TemplateExecutor.Resolve(bpCfg.Url, session.Vars);
        var req = new HttpRequestMessage(new HttpMethod(bpCfg.Method), url);

        foreach (var h in bpCfg.Headers)
            req.Headers.TryAddWithoutValidation(h.Key, TemplateExecutor.Resolve(h.Value, session.Vars));

        using var resp = await session.Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return;

        var content = await resp.Content.ReadAsStringAsync();
        var root = JsonNode.Parse(content);
        Console.WriteLine($"[RAW DATA FROM JSON: {root}]"); 
        var arr = JsonPath.Select(root, cfg.BasePositions.JsonPaths.Array) as JsonArray;
        string finalDir = "BUY";
        if (arr != null)
        {
            var list = new List<BasePosition>();
            session.Vars.TryGetValue("AccountId", out var accId);
            foreach (var row in arr)
            {
                var dirPath = cfg.BasePositions.JsonPaths.Direction;
                var rawDir = !string.IsNullOrEmpty(dirPath)
                             ? JsonPath.Select(row, dirPath)?.ToString() ?? "BUY"
                             : "BUY";

                // Normalize Direction
               
                if (rawDir.Equals("SELL", StringComparison.OrdinalIgnoreCase) ||
                    rawDir.Equals("Ask", StringComparison.OrdinalIgnoreCase)) // Saxo's "Ask" is a SELL
                {
                    finalDir = "SELL";
                }
                list.Add(new BasePosition
                {
                    Broker = cfg.Name,
                    AccountId = accId ?? "Main",
                    DealId = JsonPath.Select(row, cfg.BasePositions.JsonPaths.DealId)?.ToString() ?? "",
                    // Keep Amount as a raw number, but ensure it's negative for SELLs for the UI
                    Amount = decimal.TryParse(JsonPath.Select(row, cfg.BasePositions.JsonPaths.Amount)?.ToString(), out var a) ? a : 0,
                    Epic = JsonPath.Select(row, cfg.BasePositions.JsonPaths.Epic)?.ToString() ?? "",
                    OpenLevel = decimal.TryParse(JsonPath.Select(row, cfg.BasePositions.JsonPaths.OpenLevel)?.ToString(), out var price) ? price : 0,
                    Currency = JsonPath.Select(row, cfg.BasePositions.JsonPaths.Currency)?.ToString() ?? "USD",
                    ScalingFactor = decimal.TryParse(JsonPath.Select(row, "scalingFactor")?.ToString(), out var sf) ? sf : 1.0m,
                    ValuePerPoint = decimal.TryParse(JsonPath.Select(row, "position.contractSize")?.ToString(), out var vpp) ? vpp : 1.0m, //contractSize is IG's terminology for VPP,
                                                                                                                                  //i'll probably need another helper to make it generic
                                                                                                                                  //for other brokers. for Saxo it is always 1m
                    Direction = finalDir,
                    LastUpdatedUtc = DateTimeOffset.UtcNow
                });
            }

            service.UpdateBrokerPositions(cfg.Name, list);
            Console.WriteLine($"[REST-FLOW] Local service updated for {cfg.Name}.");

            // --- NEW LOGIC START ---
            if (list.Count > 0)
            {
                Console.WriteLine($"[REST-FLOW] POSTing {list.Count} {cfg.Name} positions to Web App for initialization...");

                // We map BasePosition to the format the Web App expects for a snapshot
                var snapshots = list.Select(p => new {
                    type = "snapshot",
                    dealId = p.DealId,
                    epic = p.Epic,
                    broker = p.Broker,
                    accountId = p.AccountId ?? "NotFound",
                    size = p.Amount,
                    openLevel = p.OpenLevel,
                    currency = p.Currency,
                    direction = p.Direction, // Adding this helps the UI color-coding
                    valuePerPoint = p.ValuePerPoint,
                    scalingFactor = p.ScalingFactor
                }).ToList();

                try
                {
                    await HttpPoster.PostJsonAsync(ingestUrl, ingestKey, snapshots);
                    Console.WriteLine($"[REST-FLOW] Initialization successful for {cfg.Name}.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[REST-FLOW] Failed to initialize Web App: {ex.Message}");
                }
            }
            // --- NEW LOGIC END ---
        }

        StartStreamer(cfg, session, ingestUrl, ingestKey, file);
    }

    private static void StartStreamer(
        BrokerConfig cfg,
        AuthSession session,
        string ingestUrl,
        string ingestKey,
        string brokerFilePath)
    {
        try
        {
            var jsonText = File.ReadAllText(brokerFilePath);
            var rootNode = JsonNode.Parse(jsonText);
            var streamingNode = rootNode?["Streaming"];

            if (streamingNode == null || streamingNode["Ws"] == null) return;

            // FIXED: Convert JsonNode to JsonElement for the constructor
            // FIXED: Pass _positionService instead of _baseIndex
            var streamer = new GenericWebSocketStreamer(
                    session.Http,
                    session.BaseUrl,
                    session.Vars,
                    JsonDocument.Parse(streamingNode.ToJsonString()).RootElement,
                    ingestUrl,
                    ingestKey,
                    _positionService, // This is now passed as IPositionService
                    cfg.Name
                );

            _ = Task.Run(async () => await streamer.StartAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{cfg.Name}] Failed to start streamer: {ex.Message}");
        }
    }
}
