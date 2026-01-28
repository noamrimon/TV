using System.Globalization;
using com.lightstreamer.client;
using TVStreamer.Models;
using TVStreamer.Services;

namespace TVStreamer.Listeners
{
    // Ensure it inherits from SubscriptionListener
    sealed class PriceListener : SubscriptionListener
    {
        private readonly string[] _items;
        private readonly List<PositionInfo> _positions;
        private readonly PositionIngestService _ingestService;

        public PriceListener(string[] items, List<PositionInfo> positions, PositionIngestService ingestService)
        {
            _items = items;
            _positions = positions;
            _ingestService = ingestService;
        }

        // This is the method the Lightstreamer DLL calls automatically
        public void onItemUpdate(ItemUpdate itemUpdate)
        {
            // 1. Identify which Epic this update belongs to
            var pos = itemUpdate.ItemPos;
            var epic = (pos >= 1 && pos <= _items.Length)
                ? _items[pos - 1].Replace("MARKET:", "") // Strip the prefix
                : string.Empty;

            // 2. Extract Prices (IG uses BID/OFFER or BIDPRICE1/ASKPRICE1)
            var bidStr = itemUpdate.getValue("BID");
            var askStr = itemUpdate.getValue("OFFER");

            decimal? bid = decimal.TryParse(bidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var b) ? b : null;
            decimal? ask = decimal.TryParse(askStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var a) ? a : null;

            // 3. Find matching deals and push to the Ingest Service
            var deals = _positions.Where(p => p.Epic == epic).ToList();

            if (deals.Count == 0) return;

            foreach (var deal in deals)
            {
                // THIS is where the data leaves the listener and hits your service
                _ = _ingestService.UpdatePriceAsync(deal.DealId, epic, bid, ask);
            }

            // Console.WriteLine($"[TICK] {epic} Bid: {bid} Ask: {ask}");
        }

        // Mandatory interface methods
        public void onSubscription() => Console.WriteLine("[LS] Price Subscription Active.");
        public void onSubscriptionError(int code, string message) => Console.WriteLine($"[LS] Sub Error: {code} - {message}");
        public void onUnsubscription() { }
        public void onEndOfSnapshot(string itemName, int itemPos) { }
        public void onClearSnapshot(string itemName, int itemPos) { }
        public void onItemLostUpdates(string itemName, int itemPos, int lostUpdates) { }
        public void onListenStart() { }
        public void onListenEnd() { }
        public void onRealMaxFrequency(string frequency) { }
        public void onCommandSecondLevelItemLostUpdates(int lostUpdates, string key) { }
        public void onCommandSecondLevelSubscriptionError(int code, string message, string key) { }
    }
}