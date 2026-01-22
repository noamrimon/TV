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

        // Helper to extract a readable Market name from an IG Epic
        private string GetMarketDisplay(string epic)
        {
            // IG EPIC: "CS.D.EURUSD.MINI.IP"
            var parts = epic.Split('.');
            if (parts.Length >= 3)
            {
                var pair = parts[2];
                if (pair.Length == 6 && pair.All(char.IsLetter))
                    return $"{pair.Substring(0, 3)}/{pair.Substring(3, 3)}";
                return pair;
            }
            // SAXO symbols like "SAXO-EURUSD" or "SAXO-5025327830"
            if (epic.StartsWith("SAXO-", StringComparison.OrdinalIgnoreCase))
            {
                var sym = epic.Substring(5);
                if (sym.Length == 6 && sym.All(char.IsLetter))
                    return $"{sym.Substring(0, 3)}/{sym.Substring(3, 3)}";
                return sym; // UIC or other format
            }
            // raw 6-letter symbol?
            if (epic.Length == 6 && epic.All(char.IsLetter))
                return $"{epic.Substring(0, 3)}/{epic.Substring(3, 3)}";
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

        private IEnumerable<PositionModel> FilteredPositions => Store.GetPositions()
            .Where(p => string.IsNullOrWhiteSpace(_searchTerm)
                     || p.Symbol.Contains(_searchTerm, StringComparison.OrdinalIgnoreCase)
                     || GetMarketDisplay(p.Symbol).Contains(_searchTerm, StringComparison.OrdinalIgnoreCase));

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
                if (Store?.Positions == null) return 0;
                var x = Store.Positions.Values.Where(p => p.IsWatched).ToList();
                foreach (var y in x) 
                {
                    Console.WriteLine($"[IsWatched?] = {y.IsWatched}. [Broker?] = {y.Broker}");
                }
                var selected = Store.Positions.Values.Where(p => p.IsWatched).ToList();
                if (!selected.Any())
                {
                    return 0;
                }
                decimal total = 0;
                foreach (var pos in selected)
                {
                    string iso = pos.Currency;
                    decimal pl = pos.ProfitLoss;

                    if (iso == "USD")
                        total += pl;
                    else if (_usdRates.TryGetValue(iso, out var rate) && rate != 0)
                        total += pl / rate;
                    else
                        total += pl;
                }

                return Math.Round(total, 2);
            }
        }


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
    }
}