using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;
using TVStreamer.Models;
using TVStreamer.Services;
using TVStreamer.Streaming;

namespace TVStreamer.Models
{
    public sealed class BasePositionsIndex
    {
        private ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> _data =
            ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>>.Empty;

        public ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> GetData()
            => Volatile.Read(ref _data);

        public void Swap(ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> next)
            => Interlocked.Exchange(ref _data, next);

        public bool TryGet(string broker, string dealId, out BasePosition bp)
        {
            var snap = Volatile.Read(ref _data);
            if (snap.TryGetValue(broker, out var map) &&
                map.TryGetValue(dealId, out var found))
            {
                bp = found; return true;
            }
            bp = null!;
            return false;
        }
    }

    public static class JsonPath
    {
        public static JsonNode? Select(JsonNode? node, string path)
        {
            if (node == null) return null;
            var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            JsonNode? cur = node;
            foreach (var p in parts)
            {
                cur = cur?[p];
                if (cur == null) return null;
            }
            return cur;
        }
    }

    public sealed class BasePositionsLoader
    {
        private readonly IPositionService _positionService;
        private readonly IReadOnlyList<BrokerRuntime> _runtimes;
        private readonly PositionIngestService _ingestService;
        private readonly string _ingestUrl;
        private readonly string _ingestKey;

        public BasePositionsLoader(
            IPositionService positionService,
            IEnumerable<BrokerRuntime> runtimes,
            string ingestUrl,
            string ingestKey)
        {
            _positionService = positionService;
            _runtimes = runtimes.ToList();
            _ingestUrl = ingestUrl;
            _ingestKey = ingestKey;
            // Initialize the ingest service here
            _ingestService = new PositionIngestService(ingestUrl, ingestKey);
        }

        public async Task RunAsync(TimeSpan interval, CancellationToken ct)
        {
            var lastGood = new Dictionary<string, Dictionary<string, BasePosition>>(StringComparer.OrdinalIgnoreCase);

            while (!ct.IsCancellationRequested)
            {
                foreach (var rt in _runtimes)
                {
                    try
                    {
                        var list = await LoadOneBroker(rt, ct);

                        // 1. Identify New and Closed positions
                        if (lastGood.TryGetValue(rt.Config.Name, out var prevMap))
                        {
                            var currentIds = list.Select(x => x.DealId).ToHashSet(StringComparer.OrdinalIgnoreCase);

                            // Detect Closed
                            var closedIds = prevMap.Keys.Where(id => !currentIds.Contains(id));
                            foreach (var dealId in closedIds)
                            {
                                var epic = prevMap[dealId].Epic;
                                Console.WriteLine($"[LOADER] Detected Closed: {rt.Config.Name} {dealId} {epic}");
                                _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey,
                                    new { type = "closed", broker = rt.Config.Name, dealId = dealId, epic = epic });
                            }

                            // Detect New
                            var newPositions = list.Where(p => !prevMap.ContainsKey(p.DealId)).ToList();
                            if (newPositions.Any())
                            {
                                await _ingestService.RegisterPositionsAsync(newPositions);
                            }
                        }
                        else
                        {
                            // First run: Register everything found
                            await _ingestService.RegisterPositionsAsync(list);
                        }

                        // 2. Update Global Service and Local State
                        _positionService.UpdateBrokerPositions(rt.Config.Name, list);
                        lastGood[rt.Config.Name] = list.ToDictionary(x => x.DealId, x => x, StringComparer.OrdinalIgnoreCase);

                        // 3. Update Streamer Subscriptions
                        if (rt.Streamer is LightstreamerStreamer ls)
                        {
                            var positionInfos = list.Select(p => new PositionInfo(
                                p.DealId, p.Epic, p.Direction, p.Amount, 0, 0,
                                p.OpenLevel, 0, p.Currency, rt.Config.Name, DateTime.UtcNow
                            )).ToList();

                            ls.UpdatePriceSubscriptions(positionInfos);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"{rt.Config.LogPrefix} BasePositionsLoader error: {ex.Message}");
                    }
                }
                await Task.Delay(interval, ct);
            }
        }

        private async Task<List<BasePosition>> LoadOneBroker(BrokerRuntime rt, CancellationToken ct)
        {
            var cfg = rt.Config;
            var session = rt.Session;
            var resolveVars = new Dictionary<string, string>(session.Vars, StringComparer.OrdinalIgnoreCase);
            string accId = session.AccountId ?? (resolveVars.TryGetValue("AccountId", out var id) ? id : "N/A");
            if (cfg.Name.Contains("IG", StringComparison.OrdinalIgnoreCase))
            {
                if (session.Http.DefaultRequestHeaders.TryGetValues("CST", out var cst))
                    resolveVars["CST"] = cst.First();
                if (session.Http.DefaultRequestHeaders.TryGetValues("X-SECURITY-TOKEN", out var xst))
                    resolveVars["XST"] = xst.First();
            }
            
            var bpCfg = cfg.BasePositions;
            var url = TemplateExecutor.Resolve(bpCfg.Url, resolveVars);
            var req = new HttpRequestMessage(new HttpMethod(bpCfg.Method), url);

            foreach (var h in bpCfg.Headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, TemplateExecutor.Resolve(h.Value, resolveVars));
            }

            var resp = await session.Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new List<BasePosition>();

            var content = await resp.Content.ReadAsStringAsync(ct);
            var root = JsonNode.Parse(content);
            var arr = JsonPath.Select(root, bpCfg.JsonPaths.Array) as JsonArray;

            var list = new List<BasePosition>();
            if (arr != null)
            {
                foreach (var row in arr)
                {
                    var rawDir = JsonPath.Select(row, bpCfg.JsonPaths.Direction)?.ToString() ?? "BUY";
                    string normalizedDir = (rawDir.Contains("SELL", StringComparison.OrdinalIgnoreCase) ||
                                           rawDir.Contains("Ask", StringComparison.OrdinalIgnoreCase))
                                           ? "SELL" : "BUY";
                    list.Add(new BasePosition
                    {
                        Broker = cfg.Name,
                        AccountId = accId,
                        DealId = JsonPath.Select(row, bpCfg.JsonPaths.DealId)?.ToString() ?? "",
                        Amount = decimal.TryParse(JsonPath.Select(row, bpCfg.JsonPaths.Amount)?.ToString(), out var a) ? a : 0,
                        OpenLevel = decimal.TryParse(JsonPath.Select(row, bpCfg.JsonPaths.OpenLevel)?.ToString(), out var p) ? p : 0,
                        Epic = JsonPath.Select(row, cfg.BasePositions.JsonPaths.Epic)?.ToString() ?? "",
                        Currency = JsonPath.Select(row, bpCfg.JsonPaths.Currency)?.ToString() ?? "USD",
                        Direction = normalizedDir,
                        Uic = int.TryParse(JsonPath.Select(row, "PositionBase.Uic")?.ToString(), out var u) ? u : null,
                        ScalingFactor = decimal.TryParse(JsonPath.Select(row, "scalingFactor")?.ToString(), out var sf) ? sf : 1.0m,
                        ValuePerPoint = decimal.TryParse(JsonPath.Select(row, "position.contractSize")?.ToString(), out var vpp) ? vpp : 1.0m,
                        LastUpdatedUtc = DateTimeOffset.UtcNow
                    });
                }
            }
            return list;
        }
    }
}