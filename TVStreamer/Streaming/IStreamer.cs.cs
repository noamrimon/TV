namespace TVStreamer.Streaming;

public interface IStreamer : IDisposable
{
    Task StartAsync();
}