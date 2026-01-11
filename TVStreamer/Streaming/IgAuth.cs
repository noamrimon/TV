using System.Text;
using System.Text.Json;

namespace TVStreamer.Streaming;

public static class IgAuth
{
    public sealed record AuthResult(
        string AccountId,
        string ClientId,
        string Cst,
        string Xst,
        string LightstreamerEndpoint
    );

    public static async Task<AuthResult> LoginAsync(HttpClient http, string baseUrl, string apiKey, string username, string password)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/session");
        req.Headers.Add("X-IG-API-KEY", apiKey);
        req.Headers.Add("Accept", "application/json; charset=UTF-8");
        req.Headers.Add("Version", "2");
        req.Content = new StringContent(JsonSerializer.Serialize(new { identifier = username, password }), Encoding.UTF8, "application/json");

        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var cst = resp.Headers.TryGetValues("CST", out var cstVals) ? cstVals.FirstOrDefault()! : throw new InvalidOperationException("CST missing");
        var xst = resp.Headers.TryGetValues("X-SECURITY-TOKEN", out var xstVals) ? xstVals.FirstOrDefault()! : throw new InvalidOperationException("XST missing");
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var accId = json.RootElement.GetProperty("currentAccountId").GetString()!;
        var clientId = json.RootElement.TryGetProperty("clientId", out var cidEl) ? cidEl.GetString() ?? "" : "";
        var lsEndpoint = json.RootElement.GetProperty("lightstreamerEndpoint").GetString()!;

        return new AuthResult(accId, clientId, cst, xst, lsEndpoint);
    }
}