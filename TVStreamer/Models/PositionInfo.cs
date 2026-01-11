namespace TVStreamer.Models;

public sealed record PositionInfo(
    string DealId,
    string Epic,
    string Direction,
    decimal Size,
    decimal OpenLevel
);