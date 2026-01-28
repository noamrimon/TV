
using TVStreamer.Streaming;

namespace TVStreamer.Models
{
    public sealed class BrokerRuntime
    {
        public BrokerConfig Config { get; init; } = default!;
        public AuthSession Session { get; init; } = default!;
        public IStreamer? Streamer { get; set; }
    }
}
