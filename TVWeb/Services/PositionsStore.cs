
using TVWeb.Models;

namespace TVWeb.Services;

public sealed class PositionsStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PositionSnapshot> _byDealId = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? PositionsChanged;

    public List<PositionSnapshot> GetAll()
    {
        lock (_sync) return _byDealId.Values.ToList();
    }

    public void UpsertRange(IEnumerable<PositionSnapshot> snapshots)
    {
        lock (_sync)
        {
            foreach (var s in snapshots)
                _byDealId[s.DealId] = s;
        }
        PositionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyTick(PriceTick tick)
    {
        lock (_sync)
        {
            if (_byDealId.TryGetValue(tick.DealId, out var s))
            {
                var updated = s with
                {
                    Bid = tick.Bid ?? s.Bid,
                    Ask = tick.Ask ?? s.Ask,
                    LastUpdatedUtc = tick.TimestampUtc
                };
                _byDealId[tick.DealId] = updated;
            }
        }
        PositionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Remove(string dealId, string? epic)
    {
        lock (_sync)
        {
            if (_byDealId.Remove(dealId))
                PositionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
