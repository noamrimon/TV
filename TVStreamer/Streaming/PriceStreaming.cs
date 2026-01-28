using com.lightstreamer.client;
using TVStreamer.Listeners;
using TVStreamer.Models;
using TVStreamer.Services;

namespace TVStreamer.Streaming;

public sealed class PriceStreaming
{
    private readonly LightstreamerClient _client;
    private readonly PositionIngestService _ingestService;
    private Subscription? _priceSub;

    public PriceStreaming(LightstreamerClient client, string ingestUrl, string ingestKey)
    {
        _client = client;
        // Create the service once to pass into listeners
        _ingestService = new PositionIngestService(ingestUrl, ingestKey);
    }

    public void Subscribe(List<PositionInfo> positions, string accountId)
    {
        // Logic: IG Prices are usually PRICE:EPIC (account ID is not always in the item name)
        var items = positions.Select(p => $"PRICE:{p.Epic}").Distinct().ToArray();
        var fields = new[] { "BIDPRICE1", "ASKPRICE1", "BID", "OFFER", "TIMESTAMP" };

        if (items.Length == 0)
        {
            Console.WriteLine("[LS] No open positions. Skipping PRICE subscription.");
            return;
        }

        _priceSub = new Subscription("MERGE", items, fields)
        {
            DataAdapter = "DEFAULT",
            RequestedSnapshot = "yes"
        };

        // Pass the ingestService instead of the raw strings
        _priceSub.addListener(new PriceListener(items, positions, _ingestService));
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