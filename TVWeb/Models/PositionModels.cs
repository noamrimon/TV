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

            // 1. Calculate raw difference (e.g., -0.00033)
            decimal diff = isBuy ? (currentPrice - OpenLevel) : (OpenLevel - currentPrice);

            decimal multiplier = 1.0m;
            string s = Symbol.ToUpper();

            // 2. Determine Multiplier
            if (currentPrice >= 1000)
            {
                multiplier = 0.0001m; // For EURUSD at 11637
            }
            else if (s.Contains("JPY"))
            {
                // If your JPY is working with 1.0, keep it. 
                // If JPY becomes 0, this needs to be 1.0.
                multiplier = 1.0m;
            }
            else
            {
                // For ALL standard FX (AUDUSD, USDCAD, GBPUSD)
                multiplier = 1.0m;
            }

            // 3. CRITICAL: Multiply everything FIRST, then round at the very end.
            decimal total = diff * multiplier * Size * ValuePerPoint;

            return Math.Round(total, 2);
        }
    }
}
