
using System.Net.Http;
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

        Console.WriteLine($"[POST] {url}  body-len={json.Length}");

        using var resp = await client.PostAsync(url, content);
        var respText = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"[POST] -> {(int)resp.StatusCode} {resp.StatusCode}");

        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"POST {url} failed with {(int)resp.StatusCode} {resp.StatusCode}. Body: {respText}");
        }
    }
}
