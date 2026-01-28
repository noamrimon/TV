using System.Collections.Concurrent;
using System.Security.Principal;
using TVWeb.Models;
using TVWeb.Shared;

namespace TVWeb.Services;

public class PositionsStore
{
    public readonly ConcurrentDictionary<string, PositionModel> Positions = new();
    public event Action? OnPositionsChanged;


    public void UpsertRange(IEnumerable<PositionSnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {

            // Normalize helper
            static string NormalizeDir(string? dir)
                => string.IsNullOrWhiteSpace(dir) ? "" : dir.Trim().ToUpperInvariant();

            string InferDirectionFromSize(decimal size)
                => size < 0 ? "SELL" : (size > 0 ? "BUY" : "");

            Positions.AddOrUpdate(s.DealId,
                // INSERT
                key => {
                    var dir = NormalizeDir(s.Direction);
                    if (string.IsNullOrEmpty(dir))
                    {
                        // Fallback: infer from size if direction not supplied on first snapshot
                        dir = InferDirectionFromSize(s.Size);
                    }

                    //var quote = CurrencyHelper.GetQuoteCurrencyFromEpic(s.Epic);
                    var quote = s.Currency;
                    var symbol = CurrencyHelper.MapCurrencySymbol(quote);

                    return new PositionModel
                    {
                        Id = s.DealId,
                        Broker = s.Broker ?? "",
                        Account = s.Account ?? "",
                        Symbol = s.Epic,
                        Type = dir,                               // <- ensure something on insert
                        Bid = s.Bid ?? 0,
                        Ask = s.Ask ?? 0,
                        OpenLevel = s.OpenLevel ?? 0,
                        Size = s.Size,
                        ValuePerPoint = s.ValuePerPoint ?? 1m,
                        ScalingFactor = s.ScalingFactor ?? 1m,
                        Currency = quote,
                        CurrencySymbol = symbol
                    };
                },
                // UPDATE
                (key, existing) =>
                {

                    var quote = s.Currency;
                    var symbol = CurrencyHelper.MapCurrencySymbol(quote);

                    existing.Currency = quote;
                    existing.CurrencySymbol = symbol;

                    existing.Bid = s.Bid ?? existing.Bid;
                    existing.Ask = s.Ask ?? existing.Ask;
                    existing.OpenLevel = s.OpenLevel ?? existing.OpenLevel;
                    existing.Size = s.Size;
                    existing.ValuePerPoint = s.ValuePerPoint ?? existing.ValuePerPoint;

                    if (!string.IsNullOrEmpty(s.Broker)) existing.Broker = s.Broker!;
                    if (!string.IsNullOrEmpty(s.Account)) existing.Account = s.Account!;
                    if (!string.IsNullOrEmpty(s.Currency)) existing.Currency = s.Currency;

                    // 🔑 Direction update logic:
                    var newDir = NormalizeDir(s.Direction);
                    if (!string.IsNullOrEmpty(newDir))
                    {
                        existing.Type = newDir;                     // <- update direction when provided
                    }
                    else if (string.IsNullOrEmpty(existing.Type))
                    {
                        // If we still never had a direction, infer from size sign as a fallback
                        var inferred = InferDirectionFromSize(s.Size);
                        if (!string.IsNullOrEmpty(inferred))
                            existing.Type = inferred;
                    }

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