
namespace TVWeb.Shared;

public static class CurrencyHelper
{
    /// <summary>
    /// Extracts the quote currency (right-side currency) from an FX epic.
    /// Handles formats:
    /// - "SAXO-EURUSD"
    /// - "IG.EURUSD"
    /// - "EURUSD"
    /// - "GBP/JPY"
    /// - "USDJPY"
    /// </summary>

    public static string GetQuoteCurrencyFromEpic(string epic)
    {
        if (string.IsNullOrWhiteSpace(epic))
            return "USD";

        // Clean IG/SAXO prefixes and separators
        var clean = epic
            .Replace("SAXO-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("CS.D.", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".MINI.IP", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".CFD.IP", "", StringComparison.OrdinalIgnoreCase)
            .Replace("IG.", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".", "", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "", StringComparison.OrdinalIgnoreCase)
            .Trim()
            .ToUpperInvariant();

        // Now clean = "EURUSD" or "GBPJPY" etc.
        if (clean.Length >= 6)
            return clean.Substring(clean.Length - 3);

        return "USD";
    }


    /// <summary>
    /// Maps ISO currency codes to display symbols for the UI.
    /// </summary>
    public static string MapCurrencySymbol(string ccy)
    {
        return ccy.ToUpperInvariant() switch
        {
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            "JPY" => "¥",
            "CHF" => "CHf",
            "CAD" => "CA$",
            "AUD" => "AU$",
            "NZD" => "NZ$",
            "HKD" => "HK$",
            "SEK" => "kr",
            "NOK" => "kr",
            _ => ccy // fallback to literal "XYZ"
        };
    }
}