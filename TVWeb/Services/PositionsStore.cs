using System.Collections.Concurrent;
using TVWeb.Models;

namespace TVWeb.Services;

public class PositionsStore
{
    public readonly ConcurrentDictionary<string, PositionModel> Positions = new();
    public event Action? OnPositionsChanged;

    public void UpsertRange(IEnumerable<PositionSnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {
            Positions.AddOrUpdate(s.DealId,
                (key) => new PositionModel
                {
                    Id = s.DealId,
                    Symbol = s.Epic,
                    Type = s.Direction,
                    Bid = s.Bid ?? 0,
                    Ask = s.Ask ?? 0,
                    OpenLevel = s.OpenLevel ?? 0,
                    Size = s.Size,
                    ValuePerPoint = s.ValuePerPoint ?? 1,
                    Currency = s.Currency ?? "USD"
                },
                (key, existing) => {
                    existing.Bid = s.Bid ?? existing.Bid;
                    existing.Ask = s.Ask ?? existing.Ask;
                    existing.OpenLevel = s.OpenLevel ?? existing.OpenLevel;
                    existing.Size = s.Size;
                    existing.ValuePerPoint = s.ValuePerPoint ?? existing.ValuePerPoint;

                    // Ensure we don't lose the currency if it's sent in a refresh
                    if (!string.IsNullOrEmpty(s.Currency)) existing.Currency = s.Currency;

                    return existing;
                });
        }
        OnPositionsChanged?.Invoke();
    }

    public void ApplyTick(PriceTick tick)
    {
        if (tick == null || string.IsNullOrEmpty(tick.DealId)) return;
        if (Positions.TryGetValue(tick.DealId, out var existing))
        {
            existing.Bid = tick.Bid ?? existing.Bid;
            existing.Ask = tick.Ask ?? existing.Ask;
        }
    }

    public void Remove(string dealId, string? info = null)
    {
        if (Positions.TryRemove(dealId, out _)) OnPositionsChanged?.Invoke();
    }

    public IEnumerable<PositionModel> GetPositions() => Positions.Values;
}