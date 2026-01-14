using System.Globalization;
using TVWeb.Models;

namespace TVWeb.Components.Pages
{
    public partial class Trade
    {
        private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(500));
        private readonly CancellationTokenSource _cts = new();
        private string _searchTerm = "";


        // Helper to extract a readable Market name from an IG Epic
        private string GetMarketDisplay(string epic)
        {
            var parts = epic.Split('.');
            if (parts.Length >= 3)
            {
                var pair = parts[2]; // Usually "EURUSD" or "USDJPY"
                if (pair.Length == 6 && !pair.Contains("100")) // standard FX
                    return $"{pair.Substring(0, 3)}/{pair.Substring(3, 3)}";
                return pair; // Indices or Commodities
            }
            return epic;
        }
        // Logic for Select All
        private bool SelectAll
        {
            get => FilteredPositions.Any() && FilteredPositions.All(p => p.IsWatched);
            set { foreach (var p in FilteredPositions) p.IsWatched = value; }
        }
        // Get the top 10 unique markets (based on the first 3-6 letters of the Epic)
        private IEnumerable<string> DynamicMarkets => Store.GetPositions()
            .Select(p => GetMarketDisplay(p.Symbol))
            .Distinct()
            .Take(10);

        // Get unique accounts (assuming you have an AccountId or similar)
        private IEnumerable<string> DynamicAccounts => Store.GetPositions()
            .Select(p => p.Currency) // Or use a 'AccountId' property if available
            .Distinct();

        private IEnumerable<PositionModel> FilteredPositions => Store.GetPositions()
            .Where(p => string.IsNullOrWhiteSpace(_searchTerm)
                     || p.Symbol.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase)
                     || GetMarketDisplay(p.Symbol).Contains(_searchTerm, StringComparison.OrdinalIgnoreCase));

        private static readonly Dictionary<string, decimal> FX_RATES = new(StringComparer.OrdinalIgnoreCase)
    {
        { "USD", 1.0m }, { "GBP", 1.28m }, { "EUR", 1.09m }, { "JPY", 0.0065m }
    };

        private string FormatLocalPL(decimal pl, string currency)
        {
            var culture = CURRENCY_CULTURES.GetValueOrDefault(currency, CultureInfo.InvariantCulture);
            // "C2" handles the symbol placement (e.g., -$250 or -JP¥250)
            return pl.ToString("C2", culture);
        }

        private string FormatPrice(decimal price)
        {
            // If the number is large (like an Index price 11637.6), format without thousands separator
            // and show 1 decimal place. Otherwise, show 4 or 5 for FX.
            if (price > 1000)
            {
                return price.ToString("F1", CultureInfo.InvariantCulture);
            }
            return price.ToString("N5", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        }

        private decimal TotalPLUsd => Store.GetPositions()
                .Where(p => p.IsWatched)
                .Sum(p =>
                {
                    decimal localPl = CalculatePL(p);
                    string ccy = GetQuoteCurrency(p.Symbol);
                    return localPl * FX_RATES.GetValueOrDefault(ccy, 1.0m);
                });

        private int WatchedCount => Store.GetPositions().Count(p => p.IsWatched);

        protected override void OnInitialized() => _ = RefreshLoop();

        private async Task RefreshLoop()
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    if (!_cts.IsCancellationRequested) await InvokeAsync(StateHasChanged);
                }
            }
            catch { }
        }

        public void Dispose() { _cts.Cancel(); _cts.Dispose(); _timer.Dispose(); }
        private static readonly Dictionary<string, CultureInfo> CURRENCY_CULTURES = new(StringComparer.OrdinalIgnoreCase)
    {
        { "USD", CultureInfo.GetCultureInfo("en-US") },
        { "GBP", CultureInfo.GetCultureInfo("en-GB") },
        { "EUR", CultureInfo.GetCultureInfo("fr-FR") }, // Displays €
        { "JPY", CultureInfo.GetCultureInfo("ja-JP") }  // Displays ¥
    };

        private string FormatLocalPL(PositionModel pos)
        {
            // 1. Correct Currency Detection for FX Pairs
            string ccy = "USD"; // Default
            string s = pos.Symbol.ToUpper();

            // Look for the "Quote" currency (the second one)
            if (s.Contains("JPY")) ccy = "JPY";
            else if (s.Contains("EURUSD") || s.EndsWith("USD.IP")) ccy = "USD";
            else if (s.Contains("GBPEUR") || s.EndsWith("EUR.IP")) ccy = "EUR";
            else if (s.Contains("EURGBP") || s.EndsWith("GBP.IP")) ccy = "GBP";

            var culture = CURRENCY_CULTURES.GetValueOrDefault(ccy, CultureInfo.GetCultureInfo("en-US"));

            if (ccy == "JPY")
            {
                string sign = pos.ProfitLoss < 0 ? "-" : "";
                return $"{sign}JP¥{Math.Abs(pos.ProfitLoss):N0}";
            }

            return pos.ProfitLoss.ToString("C2", culture);
        }
        // 1. Detect Quote Currency (e.g., EURUSD -> USD, USDJPY -> JPY)
        private string GetQuoteCurrency(string symbol)
        {
            var parts = symbol.ToUpper().Split('.');
            if (parts.Length < 3) return "USD";
            var epic = parts[2];

            if (epic.Contains("JPY")) return "JPY";
            if (epic.Contains("GBP")) return "GBP";
            if (epic.Contains("EUR")) return "EUR";
            if (epic.Contains("SGD")) return "SGD";
            if (epic.EndsWith("USD")) return "USD";

            return "USD"; // Default
        }
        public decimal CalculatePL(PositionModel pos)
        {
            if (pos.OpenLevel <= 0 || (pos.Bid <= 0 && pos.Ask <= 0)) return 0;

            bool isBuy = pos.Type.Contains("BUY", StringComparison.OrdinalIgnoreCase);
            decimal currentPrice = isBuy ? pos.Bid : pos.Ask;
            decimal diff = isBuy ? (currentPrice - pos.OpenLevel) : (pos.OpenLevel - currentPrice);

            decimal multiplier = 1.0m;
            string quoteCcy = GetQuoteCurrency(pos.Symbol);

            if (currentPrice >= 1000)
            {
                // Case: EURUSD at 11654
                multiplier = 0.0001m;
            }
            else if (quoteCcy == "JPY")
            {
                // Case: USDJPY at 158.598. Diff is -0.004.
                // If we want points, 1 point = 0.01. So multiplier = 100.
                // But if your VPP is high, keep it 1.0.
                // Let's use 1.0 to stay consistent with your working JPY.
                multiplier = 1.0m;
            }

            // CRITICAL: No rounding until the very end
            decimal rawPl = diff * multiplier * pos.Size * pos.ValuePerPoint;
            return Math.Round(rawPl, 2);
        }
    }
}