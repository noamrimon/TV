using System.Text.Json.Serialization;

namespace TVStreamer.Models;

public sealed class BrokerConfig
{
    public string Name { get; set; } = default!;
    public string Transport { get; set; } = "Http";
    public string BrokerTemplate { get; set; } = default!;
    public HttpSection? Http { get; set; }
    public WebSocketSection? WebSocket { get; set; }
    public Dictionary<string, string>? Vars { get; set; }
    public AuthSection? Auth { get; set; }
    public List<Operation>? Operations { get; set; }
    public string LogPrefix { get; set; } = "[default]"!;
    public BasePositionsConfig BasePositions { get; set; } = new();
    public sealed class HttpSection
    {
        public string BaseUrl { get; set; } = default!;
        public Dictionary<string,string>? DefaultHeaders { get; set; }
        public int TimeoutSeconds { get; set; } = 30;
    }
    public sealed class WebSocketSection
    {
        public string Url { get; set; } = default!;
        public Dictionary<string,string>? Headers { get; set; }
        public string? Protocol { get; set; }
    }
    public sealed class AuthSection
    {
        public List<AuthStep>? Steps { get; set; }
    }
    public sealed class AuthStep
    {
        public AuthRequest Request { get; set; } = default!;
        public ExtractSection? Extract { get; set; }
        public BindDefaultsSection? BindDefaults { get; set; }
    }
    public sealed class AuthRequest
    {
        public string Method { get; set; } = "POST";
        public string Path { get; set; } = default!;
        public Dictionary<string,string>? Headers { get; set; }
        public object? Body { get; set; }
    }
    public sealed class ExtractSection
    {
        public Dictionary<string,string>? Headers { get; set; }
        public Dictionary<string,string>? Json { get; set; }
    }
    public sealed class BindDefaultsSection
    {
        public Dictionary<string,string>? Headers { get; set; }
    }
    public sealed class Operation
    {
        public string Name { get; set; } = default!;
        public string Method { get; set; } = "GET";
        public string? Path { get; set; }
        public Dictionary<string,string>? Headers { get; set; }
        public Dictionary<string,string>? Query { get; set; }
        public object? Body { get; set; }
    }


    public class BasePositionsJsonPaths
    {
        public string Array { get; set; } = "";
        public string DealId { get; set; } = "";
        public string Epic { get; set; } = "";
        public string Amount { get; set; } = "";
        public string OpenLevel { get; set; } = "";
        public string Currency { get; set; } = "";
        public string Uic { get; set; } = "";
        public string Direction { get; set; } = "";
    }

    public class BasePositionsConfig
    {
        public string Method { get; set; } = "GET";
        public string Url { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new();
        public BasePositionsJsonPaths JsonPaths { get; set; } = new();
    }

}
