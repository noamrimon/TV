using System.Collections.Immutable;
using System.Text.Json.Nodes;
using TVStreamer.Models;

public sealed class BasePosition
{
    public string Broker { get; init; } = "";
    public string Account { get; init; } = "";
    public string DealId { get; init; } = "";
    public string Epic { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal OpenLevel { get; init; }
    public string Currency { get; init; } = "USD";
    public int? Uic { get; init; }
    public DateTimeOffset LastUpdatedUtc { get; init; }
}

