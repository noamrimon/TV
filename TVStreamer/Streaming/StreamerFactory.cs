using System.Text.Json;
using System.Text.Json.Nodes;
using TVStreamer.Models;

namespace TVStreamer.Streaming;

public static class StreamerFactory
{
    public static IStreamer Create(
        BrokerConfig cfg,
        AuthSession session,
        JsonNode streamingNode,
        IPositionService positionService,
        string ingestUrl,
        string ingestKey)
    {
        var provider = streamingNode["Provider"]?.ToString() ?? "WebSocket";

        if (provider.Equals("Lightstreamer", StringComparison.OrdinalIgnoreCase))
        {
            session.Vars.TryGetValue("LightStreamerEndpoint", out var endpoint);
            session.Vars.TryGetValue("User", out var user);
            session.Vars.TryGetValue("AccountId", out var accId);

            // Fetch the tokens extracted by your Auth code
            session.Vars.TryGetValue("CST", out var cst);
            session.Vars.TryGetValue("XST", out var xst);

            // IG Requirement: The password must be "CST-xxx|XST-yyy"
            var lsPassword = $"CST-{cst}|XST-{xst}";

            return new LightstreamerStreamer(
                endpoint ?? "",
                user ?? "",
                lsPassword,
                accId ?? "",
                ingestUrl,
                ingestKey,
                cfg.Name
            );
        }

        // Standard Generic WebSocket Streamer (Saxo)
        return new GenericWebSocketStreamer(
            session.Http,
            session.BaseUrl,
            session.Vars,
            JsonDocument.Parse(streamingNode.ToJsonString()).RootElement,
            ingestUrl,
            ingestKey,
            positionService,
            cfg.Name);
    }
}