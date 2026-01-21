public sealed class SaxoBase
{
    public string PositionId { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal OpenPrice { get; set; }
    public string Direction => Amount >= 0 ? "BUY" : "SELL";
    public int Uic { get; set; }
    public string Symbol { get; set; } = "";
    public string Currency { get; set; } = "USD";
    public decimal ValuePerPoint { get; set; } = 1;
}
