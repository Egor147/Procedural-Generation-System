using System;

/// <summary>
/// Immutable container for a chunk's generated layout and runtime state.
/// Stored in ChunkCache to enable fast restoration without regeneration.
/// </summary>
public readonly struct ChunkSnapshot
{
    /// <summary>
    /// The original generated layout (grid, biome, world origin, etc.).
    /// </summary>
    public LayoutData BaseLayout { get; }

    /// <summary>
    /// Runtime state changes (collected loot, destroyed obstacles, etc.).
    /// </summary>
    public ChunkDelta RuntimeState { get; }

    public ChunkSnapshot(LayoutData baseLayout, ChunkDelta runtimeState)
    {
        BaseLayout = baseLayout;
        RuntimeState = runtimeState;
    }
}