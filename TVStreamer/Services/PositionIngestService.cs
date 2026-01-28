using System.Collections.Generic;
using TVStreamer.Models;
using TVStreamer.Streaming;

namespace TVStreamer.Services
{
    public class PositionIngestService
    {
        private readonly string _url;
        private readonly string _key;

        public PositionIngestService(string url, string key)
        {
            _url = url;
            _key = key;
        }

        // PATH 1: The "Heavy" path (Initial Load / New Positions)
        public async Task RegisterPositionsAsync(List<BasePosition> positions)
        {
            if (positions == null || !positions.Any()) return;

            var snapshots = positions.Select(p => new {
                type = "snapshot",
                dealId = p.DealId,
                epic = p.Epic,
                direction = p.Direction,
                size = p.Amount,
                openLevel = p.OpenLevel,
                currency = p.Currency,
                broker = p.Broker,
                account = p.AccountId,
                valuePerPoint = p.ValuePerPoint,
                scalingFactor = p.ScalingFactor
            }).ToList();

            // Note: Using PostPrioritySnapshotAsync for registration to ensure they land first
            await HttpPoster.PostPrioritySnapshotAsync(_url, _key, snapshots);
        }

        // PATH 2: The "Light" path (High-frequency Ticks)
        public async Task UpdatePriceAsync(string dealId, string epic, decimal? bid, decimal? ask, DateTimeOffset? ts = null)
        {
            var tick = new
            {
                type = "tick",
                dealId = dealId,
                epic = epic,
                bid = bid,
                ask = ask,
                timestampUtc = ts ?? DateTimeOffset.UtcNow
            };
            Console.WriteLine($"[{epic}] INGESTING: {tick}");
            await HttpPoster.PostJsonAsync(_url, _key, tick);
        }
    }
}