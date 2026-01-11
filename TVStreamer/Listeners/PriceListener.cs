
using com.lightstreamer.client;
using TVStreamer.Models;
using TVStreamer.Streaming;

namespace TVStreamer.Listeners;

sealed class PriceListener : SubscriptionListener
{
    private readonly string[] _items;
    private readonly List<PositionInfo> _positions;
    private readonly string _ingestUrl;
    private readonly string _ingestKey;

    public PriceListener(string[] items, List<PositionInfo> positions, string ingestUrl, string ingestKey)
    {
        _items = items;
        _positions = positions;
        _ingestUrl = ingestUrl;
        _ingestKey = ingestKey;
    }

    public void onItemUpdate(ItemUpdate itemUpdate)
    {
        var pos = itemUpdate.ItemPos;
        var epic = (pos >= 1 && pos <= _items.Length) ? _items[pos - 1].Split(':').Last() : string.Empty;
        var deal = _positions.FirstOrDefault(p => p.Epic == epic);
        if (deal is null) return;

        var bidStr = itemUpdate.getValue("BIDPRICE1");
        var askStr = itemUpdate.getValue("ASKPRICE1");

        decimal? bid = decimal.TryParse(bidStr, out var b) ? b : null;
        decimal? ask = decimal.TryParse(askStr, out var a) ? a : null;

        var payload = new { type = "tick", epic, dealId = deal.DealId, bid, ask };
        _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, payload);

        Console.WriteLine($"[LS] Tick {epic} Bid={bidStr} Ask={askStr}");
    }

    public void onSubscription() => Console.WriteLine("[LS] PRICE subscription confirmed.");
    public void onSubscriptionError(int code, string message) => Console.WriteLine($"[LS] PRICE subscription error: {code} - {message}");
    public void onUnsubscription() => Console.WriteLine("[LS] PRICE unsubscribed.");

    public void onEndOfSnapshot(string itemName, int itemPos) { }
    public void onClearSnapshot(string itemName, int itemPos) { }
    public void onItemLostUpdates(string itemName, int itemPos, int lostUpdates) { }
    public void onListenStart() { }
    public void onListenEnd() { }
    public void onRealMaxFrequency(string frequency) { }
    public void onCommandSecondLevelItemLostUpdates(int lostUpdates, string key) { }
    public void onCommandSecondLevelSubscriptionError(int code, string message, string key) { }
}
