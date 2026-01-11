
using com.lightstreamer.client;
using TVStreamer.Listeners;
using TVStreamer.Models;

namespace TVStreamer.Streaming;

public sealed class PriceStreaming
{
    private readonly LightstreamerClient _client;
    private readonly string _ingestUrl;
    private readonly string _ingestKey;
    private Subscription? _priceSub;

    public PriceStreaming(LightstreamerClient client, string ingestUrl, string ingestKey)
    {
        _client = client;
        _ingestUrl = ingestUrl;
        _ingestKey = ingestKey;
    }

    public void Subscribe(List<PositionInfo> positions, string accountId)
    {
        var items = positions.Select(p => $"PRICE:{accountId}:{p.Epic}").ToArray();

        // Include ladder + top-of-book; ladder may be absent on some instruments
        var fields = new[] { "BIDPRICE1", "ASKPRICE1", "BID", "OFFER", "TIMESTAMP" };

        if (items.Length == 0)
        {
            Console.WriteLine("[LS] No open positions. Skipping PRICE subscription.");
            return;
        }

        _priceSub = new Subscription("MERGE", items, fields)
        {
            DataAdapter = "Pricing",
            RequestedSnapshot = "yes"
        };

        _priceSub.addListener(new PriceListener(items, positions, _ingestUrl, _ingestKey));
        _client.subscribe(_priceSub);
        Console.WriteLine($"[LS] PRICE subscribed with {items.Length} items.");
    }

    public void Resubscribe(List<PositionInfo> positions, string accountId)
    {
        if (_priceSub is not null)
            _client.unsubscribe(_priceSub);

        Subscribe(positions, accountId);
    }
}
