
using Microsoft.AspNetCore.Mvc;
using System;
using System.Globalization;
using System.Net;
using System.Security.Principal;
using System.Text.Json;
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
        // Debugging request - remove when done
        http.Request.EnableBuffering();
        var length = http.Request.ContentLength;
        http.Request.Body.Position = 0;
        using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
        string rawBody = await reader.ReadToEndAsync();
        http.Request.Body.Position = 0;
        Console.WriteLine($"RAW INCOMING: {rawBody}");
        /////////////////////////////////////////////////

        if (!http.Request.Headers.TryGetValue("X-INGEST-KEY", out var keys)
            || keys.Count == 0
            || !CryptographicEquals(keys[0], ingestKey))
            return Results.StatusCode((int)HttpStatusCode.Forbidden);

        string body = await new StreamReader(http.Request.Body).ReadToEndAsync();
        Console.WriteLine($">>>> [INGEST-RECEIVE] Length: {body.Length} | StartsWith '[': {body.TrimStart().StartsWith("[")}");
        if (string.IsNullOrWhiteSpace(body))
            return Results.BadRequest("Empty body");

        var trimmed = body.TrimStart();

        // --- CASE A: ARRAY (Saxo Initial Load / IG REST Load) ---
        if (trimmed.StartsWith("["))
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var snapshots = new List<PositionSnapshot>();

            foreach (var el in root.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var t) ? t.GetString() : "snapshot";
                if (!string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase)) continue;

                var dealId = TryString(el, "dealId") ?? TryString(el, "DealId");
                var epic = TryString(el, "epic") ?? TryString(el, "Epic");
                if (dealId == null)
                {
                    Console.WriteLine("!!!! [WEB-DEBUG] Could not find DealId property in array element.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(dealId) || string.IsNullOrWhiteSpace(epic)) continue;

                snapshots.Add(new PositionSnapshot(
                    DealId: dealId!,
                    Epic: epic!,
                    Direction: el.TryGetProperty("direction", out var dirEl) ? dirEl.GetString() : "",
                    Currency: el.TryGetProperty("currency", out var cur) ? cur.GetString() : "",
                    Size: TryDec(el, "size") ?? 0m,
                    Bid: TryDec(el, "Bid"),
                    Ask: TryDec(el, "Ask"),
                    OpenLevel: TryDec(el, "openLevel"),
                    LastUpdatedUtc: DateTimeOffset.UtcNow,
                    ValuePerPoint: TryDec(el, "valuePerPoint") ?? 1m,
                    ScalingFactor: TryDec(el, "scalingFactor") ?? 1m,
                    Broker: TryString(el, "broker"),
                    Account: TryString(el, "account")
                ));
            }

            if (snapshots.Count > 0)
            {
                Console.WriteLine($"[INGEST] Upserting {snapshots.Count} positions from Array.");
                positionsStore.UpsertRange(snapshots);
            }
            return Results.Ok(new { snapshots = snapshots.Count });
        }

        // --- CASE B: SINGLE OBJECT (Ticks, Closed, Single Saxo Updates) ---
        else
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (string.Equals(type, "tick", StringComparison.OrdinalIgnoreCase))
            {
                var epic = TryString(root, "epic");
                var dealId = TryString(root, "dealId");

                if (string.IsNullOrWhiteSpace(epic) || string.IsNullOrWhiteSpace(dealId))
                    return Results.BadRequest("Tick must include epic and dealId");

                // DIAGNOSTIC LOG: Check if IG trade exists in store
                bool exists = positionsStore.GetPositions().Any(p => p.Id.Equals(dealId, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($">>>> [IG-TICK] DealId: {dealId} | Exists In Store: {exists} | Bid: {TryDec(root, "bid")}");

                positionsStore.ApplyTick(new PriceTick(epic!, dealId!, TryDec(root, "bid"), TryDec(root, "ask"), DateTimeOffset.UtcNow));
                return Results.Ok(new { tick = true });
            }
            else if (string.Equals(type, "closed", StringComparison.OrdinalIgnoreCase))
            {
                var dealId = TryString(root, "dealId");
                var epic = TryString(root, "epic");
                if (!string.IsNullOrWhiteSpace(dealId))
                    positionsStore.Remove(dealId!, epic);
                return Results.Ok(new { closed = dealId });
            }
            else if (string.Equals(type, "snapshot", StringComparison.OrdinalIgnoreCase))
            {
                var dealId = TryString(root, "dealId");
                var epic = TryString(root, "epic");
                if (string.IsNullOrWhiteSpace(dealId)) return Results.BadRequest("Missing DealId");
                positionsStore.UpsertRange(new[]
                {
                    new PositionSnapshot(
                        DealId: dealId!,
                        Epic: epic!,
                        Direction: TryString(root, "direction") ?? "",
                        Size: TryDec(root, "size") ?? 0m,
                        Bid: TryDec(root, "bid"),
                        Ask: TryDec(root, "ask"),
                        OpenLevel: TryDec(root, "openLevel"),
                        LastUpdatedUtc: DateTimeOffset.UtcNow,
                        ValuePerPoint: TryDec(root, "valuePerPoint") ?? 1m,
                        ScalingFactor: TryDec(root, "scalingFactor") ?? 1m,
                        Currency: TryString(root, "currency") ?? "USD",
                        Broker: TryString(root, "broker"),
                        Account: TryString(root, "account")
                    )
                });
                return Results.Ok(new { snapshot = dealId });
            }

            // This is what fixes CS1643 - ensure every path returns a Result
            return Results.BadRequest("Unrecognized payload type");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[INGEST] error: {ex}");
        return Results.BadRequest("Invalid JSON or internal error");
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

static string? TryString(JsonElement el, string name)
{
    if (!el.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;

    return v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True   => bool.TrueString,
        JsonValueKind.False  => bool.FalseString,
        _ => v.GetRawText().Trim('"')
    };
}