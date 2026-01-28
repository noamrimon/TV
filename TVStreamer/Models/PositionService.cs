using System.Collections.Immutable;

namespace TVStreamer.Models;

public interface IPositionService
{
    void UpdateBrokerPositions(string broker, List<BasePosition> positions);
    bool TryGetPosition(string broker, string dealId, out BasePosition bp);
    ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> GetAllData();
}

public class PositionService : IPositionService
{
    // The master index: BrokerName -> (DealId -> Position)
    private ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> _data =
        ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>>.Empty;

    /// <summary>
    /// Thread-safe update of positions for a specific broker.
    /// Does not wipe out data from other brokers.
    /// </summary>
    public void UpdateBrokerPositions(string broker, List<BasePosition> positions)
    {
        var newBrokerMap = positions.ToImmutableDictionary(
            x => x.DealId,
            x => x,
            StringComparer.OrdinalIgnoreCase);

        // Atomic swap using Immutable patterns
        ImmutableInterlocked.Update(ref _data, current => current.SetItem(broker, newBrokerMap));

        Console.WriteLine($"[POSITION-SERVICE] Updated {broker} with {positions.Count} positions.");

    }

    /// <summary>
    /// Retrieves a single position. Used by the WebSocket Streamer to find 'Size'.
    /// </summary>
    public bool TryGetPosition(string broker, string dealId, out BasePosition bp)
    {
        var snapshot = Volatile.Read(ref _data);
        if (snapshot.TryGetValue(broker, out var brokerMap) &&
            brokerMap.TryGetValue(dealId, out var found))
        {
            bp = found;
            return true;
        }
        bp = null!;
        return false;
    }

    /// <summary>
    /// Returns a snapshot of the current state for UI or Logging.
    /// </summary>
    public ImmutableDictionary<string, ImmutableDictionary<string, BasePosition>> GetAllData()
        => Volatile.Read(ref _data);
}