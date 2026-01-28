using com.lightstreamer.client;
using System.Text.Json.Nodes;
using TVStreamer.Models;
using TVStreamer.Services;
using TVStreamer.Listeners;

namespace TVStreamer.Streaming;

public class LightstreamerStreamer : IStreamer, IDisposable
{
    private readonly LightstreamerClient _lsClient;
    private readonly string _accountId;
    private readonly string _brokerName;
    private readonly PositionIngestService _ingestService;
    private readonly string _ingestUrl;
    private readonly string _ingestKey;
    private List<PositionInfo> _pendingPositions = new();
    private bool _isConnected = false;

    public LightstreamerStreamer(
        string rawEndpoint,
        string user,
        string password,
        string accountId,
        string ingestUrl,
        string ingestKey,
        string brokerName)
    {
        _lsClient = new LightstreamerClient(null, "DEFAULT");

        // Clean endpoint for IG
        var cleanEndpoint = rawEndpoint
            .Replace("https://", "")
            .Replace("http://", "")
            .Trim()
            .Split('/')[0];

        _lsClient.connectionOptions.ForcedTransport = "HTTP";
        _lsClient.connectionDetails.ServerAddress = "https://" + cleanEndpoint;
        _lsClient.connectionDetails.User = accountId;
        _lsClient.connectionDetails.Password = password;

        _accountId = accountId;
        _brokerName = brokerName;
        _ingestUrl = ingestUrl;
        _ingestKey = ingestKey;

        // Initialize the service for use in Listeners
        _ingestService = new PositionIngestService(ingestUrl, ingestKey);
    }

    public Task StartAsync()
    {
        _lsClient.addListener(new ConnListener(this));
        _lsClient.connect();
        return Task.CompletedTask;
    }

    public void UpdatePriceSubscriptions(List<PositionInfo> positions)
    {
        // Save these for later in case we aren't connected yet
        _pendingPositions = positions;

        if (!_isConnected)
        {
            Console.WriteLine($"[{_brokerName}] Delaying subscription - Client state: {_lsClient.Status}");
            return;
        }

        var items = positions.Select(p => $"MARKET:{p.Epic}").Distinct().ToArray();
        if (!items.Any()) return;
        //Console.WriteLine($"[{_brokerName}] Attempting sub for Group: {string.Join(", ", items)}");
        // Use the standardized fields for IG
        var fields = new[] { "BID", "OFFER", "UPDATE_TIME" };
        var priceSub = new Subscription("MERGE", items, fields) { DataAdapter = "DEFAULT", RequestedSnapshot = "yes" };

        priceSub.addListener(new PriceListener(items, positions, _ingestService));

        
        // Use Task.Run to ensure we don't block the Loader's loop
        Task.Run(() => {
            try
            {
                _lsClient.subscribe(priceSub);
                Console.WriteLine($"[{_brokerName}] Subscribed to {items.Length} price items.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_brokerName}] Subscription failed: {ex.Message}");
            }
        });
    }

    public void ActivateSubscriptions()
    {
        // TRADE channel gives us OPU (Open Position Update) snapshots and live updates
        var tradeSub = new Subscription("DISTINCT", $"TRADE:{_accountId}", new[] { "OPU", "CONFIRMS", "WOU" })
        {
            RequestedSnapshot = "yes"
        };

        tradeSub.addListener(new LSTradeListener(this));
        _lsClient.subscribe(tradeSub);
        Console.WriteLine($"[{_brokerName}] Trade channel subscription requested.");
    }

    private void HandleUpdate(string opuJson)
    {
        try
        {
            var node = JsonNode.Parse(opuJson);
            if (node == null) return;

            string status = node["status"]?.ToString() ?? "";

            // If deleted, send a close signal, otherwise treat as a registration/snapshot
            if (status == "DELETED")
            {
                var dealId = node["dealId"]?.ToString() ?? "";
                _ = HttpPoster.PostJsonAsync(_ingestUrl, _ingestKey, new { type = "closed", broker = _brokerName, dealId = dealId });
            }
            else
            {
                // Map to BasePosition to use the Register path
                var pos = new BasePosition
                {
                    Broker = _brokerName,
                    AccountId = _accountId,
                    DealId = node["dealId"]?.ToString() ?? "",
                    Epic = node["epic"]?.ToString() ?? "",
                    Amount = node["size"]?.GetValue<decimal>() ?? 0,
                    OpenLevel = node["level"]?.GetValue<decimal>() ?? 0,
                    Direction = node["direction"]?.ToString() ?? "BUY",
                    Currency = node["currency"]?.ToString() ?? "USD"
                };

                _ = _ingestService.RegisterPositionsAsync(new List<BasePosition> { pos });
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LS-TRADE] Error parsing OPU: {ex.Message}");
        }
    }

    public void Dispose() => _lsClient.disconnect();

    // --- INTERNAL LISTENERS ---

    private class LSTradeListener : SubscriptionListener
    {
        private readonly LightstreamerStreamer _parent;
        public LSTradeListener(LightstreamerStreamer parent) => _parent = parent;

        public void onItemUpdate(ItemUpdate update)
        {
            var opu = update.getValue("OPU");
            if (!string.IsNullOrEmpty(opu))
            {
                _parent.HandleUpdate(opu);
            }
        }

        public void onSubscription() => Console.WriteLine("[LS-TRADE] Subscribed.");
        public void onSubscriptionError(int code, string message) => Console.WriteLine($"[LS-TRADE] Error: {code} {message}");
        public void onUnsubscription() { }
        public void onEndOfSnapshot(string n, int p) { }
        public void onClearSnapshot(string n, int p) { }
        public void onItemLostUpdates(string n, int p, int l) { }
        public void onListenStart() { }
        public void onListenEnd() { }
        public void onRealMaxFrequency(string f) { }
        public void onCommandSecondLevelItemLostUpdates(int l, string k) { }
        public void onCommandSecondLevelSubscriptionError(int c, string m, string k) { }
    }

    private class ConnListener : ClientListener
    {
        private readonly LightstreamerStreamer _parent;
        public ConnListener(LightstreamerStreamer parent) => _parent = parent;

        public void onStatusChange(string status)
        {
            Console.WriteLine($"[LS-CONN] {status}");

            if (status.StartsWith("CONNECTED"))
            {
                _parent._isConnected = true;
                _parent.ActivateSubscriptions(); // Trade channel

                // Now that we are connected, trigger any pending prices
                if (_parent._pendingPositions.Any())
                {
                    _parent.UpdatePriceSubscriptions(_parent._pendingPositions);
                }
            }
            else if (status.StartsWith("DISCONNECTED"))
            {
                _parent._isConnected = false;
            }
        }
        public void onListenEnd() { }
        public void onListenStart() { }
        public void onPropertyChange(string p) { }
        public void onServerError(int code, string message) => Console.WriteLine($"[LS-SRV-ERR] {code}: {message}");
    }
}