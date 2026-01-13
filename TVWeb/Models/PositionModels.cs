namespace TVWeb.Models;

public sealed record PositionSnapshot(
    string DealId, string Epic, string Direction, decimal Size,
    decimal? Bid = null, decimal? Ask = null, decimal? OpenLevel = null,
    decimal? ProfitLoss = null, string? Currency = "USD",
    bool IsWatched = false, DateTimeOffset? LastUpdatedUtc = null, decimal? ValuePerPoint = null
);

public sealed record PriceTick(string Epic, string DealId, decimal? Bid, decimal? Ask, DateTimeOffset? TimestampUtc);

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
    public string Currency { get; set; } = "USD";
    public bool IsWatched { get; set; }

    public decimal ProfitLoss
    {
        get
        {
            if (OpenLevel <= 0 || (Bid <= 0 && Ask <= 0)) return 0;
            bool isBuy = Type.Contains("BUY", StringComparison.OrdinalIgnoreCase);
            decimal currentPrice = isBuy ? Bid : Ask;
            if (currentPrice <= 0) return 0;

            decimal diff = isBuy ? (currentPrice - OpenLevel) : (OpenLevel - currentPrice);
            decimal multiplier = ValuePerPoint;
            if (multiplier >= 10000 && currentPrice > 100) multiplier /= 10000;

            return Math.Round(diff * Size * multiplier, 2);
        }
    }
}