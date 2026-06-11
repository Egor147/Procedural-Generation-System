using System;
using System.Collections.Generic;

/// <summary>
/// In-memory cache for chunk snapshots (LayoutData + ChunkDelta).
/// Enables fast restoration of previously generated chunks without regeneration.
/// Can be extended with disk serialization for persistent worlds.
/// </summary>
public class ChunkCache
{
    private readonly Dictionary<Guid, ChunkSnapshot> _snapshots = new Dictionary<Guid, ChunkSnapshot>();
    private readonly int _maxEntries;

    /// <summary>
    /// Creates a new cache with optional size limit.
    /// </summary>
    public ChunkCache(int maxEntries = 100)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Stores a chunk snapshot in the cache.
    /// </summary>
    public void StoreSnapshot(Guid chunkId, LayoutData layout, ChunkDelta delta)
    {
        if (_snapshots.Count >= _maxEntries && !_snapshots.ContainsKey(chunkId))
        {
            var enumerator = _snapshots.GetEnumerator();
            if (enumerator.MoveNext())
            {
                _snapshots.Remove(enumerator.Current.Key);
            }
        }

        _snapshots[chunkId] = new ChunkSnapshot(layout, delta);
        UnityEngine.Debug.Log($"[ChunkCache] Stored snapshot for {chunkId}");
    }

    /// <summary>
    /// Tries to retrieve a cached snapshot by chunk ID.
    /// </summary>
    public bool TryGetSnapshot(Guid chunkId, out ChunkSnapshot snapshot)
    {
        bool found = _snapshots.TryGetValue(chunkId, out snapshot);
        if (found)
        {
            UnityEngine.Debug.Log($"[ChunkCache] Found snapshot for {chunkId}");
        }
        else
        {
            UnityEngine.Debug.LogWarning($"[ChunkCache] Snapshot NOT found for {chunkId}");
        }
        return found;
    }

    /// <summary>
    /// Updates the delta state of a cached chunk without regenerating layout.
    /// </summary>
    public void UpdateDelta(Guid chunkId, ChunkDelta newDelta)
    {
        if (_snapshots.TryGetValue(chunkId, out var existing))
        {
            _snapshots[chunkId] = new ChunkSnapshot(existing.BaseLayout, newDelta);
            UnityEngine.Debug.Log($"[ChunkCache] Updated delta for {chunkId}");
        }
    }

    /// <summary>
    /// Removes a chunk from cache. Use when chunk is permanently unloaded.
    /// </summary>
    public void Remove(Guid chunkId)
    {
        _snapshots.Remove(chunkId);
    }

    /// <summary>
    /// Clears all cached snapshots.
    /// </summary>
    public void Clear()
    {
        _snapshots.Clear();
    }

    /// <summary>
    /// Current number of cached chunks.
    /// </summary>
    public int Count => _snapshots.Count;
}

/// <summary>
/// Immutable container for a chunk's generated layout and runtime state.
/// </summary>
public readonly struct ChunkSnapshot
{
    public LayoutData BaseLayout { get; }
    public ChunkDelta RuntimeState { get; }

    public ChunkSnapshot(LayoutData baseLayout, ChunkDelta runtimeState)
    {
        BaseLayout = baseLayout;
        RuntimeState = runtimeState;
    }
}