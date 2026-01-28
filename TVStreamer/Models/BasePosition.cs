using System.Text.Json.Nodes;

namespace TVStreamer.Models
{
    public sealed class BasePosition
    {
        public string Broker { get; init; } = "";
        public string AccountId { get; set; } = ""; // Keep only ID
        public string DealId { get; init; } = "";
        public string Epic { get; init; } = "";
        public decimal Amount { get; init; }
        public decimal OpenLevel { get; init; }
        public string Currency { get; init; } = "USD";
        public string Direction { get; init; } = "BUY";
        public int? Uic { get; init; }
        public DateTimeOffset LastUpdatedUtc { get; init; }
        public decimal ValuePerPoint { get; init; } = 1.0m; // Ensure this is definitely used
        public decimal ScalingFactor { get; init; } = 1.0m;
        public static class FormatHelper
        {
            public static JsonValue ToStandardAmount(decimal amount) => JsonValue.Create(amount);
        }
    }
}