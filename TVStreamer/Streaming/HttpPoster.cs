using System.Text;
using System.Text.Json;

namespace TVStreamer.Streaming;

public static class HttpPoster
{
    public static async Task PostJsonAsync(string url, string ingestKey, object payload)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-INGEST-KEY", ingestKey);
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();
    }
}