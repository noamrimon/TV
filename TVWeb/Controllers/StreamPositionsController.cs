
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using TVWeb.Models;
using TVWeb.Services;

namespace TVWeb.Controllers;

[ApiController]
[Route("api/stream/positions")]
[IgnoreAntiforgeryToken] // <-- important: bypass antiforgery for this server-to-server endpoint
public sealed class StreamPositionsController : ControllerBase
{
    private readonly PositionsStore _store;
    private readonly IConfiguration _cfg;

    public StreamPositionsController(PositionsStore store, IConfiguration cfg)
    {
        _store = store;
        _cfg = cfg;
    }

    [HttpGet]
    public IActionResult Health() => Ok(new { ok = true });

    // NOTE: changed route to "ingest" to avoid collisions with any MapPost("/api/stream/positions")
    [HttpPost("ingest")]
    public async Task<IActionResult> Ingest()
    {
        // Auth: X-INGEST-KEY must match appsettings.json
        if (!Request.Headers.TryGetValue("X-INGEST-KEY", out var keyVals))
            return StatusCode(403);
        var expectedKey = _cfg["WebIngest:IngestKey"] ?? string.Empty;
        if (!FixedTimeEquals(keyVals[0], expectedKey))
            return StatusCode(403);

        string body;
        using (var reader = new StreamReader(Request.Body))
            body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(body))
            return BadRequest("Empty body");

        var trimmed = body.TrimStart();

        try
        {
            // CASE 1: snapshots array (startup & reconcile)
            if (trimmed.StartsWith("["))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var list = new List<PositionSnapshot>();
                foreach (var el in root.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var t) ? t.GetString() : "snapshot";
                    if (!string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase)) continue;

                    var dealId = el.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                    var epic = el.TryGetProperty("epic", out var e) ? e.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dealId) || string.IsNullOrWhiteSpace(epic)) continue;

                    var dir = el.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : null;
                    var size = TryDec(el, "size") ?? 0m;
                    var bid = TryDec(el, "bid");
                    var ask = TryDec(el, "ask");
                    var open = TryDec(el, "openLevel");
                    var valuePerPoint = TryDec(el, "valuePerPoint"); // read nullable valuePerPoint

                    list.Add(new PositionSnapshot(
                        DealId: dealId!,
                        Epic: epic!,
                        Direction: dir ?? "",
                        Size: size,
                        Bid: bid,
                        Ask: ask,
                        OpenLevel: open,
                        LastUpdatedUtc: DateTimeOffset.UtcNow,
                        ValuePerPoint: valuePerPoint
                    ));
                }

                if (list.Count > 0) _store.UpsertRange(list);
                return Ok(new { snapshots = list.Count });
            }

            // CASE 2: single object (tick / snapshot / closed)
            using var doc2 = JsonDocument.Parse(body);
            var root2 = doc2.RootElement;
            var type2 = root2.TryGetProperty("type", out var t2) ? t2.GetString() : null;

            if (string.Equals(type2, "tick", StringComparison.OrdinalIgnoreCase))
            {
                var epic = root2.TryGetProperty("epic", out var e) ? e.GetString() : null;
                var dealId = root2.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(epic) || string.IsNullOrWhiteSpace(dealId))
                    return BadRequest("Tick must include epic and dealId");

                var bid = TryDec(root2, "bid");
                var ask = TryDec(root2, "ask");

                DateTimeOffset? ts = null;
                if (root2.TryGetProperty("timestampUtc", out var tsEl) && tsEl.ValueKind != JsonValueKind.Null)
                {
                    if (tsEl.TryGetDateTimeOffset(out var iso)) ts = iso;
                    else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var ms))
                        ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
                ts ??= DateTimeOffset.UtcNow;

                _store.ApplyTick(new PriceTick(epic!, dealId!, bid, ask, ts));
                return Ok(new { tick = true });
            }
            else if (string.Equals(type2, "closed", StringComparison.OrdinalIgnoreCase))
            {
                var dealId = root2.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                var epic = root2.TryGetProperty("epic", out var e) ? e.GetString() : null;
                if (!string.IsNullOrWhiteSpace(dealId))
                    _store.Remove(dealId!, epic);
                return Ok(new { closed = dealId });
            }
            else
            {
                // Treat as single snapshot if it looks like one
                var dealId = root2.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                var epic = root2.TryGetProperty("epic", out var e) ? e.GetString() : null;
                if (!string.IsNullOrWhiteSpace(dealId) && !string.IsNullOrWhiteSpace(epic))
                {
                    var dir = root2.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : null;
                    var size = TryDec(root2, "size") ?? 0m;
                    var bid = TryDec(root2, "bid");
                    var ask = TryDec(root2, "ask");
                    var open = TryDec(root2, "openLevel");
                    var valuePerPoint = TryDec(root2, "valuePerPoint");

                    _store.UpsertRange(new[]
                    {
                        new PositionSnapshot(
                            DealId: dealId!,
                            Epic: epic!,
                            Direction: dir ?? "",
                            Size: size,
                            Bid: bid,
                            Ask: ask,
                            OpenLevel: open,
                            LastUpdatedUtc: DateTimeOffset.UtcNow,
                            ValuePerPoint: valuePerPoint
                        )
                    });
                    return Ok(new { snapshot = dealId });
                }

                return BadRequest("Unrecognized payload");
            }
        }
        catch (Exception ex)
        {
            // Never 500: report 400 with reason
            Console.WriteLine($"[INGEST] error: {ex}");
            return BadRequest("Invalid JSON or payload");
        }
    }

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static decimal? TryDec(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;

        try { return v.GetDecimal(); }
        catch
        {
            if (v.ValueKind == JsonValueKind.String &&
                decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                return d;
            return null;
        }
    }
}
