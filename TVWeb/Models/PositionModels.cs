    namespace TVWeb.Models;

    public sealed record PositionSnapshot(
        string DealId, string Epic, string Direction, decimal Size,
        decimal? Bid = null, decimal? Ask = null, decimal? OpenLevel = null,
        decimal? ProfitLoss = null, string? Currency = "USD",
        bool IsWatched = false, DateTimeOffset? LastUpdatedUtc = null, decimal? ValuePerPoint = null,
        string? Broker = null, string? Account = null
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
                decimal total = diff * multiplier * Math.Abs(Size) * ValuePerPoint;

                return Math.Round(total, 2);
            }

        }

    }
