using System.Globalization;
using TVWeb.Models;
using TVWeb.Services;



namespace TVWeb.Components.Pages
{
    public class FrankfurterResponse { public Dictionary<string, decimal> Rates { get; set; } = new(); }
    public partial class Trade
    {
        private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(500));
        private readonly CancellationTokenSource _cts = new();
        private string _searchTerm = "";
        private Dictionary<string, decimal> _usdRates = new();
        private bool _isRateLoading = false;

        private static readonly Dictionary<string, string> _currencySymbols = new();

        protected override async Task OnInitializedAsync()
        {
            await LoadRates();
            // Your existing streaming/data logic...
        }

        private async Task LoadRates()
        {
            if (_usdRates.Any()) return;
            _isRateLoading = true;
            try
            {
                using var http = new HttpClient();
                // Frankfurter is free, no key needed. 
                // base=USD gets rates relative to 1 US Dollar.
                var data = await http.GetFromJsonAsync<FrankfurterResponse>("https://api.frankfurter.dev/v1/latest?base=USD");
                if (data?.Rates != null)
                {
                    _usdRates = data.Rates;
                    _usdRates["USD"] = 1.0m;
                }
            }
            catch { /* Log error or use hardcoded fallbacks */ }
            finally { _isRateLoading = false; }
        }
        public decimal GetTotalSelectedUsd(IEnumerable<PositionModel> selectedPositions)
        {
            decimal totalUsd = 0;

            foreach (var pos in selectedPositions)
            {
                string iso = ExtractTermCurrency(pos.Symbol);
                decimal pl = pos.ProfitLoss;

                if (iso == "USD")
                {
                    totalUsd += pl;
                }
                else if (_usdRates.TryGetValue(iso, out var rate) && rate != 0)
                {
                    decimal converted = pl / rate;
                    totalUsd += converted;

                    // Debugging: Check your F12 console to see if the math looks right
                    Console.WriteLine($"Converting {pl} {iso} to USD using rate {rate}. Result: {converted}");
                }
                else
                {
                    // If the rate isn't loaded yet, we add it as-is (fallback)
                    totalUsd += pl;
                    Console.WriteLine($"Warning: No rate found for {iso}. Adding raw value.");
                }
            }

            return Math.Round(totalUsd, 2);
        }
        private string GetFormattedPL(PositionModel pos)
        {
            string iso = ExtractTermCurrency(pos.Symbol);
            decimal val = pos.ProfitLoss;

            // 1. Get the culture based on the ISO code
            var culture = GetCultureFromIso(iso);

            // 2. Format the number using the culture's rules (decimals, sign placement)
            string formatted = val.ToString("C", culture);

            // 3. APPLY OVERRIDES: Fix the generic '$' for specific dollar-based currencies
            return iso switch
            {
                "CAD" => formatted.Replace("$", "CA$"),
                "SGD" => formatted.Replace("$", "SGD "),
                "AUD" => formatted.Replace("$", "A$"),
                "NZD" => formatted.Replace("$", "NZ$"),
                "HKD" => formatted.Replace("$", "HK$"),
                _ => formatted // USD, GBP, EUR, JPY etc. will use their native symbols
            };
        }
        private CultureInfo GetCultureFromIso(string isoCode)
        {
            try
            {
                // Find the first culture that uses this ISO currency code
                var culture = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                    .Select(c => new { Culture = c, Region = new RegionInfo(c.Name) })
                    .FirstOrDefault(x => x.Region.ISOCurrencySymbol == isoCode)
                    ?.Culture;

                return culture ?? CultureInfo.CurrentCulture;
            }
            catch
            {
                return CultureInfo.CurrentCulture;
            }
        }
        private string ExtractTermCurrency(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "USD";

            // 1. Split by dots to handle "CS.D.USDSGD.MINI.IP"
            var parts = symbol.Split('.');
            foreach (var part in parts)
            {
                // The currency pair is always the 6-letter part
                if (part.Length == 6 && part.All(char.IsLetter))
                {
                    // SKIP the first 3 (Base) and TAKE the last 3 (Term/Profit Currency)
                    return part.Substring(3, 3).ToUpper();
                }
            }

            // 2. Handle "USD/SGD" format
            if (symbol.Contains('/'))
            {
                return symbol.Split('/').Last().Trim().ToUpper();
            }

            // 3. Handle raw "USDSGD"
            if (symbol.Length == 6)
            {
                return symbol.Substring(3, 3).ToUpper();
            }

            return "USD";
        }

        private string GetSymbolFromIsoCode(string isoCode)
        {
            try
            {
                return CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                    .Select(c => new RegionInfo(c.Name))
                    .FirstOrDefault(r => r.ISOCurrencySymbol == isoCode)
                    ?.CurrencySymbol ?? isoCode;
            }
            catch
            {
                return isoCode;
            }
        }

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

        private decimal TotalPLUsd
        {
            get
            {
                // Use 'Store' here as defined in your @inject Store
                if (Store?.Positions == null) return 0;

                var allPositions = Store.Positions.Values;

                // Filter for positions that are selected/watched
                var selected = allPositions.Where(p => p.IsWatched).ToList();

                if (!selected.Any()) return 0;

                decimal total = 0;
                foreach (var pos in selected)
                {
                    // Extract the Term currency (e.g., "SGD" or "CAD")
                    string iso = ExtractTermCurrency(pos.Symbol);

                    if (iso == "USD")
                    {
                        total += pos.ProfitLoss;
                    }
                    else if (_usdRates.TryGetValue(iso, out var rate) && rate != 0)
                    {
                        // Divide the Term P/L by the USD rate (e.g., 11.00 SGD / 1.34)
                        total += pos.ProfitLoss / rate;
                    }
                    else
                    {
                        // Fallback if Frankfurter rates haven't loaded yet
                        total += pos.ProfitLoss;
                    }
                }
                return Math.Round(total, 2);
            }
        }

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