
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using System.Net;
using System.Text.Json;
using TVWeb.Models;
using TVWeb.Services;

var builder = WebApplication.CreateBuilder(args);

// ---------- Logging (diagnostics, optional) ----------
builder.Logging.AddFilter("Microsoft.AspNetCore.Components", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Debug);

// ---------- Services ----------
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(); // enable Blazor Server interactivity for components

builder.Services.AddSingleton<PositionsStore>();

var app = builder.Build();

// ---------- Middleware pipeline (order matters) ----------
app.UseExceptionHandler("/error");
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
// WebSockets: usually enabled implicitly; adding explicitly is harmless and can avoid edge cases
app.UseWebSockets();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// If you use Identity, keep these here:
// app.UseAuthentication();
// app.UseAuthorization();

app.UseAntiforgery(); // REQUIRED: interactive SSR routes carry antiforgery metadata

// ---------- Endpoints ----------

// Ingest endpoint (server-to-server) with antiforgery excluded
var ingestKey = app.Configuration["WebIngest:IngestKey"] ?? string.Empty;
var positionsStore = app.Services.GetRequiredService<PositionsStore>();

app.MapPost("/api/stream/positions", async (HttpContext http) =>
{
    if (!http.Request.Headers.TryGetValue("X-INGEST-KEY", out var keys) ||
        !keys.Any() || !CryptographicEquals(keys[0], ingestKey))
        return Results.StatusCode((int)HttpStatusCode.Forbidden);

    using var doc = await JsonDocument.ParseAsync(http.Request.Body);
    var root = doc.RootElement;

    if (root.ValueKind == JsonValueKind.Array)
    {
        var snapshots = new List<PositionSnapshot>();
        foreach (var el in root.EnumerateArray())
        {
            if (el.TryGetProperty("type", out var t) && t.GetString() == "snapshot")
            {
                snapshots.Add(new PositionSnapshot(
                    DealId: el.GetProperty("dealId").GetString()!,
                    Epic: el.GetProperty("epic").GetString()!,
                    Direction: el.GetProperty("direction").GetString()!,
                    Size: el.GetProperty("size").GetDecimal(),
                    Bid: el.TryGetProperty("bid", out var bid) && bid.ValueKind != JsonValueKind.Null ? bid.GetDecimal() : null,
                    Ask: el.TryGetProperty("ask", out var ask) && ask.ValueKind != JsonValueKind.Null ? ask.GetDecimal() : null,
                    OpenLevel: el.TryGetProperty("openLevel", out var lev) && lev.ValueKind != JsonValueKind.Null ? lev.GetDecimal() : (decimal?)null,
                    LastUpdatedUtc: DateTimeOffset.UtcNow
                ));
            }
        }

        if (snapshots.Count > 0)
            positionsStore.UpsertRange(snapshots);

        return Results.Ok();
    }
    else
    {
        var type = root.GetProperty("type").GetString();
        if (type == "tick")
        {
            var tick = new PriceTick(
                Epic: root.GetProperty("epic").GetString()!,
                DealId: root.GetProperty("dealId").GetString()!,
                Bid: root.TryGetProperty("bid", out var bid) && bid.ValueKind != JsonValueKind.Null ? bid.GetDecimal() : null,
                Ask: root.TryGetProperty("ask", out var ask) && ask.ValueKind != JsonValueKind.Null ? ask.GetDecimal() : null,
                TimestampUtc: DateTimeOffset.UtcNow);
                return Results.Ok();
         }
        else if (type == "closed")
        {
            var dealId = root.GetProperty("dealId").GetString()!;
            var epic = root.TryGetProperty("epic", out var e) ? e.GetString() : null;
            positionsStore.Remove(dealId, epic);   // implement Remove in the store
            return Results.Ok();
        }
    }
    return Results.BadRequest("Unsupported payload");
})
.WithMetadata(new IgnoreAntiforgeryTokenAttribute()); // exclude antiforgery only for ingest

// Snapshot read endpoint used by the page
app.MapGet("/api/positions", () =>
{
    return Results.Ok(positionsStore.GetAll());
});

// Map Razor components (EXACTLY ONE mapping) and configure Interactive Server render mode
app.MapRazorComponents<TVWeb.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

// ---------- helpers ----------
static bool CryptographicEquals(string? a, string? b)
{
    if (a is null || b is null) return false;
    ReadOnlySpan<byte> ba = System.Text.Encoding.UTF8.GetBytes(a);
    ReadOnlySpan<byte> bb = System.Text.Encoding.UTF8.GetBytes(b);
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ba, bb);
}
