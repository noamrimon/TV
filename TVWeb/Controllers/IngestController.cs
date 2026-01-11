
using Microsoft.AspNetCore.Mvc;
using TVWeb.Models;
using TVWeb.Services;

namespace TVWeb.Controllers;

[ApiController]
[Route("ingest")]
public sealed class IngestController : ControllerBase
{
    private readonly PositionsStore _store;
    private readonly IConfiguration _config;

    public IngestController(PositionsStore store, IConfiguration config)
    {
        _store = store;
        _config = config;
    }

    private bool Authorized()
    {
        if (!Request.Headers.TryGetValue("X-INGEST-KEY", out var h))
            return false;
        var expected = _config["WebIngest:IngestKey"];
        return h == expected;
    }

    [HttpPost("snapshots")]
    public IActionResult Snapshots([FromBody] IEnumerable<PositionSnapshot> snapshots)
    {
        if (!Authorized()) return Unauthorized();
        _store.UpsertRange(snapshots);
        Console.WriteLine($"[INGEST] Snapshots received: {snapshots?.Count()}");
        return Ok();
    }

    [HttpPost("tick")]
    public IActionResult Tick([FromBody] PriceTick tick)
    {
        if (!Authorized()) return Unauthorized();
        _store.ApplyTick(tick);
        Console.WriteLine($"[INGEST] Tick: {tick?.Epic} {tick?.DealId} {tick?.Bid}/{tick?.Ask} {tick?.TimestampUtc}");
        return Ok();
    }

    public sealed class ClosedDto
    {
        public string? dealId { get; set; }
        public string? epic { get; set; }
    }

    [HttpPost("closed")]
    public IActionResult Closed([FromBody] ClosedDto dto)
    {
        if (!Authorized()) return Unauthorized();
        if (dto.dealId is not null) _store.Remove(dto.dealId, dto.epic);
        Console.WriteLine($"[INGEST] Closed: {dto?.dealId} {dto?.epic}");
        return Ok();
    }
}
