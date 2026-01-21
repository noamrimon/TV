
using System.Text.Json.Nodes;

namespace TVStreamer.Streaming;


public sealed class SaxoBase
{
    public string PositionId { get; set; } = "";
    public decimal Amount { get; set; }               // keep signed as-is from REST
    public decimal OpenPrice { get; set; }
    public int Uic { get; set; }
    public string Symbol { get; set; } = "";
    public string Currency { get; set; } = "USD";

    // Optional: keep BuySell only if present in your SIM (not required)
    public string BuySell { get; set; } = "";
}

public sealed class SaxoBaseStore
{
    private readonly Dictionary<string, SaxoBase> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    public void LoadFromRest(JsonNode? rest)
    {
        var arr = rest?["Data"] as JsonArray;
        if (arr is null) return;

        foreach (var row in arr)
        {
            var id = row?["PositionId"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;

            var pb = row?["PositionBase"];
            var df = row?["DisplayAndFormat"];
            if (pb is null) continue;

            var sb = new SaxoBase
            {
                PositionId = id,
                Amount = decimal.TryParse(pb["Amount"]?.ToString(), out var amt) ? amt : 0m, // ← signed (REST)
                OpenPrice = decimal.TryParse(pb["OpenPrice"]?.ToString(), out var op) ? op : 0m,
                Uic = int.TryParse(pb["Uic"]?.ToString(), out var u) ? u : 0,
                Symbol = df?["Symbol"]?.ToString() ?? id,
                Currency = row?["PositionView"]?["PriceCurrency"]?.ToString() ?? "USD",
                BuySell = pb?["BuySell"]?.ToString() ?? "" // may or may not exist in SIM; not required
            };

            _byId[id] = sb;

            // Debug:
            Console.WriteLine($"[SAXO:REST-LOAD] {sb.PositionId} {sb.Symbol} Amount={sb.Amount} Open={sb.OpenPrice}");
        }
        Console.WriteLine($"[SAXO] Loaded {_byId.Count} PositionBase rows from REST.");
    }




    public JsonNode Merge(JsonNode wsItem)
    {
        try
        {
            // We only merge if incoming item is an object
            if (wsItem is not JsonObject obj)
            {
                Console.WriteLine("[SAXO:MERGE] Skip: wsItem is not a JsonObject");
                return wsItem;
            }

            // PositionId is the key: if missing, return as-is
            var id = obj["PositionId"]?.ToString();
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.WriteLine("[SAXO:MERGE] Skip: missing PositionId");
                return wsItem;
            }

            if (!_byId.TryGetValue(id, out var baseInfo))
            {
                // Not in store (e.g., new position opened after preload) → just pass through
                Console.WriteLine($"[SAXO:MERGE] Not in store: {id}");
                return wsItem;
            }

            // Clone so we never mutate wsItem
            var clone = JsonNode.Parse(wsItem.ToJsonString())!.AsObject();

            // Compute direction from signed REST amount
            var direction = baseInfo.Amount < 0 ? "SELL" : "BUY";

            // Ensure PositionBase, DisplayAndFormat, Currency exist
            clone["PositionBase"] = new JsonObject
            {
                ["Amount"] = JsonValue.Create(baseInfo.Amount),     // signed as REST
                ["OpenPrice"] = JsonValue.Create(baseInfo.OpenPrice),
                ["Uic"] = JsonValue.Create(baseInfo.Uic),
                ["BuySell"] = direction                               // extra redundancy
            };

            clone["DisplayAndFormat"] = new JsonObject
            {
                ["Symbol"] = baseInfo.Symbol
            };

            clone["Currency"] = baseInfo.Currency;

            // Also write a top-level Direction to keep template simple
            clone["Direction"] = direction;

            Console.WriteLine($"[SAXO:MERGE] {id} {baseInfo.Symbol}: Amount={baseInfo.Amount}, Direction={direction}");
            return clone;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[SAXO:MERGE] Exception: " + ex.Message);
            return wsItem; // never break pipeline
        }
    }



}
