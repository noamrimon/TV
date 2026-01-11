
namespace TVWeb.Models;

public sealed record PositionSnapshot(
    string DealId,
    string Epic,
    string Direction,
    decimal Size,
    decimal? Bid,
    decimal? Ask,
    decimal? OpenLevel,
    DateTimeOffset? LastUpdatedUtc);

public sealed record PriceTick(
    string Epic,
    string DealId,
    decimal? Bid,
    decimal? Ask,
    DateTimeOffset? TimestampUtc);
