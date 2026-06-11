using System.Collections.Generic;

/// <summary>
/// Minimal diff container for runtime chunk state changes.
/// Used for caching and persistence without regenerating geometry.
/// Stored separately from LayoutData to allow independent serialization.
/// </summary>
public class ChunkDelta
{
    /// <summary>
    /// IDs of loot items collected by the player.
    /// </summary>
    public HashSet<string> CollectedLootIds { get; } = new HashSet<string>();

    /// <summary>
    /// IDs of obstacles destroyed or deactivated.
    /// </summary>
    public HashSet<string> DestroyedObstacleIds { get; } = new HashSet<string>();

    /// <summary>
    /// State of activated triggers or switches.
    /// </summary>
    public Dictionary<string, bool> ActivatedTriggers { get; } = new Dictionary<string, bool>();

    /// <summary>
    /// Creates a new empty delta.
    /// </summary>
    public ChunkDelta() { }

    /// <summary>
    /// Creates a delta with pre-populated collected loot IDs.
    /// </summary>
    public ChunkDelta(IEnumerable<string> collectedLootIds)
    {
        if (collectedLootIds != null)
        {
            foreach (var id in collectedLootIds)
            {
                CollectedLootIds.Add(id);
            }
        }
    }
}