using com.lightstreamer.client;
using TVStreamer.Listeners;
using TVStreamer.Models;

namespace TVStreamer.Streaming;

public sealed class TradeStreaming
{
    private readonly LightstreamerClient _client;
    private readonly PriceStreaming _price;
    private readonly List<PositionInfo> _positions;
    private readonly string _accountId;

    public TradeStreaming(LightstreamerClient client, PriceStreaming price, List<PositionInfo> positions, string accountId)
    {
        _client = client;
        _price = price;
        _positions = positions;
        _accountId = accountId;
    }

    public void Subscribe(string ingestUrl, string ingestKey)
    {
        var fields = new[] { "OPU", "CONFIRMS", "WOU" }; // TRADE fields [3](https://deepwiki.com/joaquinbejar/ig-client/7.3-trade-and-account-streaming)
        var item = $"TRADE:{_accountId}";

        var tradeSub = new Subscription("DISTINCT", new[] { item }, fields)
        {
            RequestedSnapshot = "yes"
        };

        tradeSub.addListener(new TradeListener(
            ingestUrl: ingestUrl,
            ingestKey: ingestKey,
            onPositionDelta: async (delta) =>
            {
                bool changed = false;

                foreach (var add in delta.Added)
                {
                    if (!_positions.Any(p => p.DealId == add.DealId))
                    {
                        _positions.Add(new PositionInfo(add.DealId, add.Epic, "BUY", 0m, 0m));
                        changed = true;
                    }
                }
                foreach (var rem in delta.Removed)
                {
                    var idx = _positions.FindIndex(p => p.DealId == rem.DealId);
                    if (idx >= 0) { _positions.RemoveAt(idx); changed = true; }
                }

                if (changed)
                    _price.Resubscribe(_positions, _accountId);

                await Task.CompletedTask;
            }));

        _client.subscribe(tradeSub);
        Console.WriteLine("[LS] TRADE subscribed (OPU/CONFIRMS/WOU).");
    }
}