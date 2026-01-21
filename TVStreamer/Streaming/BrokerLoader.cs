using System.Text.Json;
using TVStreamer.Models;

namespace TVStreamer.Streaming;

public static class BrokerLoader
{
    static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static IEnumerable<string> EnumerateBrokerFiles(string baseDir)
        => Directory.EnumerateFiles(Path.Combine(baseDir, "Brokers"), "*.json", SearchOption.TopDirectoryOnly);

    public static BrokerConfig LoadConfig(string filePath)
        => JsonSerializer.Deserialize<BrokerConfig>(File.ReadAllText(filePath), JsonOpts)!;
}
