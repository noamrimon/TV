    namespace TVWeb.Models;

    public sealed record PositionSnapshot(
        string DealId, string Epic, string Direction, decimal Size,
        decimal? Bid = null, decimal? Ask = null, decimal? OpenLevel = null,
        decimal? ProfitLoss = null, string? Currency = "USD",
        bool IsWatched = false, DateTimeOffset? LastUpdatedUtc = null, decimal? ValuePerPoint = null, decimal? ScalingFactor = null,
        string? Broker = null, string? Account = null, string? Type = null, string CurrencySymbol = "$"
    );

    public sealed record PriceTick(string Epic, string DealId, decimal? Bid, decimal? Ask, DateTimeOffset? TimestampUtc);

    public class PositionModel
    {
        public string Id { get; set; } = string.Empty;
        public string Broker { get; set; } = string.Empty;
        public string Account { get; set; } = string.Empty;
        public string Symbol { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal OpenLevel { get; set; }
        public decimal Size { get; set; }
        public decimal ValuePerPoint { get; set; }
        public decimal ScalingFactor { get; set; }
        public string Currency { get; set; } = "USD";
        public bool IsWatched { get; set; }
        public string CurrencySymbol { get; set; } = "$";
    public decimal SpreadPips
    {
        get
        {
            if (Ask == 0 || Bid == 0) return 0;

            decimal rawSpread = Math.Abs(Ask - Bid);

            // 1. Handle JPY (e.g., 145.50) -> 1 pip is 0.01
            if (Symbol != null && Symbol.Contains("JPY"))
            {
                return rawSpread * 100m;
            }

            // 2. Handle standard FX (e.g., 1.0850) -> 1 pip is 0.0001
            // If the price is small (under 10) it's almost certainly standard FX
            if (Ask < 10)
            {
                return rawSpread * 10000m;
            }

            // 3. Handle Indices/Gold (e.g., 18000 or 2000) -> 1 point is 1.0
            return rawSpread;
        }
    }
    public decimal ProfitLoss
    {
        get
        {
            // 1. Validation: If no price has streamed yet, P/L is 0
            if (OpenLevel <= 0 || (Bid <= 0 && Ask <= 0)) return 0;

            // 2. Determine Close Price: 
            // If you are LONG (BUY), you exit at the BID.
            // If you are SHORT (SELL), you exit at the ASK.
            bool isBuy = Type.Contains("BUY", StringComparison.OrdinalIgnoreCase);
            decimal currentPrice = isBuy ? Bid : Ask;

            // 3. Calculate Directional Difference
            // BUY: (Market - Open) | SELL: (Open - Market)
            decimal diff = isBuy ? (currentPrice - OpenLevel) : (OpenLevel - currentPrice);

            // 4. THE UNIVERSAL FORMULA
            // ScalingFactor: 0.001 for Bitcoin, 100 or 1000 for JPY, 1.0 for Saxo.
            // ValuePerPoint: The 'Contract Size' or 'Currency Value' per point.
            // Size: The amount of contracts or units.
            decimal total = diff * ScalingFactor * ValuePerPoint * Math.Abs(Size);

            return Math.Round(total, 2);
        }
    }
}
