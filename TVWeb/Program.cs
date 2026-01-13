
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using System.Globalization;
using TVWeb.Models;
using TVWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// Keep your configured URL for the streamer
builder.WebHost.UseUrls("https://localhost:7199");

// Razor components + interactive server (IMPORTANT: chain off AddRazorComponents)
builder.Services
    .AddRazorComponents()
    .AddInteractiveServerComponents();

// MVC controllers for your ingest endpoints
builder.Services.AddControllers();

// Position store (singleton)
builder.Services.AddSingleton<PositionsStore>();

var app = builder.Build();

app.UseHsts();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

// ---- Ingest endpoint (unchanged) ----
var ingestKey = app.Configuration["WebIngest:IngestKey"] ?? string.Empty;
var positionsStore = app.Services.GetRequiredService<PositionsStore>();

app.MapPost("/api/stream/positions", async (HttpContext http) =>
{
    try
    {
        if (!http.Request.Headers.TryGetValue("X-INGEST-KEY", out var keys)
            || keys.Count == 0
            || !CryptographicEquals(keys[0], ingestKey))
            return Results.StatusCode((int)HttpStatusCode.Forbidden);

        string body;
        using (var reader = new StreamReader(http.Request.Body))
            body = await reader.ReadToEndAsync();

        if (string.IsNullOrWhiteSpace(body))
            return Results.BadRequest("Empty body");

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("["))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var snapshots = new List<PositionSnapshot>();

            foreach (var el in root.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : "snapshot";
                if (!string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase)) continue;

                var dealId = el.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                var epic = el.TryGetProperty("epic", out var e) ? e.GetString() : null;
                if (string.IsNullOrWhiteSpace(dealId) || string.IsNullOrWhiteSpace(epic)) continue;

                var dir = el.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : null;
                var size = TryDec(el, "size") ?? 0m;
                var bid = TryDec(el, "bid");
                var ask = TryDec(el, "ask");
                var open = TryDec(el, "openLevel");
                var vpp = TryDec(el, "valuePerPoint") ?? 1m;

                snapshots.Add(new PositionSnapshot(
                    DealId: dealId!,
                    Epic: epic!,
                    Direction: dir ?? "",
                    Size: size,
                    Bid: bid,
                    Ask: ask,
                    OpenLevel: open,
                    LastUpdatedUtc: DateTimeOffset.UtcNow,
                    ValuePerPoint: vpp
                ));
            }

            if (snapshots.Count > 0) positionsStore.UpsertRange(snapshots);
            return Results.Ok(new { snapshots = snapshots.Count });
        }
        else
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "tick", StringComparison.OrdinalIgnoreCase))
            {
                var epic = root.TryGetProperty("epic", out var e) ? e.GetString() : null;
                var dealId = root.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                if (string.IsNullOrWhiteSpace(epic) || string.IsNullOrWhiteSpace(dealId))
                    return Results.BadRequest("Tick must include epic and dealId");

                var bid = TryDec(root, "bid");
                var ask = TryDec(root, "ask");

                DateTimeOffset? ts = null;
                if (root.TryGetProperty("timestampUtc", out var tsEl) && tsEl.ValueKind != JsonValueKind.Null)
                {
                    if (tsEl.TryGetDateTimeOffset(out var iso)) ts = iso;
                    else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var ms))
                        ts = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
                ts ??= DateTimeOffset.UtcNow;

                positionsStore.ApplyTick(new PriceTick(epic!, dealId!, bid, ask, ts));
                return Results.Ok(new { tick = true });
            }
            else if (string.Equals(type, "closed", StringComparison.OrdinalIgnoreCase))
            {
                var dealId = root.TryGetProperty("dealId", out var d) ? d.GetString() : null;
                var epic = root.TryGetProperty("epic", out var e) ? e.GetString() : null;
                if (!string.IsNullOrWhiteSpace(dealId))
                    positionsStore.Remove(dealId!, epic);
                return Results.Ok(new { closed = dealId });
            }
            else
            {
                return Results.BadRequest("Unrecognized payload");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[INGEST] error: {ex}");
        return Results.BadRequest("Invalid JSON or payload");
    }
});

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));
app.MapControllers();

// ---- Razor Components host + Interactive Server render mode ----
app.MapRazorComponents<TVWeb.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

static bool CryptographicEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    var ba = System.Text.Encoding.UTF8.GetBytes(a);
    var bb = System.Text.Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
}

static decimal? TryDec(JsonElement el, string name)
{
    if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
    try { return v.GetDecimal(); }
    catch
    {
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }
}
