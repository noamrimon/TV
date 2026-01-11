using System.Text.Json;
using com.lightstreamer.client;
using TVStreamer.Streaming;

namespace TVStreamer.Listeners;

// Minimal delta model
public readonly record struct DealEpic(string DealId, string Epic);
public readonly record struct TradeDelta(IEnumerable<DealEpic> Added, IEnumerable<DealEpic> Removed);

sealed class TradeListener : SubscriptionListener
{
    private readonly string _ingestUrl;
    private readonly string _ingestKey;
    private readonly Func<TradeDelta, Task> _onPositionDelta;

    public TradeListener(string ingestUrl, string ingestKey, Func<TradeDelta, Task> onPositionDelta)
    {
        _ingestUrl = ingestUrl;
        _ingestKey = ingestKey;
        _onPositionDelta = onPositionDelta;
    }

    public void onItemUpdate(ItemUpdate update)
    {
        var opuJson = update.getValue("OPU"); // real-time Open Position Update payload (JSON) [3](https://deepwiki.com/joaquinbejar/ig-client/7.3-trade-and-account-streaming)
        if (string.IsNullOrEmpty(opuJson)) return;

        try
        {
            using var doc = JsonDocument.Parse(opuJson);
            var root = doc.RootElement;

            var dealId = root.TryGetProperty("dealId", out var d) ? d.GetString() : null;
            var epic = root.TryGetProperty("epic", out var e) ? e.GetString() : null;
            var direction = root.TryGetProperty("direction", out var dir) ? dir.GetString() : null;
            var size = root.TryGetProperty("size", out var s) ? s.GetDecimal() : 0m;
            var level = root.TryGetProperty("level", out var lev) && lev.ValueKind != JsonValueKind.Null ? lev.GetDecimal() : (decimal?)null;

            var isClose = root.TryGetProperty("positionStatus", out var ps) &&
                          string.Equals(ps.GetString(), "CLOSED", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(dealId) && !string.IsNullOrWhiteSpace(epic))
            {
                if (isClose || size == 0m)
                {
                    var payload = new { type = "closed", dealId, epic };
                    _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, payload);
                    _ = _onPositionDelta(new TradeDelta(Array.Empty<DealEpic>(), new[] { new DealEpic(dealId!, epic!) }));
                    Console.WriteLine($"[TRADE] Closed: {dealId} {epic}");
                }
                else
                {
                    var snapshot = new
                    {
                        type = "snapshot",
                        dealId,
                        epic,
                        direction,
                        size,
                        openLevel = level,
                        bid = (decimal?)null,
                        ask = (decimal?)null
                    };
                    _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, new[] { snapshot });
                    _ = _onPositionDelta(new TradeDelta(new[] { new DealEpic(dealId!, epic!) }, Array.Empty<DealEpic>()));
                    Console.WriteLine($"[TRADE] Open/Update: {dealId} {epic} {direction} {size} @ {level}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TRADE] Failed to parse OPU: {ex.Message}");
        }
    }

    public void onSubscription() => Console.WriteLine("[TRADE] subscription confirmed.");
    public void onSubscriptionError(int code, string message) => Console.WriteLine($"[TRADE] subscription error: {code} - {message}");
    public void onUnsubscription() => Console.WriteLine("[TRADE] unsubscribed.");

    public void onListenStart() { }
    public void onListenEnd() { }
    public void onEndOfSnapshot(string itemName, int itemPos) { }
    public void onItemLostUpdates(string itemName, int itemPos, int lostUpdates) { }
    public void onClearSnapshot(string itemName, int itemPos) { }
    public void onCommandSecondLevelItemLostUpdates(int lostUpdates, string key) { }
    public void onCommandSecondLevelSubscriptionError(int code, string message, string key) { }
    public void onRealMaxFrequency(string frequency) { }
}