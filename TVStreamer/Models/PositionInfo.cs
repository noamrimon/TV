namespace TVStreamer.Models;

public sealed record PositionInfo(
    string DealId,
    string Epic,
    string Direction,
    decimal Size,
    decimal OpenLevel,
    decimal CurrentLevel = 0,
    decimal ProfitLoss = 0,
    decimal Bid = 0,
    string Currency = "USD",
    string AccountName = "",
    DateTime UpdateTime = default
);