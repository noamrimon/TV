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
                new PositionModel
                {
                    Id = s.DealId,
                    Symbol = s.Epic,
                    Type = s.Direction,
                    Bid = s.Bid ?? 0,
                    Ask = s.Ask ?? 0,
                    OpenLevel = s.OpenLevel ?? 0,
                    Size = s.Size,
                    ValuePerPoint = s.ValuePerPoint ?? 1
                },
                (key, existing) => {
                    if (s.Bid.HasValue && s.Bid > 0) existing.Bid = s.Bid.Value;
                    if (s.Ask.HasValue && s.Ask > 0) existing.Ask = s.Ask.Value;
                    if (s.OpenLevel.HasValue && s.OpenLevel > 0) existing.OpenLevel = s.OpenLevel.Value;
                    existing.Size = s.Size;
                    if (s.ValuePerPoint.HasValue) existing.ValuePerPoint = s.ValuePerPoint.Value;
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

public class PositionModel
{
    public string Id { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public decimal Bid { get; set; }
    public decimal Ask { get; set; }
    public decimal OpenLevel { get; set; }
    public decimal Size { get; set; }
    public decimal ValuePerPoint { get; set; }
    public bool IsWatched { get; set; }

    public decimal ProfitLoss
    {
        get
        {
            if (OpenLevel <= 0 || (Bid <= 0 && Ask <= 0)) return 0;

            bool isBuy = Type.Contains("BUY", StringComparison.OrdinalIgnoreCase);
            decimal currentPrice = isBuy ? Bid : Ask;

            if (currentPrice <= 0) return 0;

            // Standard calculation
            decimal diff = isBuy ? (currentPrice - OpenLevel) : (OpenLevel - currentPrice);

            // CORRECTION LOGIC:
            // If VPP is 10,000, it usually means the broker is quoting in "absolute points".
            // For many CFD/Spreadbet accounts, the true multiplier is VPP / 10,000 
            // if the price is already a whole number.
            decimal multiplier = ValuePerPoint;
            if (multiplier >= 10000 && currentPrice > 100)
            {
                multiplier = multiplier / 10000;
            }

            decimal result = diff * Size * multiplier;

            return Math.Round(result, 2);
        }
    }
}