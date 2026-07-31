using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// In-memory cache for chunk snapshots with proper LRU eviction.
/// Uses a LinkedList to track access order, ensuring that the least recently
/// used snapshot is evicted when the cache reaches capacity.
/// </summary>
public class ChunkCache
{
    private readonly Dictionary<Guid, ChunkSnapshot> _snapshots = new Dictionary<Guid, ChunkSnapshot>();

    // LinkedList tracks access order. Most recently accessed items are moved to the end.
    // When eviction is needed, we remove from the front (least recently used).
    private readonly LinkedList<Guid> _accessOrder = new LinkedList<Guid>();

    private readonly int _maxEntries;

    public ChunkCache(int maxEntries = 100)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Stores a chunk snapshot in the cache. If the cache is full, evicts the
    /// least recently used snapshot (not a random one).
    /// </summary>
    public void StoreSnapshot(Guid chunkId, LayoutData layout, ChunkDelta delta)
    {
        // If this chunk is already in the cache, update it and move to end of access order
        if (_snapshots.ContainsKey(chunkId))
        {
            _snapshots[chunkId] = new ChunkSnapshot(layout, delta);
            MoveToBack(chunkId);
            Debug.Log($"[ChunkCache] Updated snapshot for {chunkId}");
            return;
        }

        // Evict least recently used if at capacity
        if (_snapshots.Count >= _maxEntries)
        {
            EvictLeastRecentlyUsed();
        }

        _snapshots[chunkId] = new ChunkSnapshot(layout, delta);
        _accessOrder.AddLast(chunkId);
        Debug.Log($"[ChunkCache] Stored snapshot for {chunkId} (total: {_snapshots.Count}/{_maxEntries})");
    }

    /// <summary>
    /// Tries to retrieve a cached snapshot by chunk ID.
    /// If found, moves the snapshot to the end of the access order (marks as recently used).
    /// </summary>
    public bool TryGetSnapshot(Guid chunkId, out ChunkSnapshot snapshot)
    {
        bool found = _snapshots.TryGetValue(chunkId, out snapshot);
        if (found)
        {
            // Mark as recently used by moving to end of access order
            MoveToBack(chunkId);
            Debug.Log($"[ChunkCache] Found snapshot for {chunkId}");
        }
        else
        {
            Debug.LogWarning($"[ChunkCache] Snapshot NOT found for {chunkId}");
        }
        return found;
    }

    /// <summary>
    /// Updates the delta state of a cached chunk without regenerating layout.
    /// Moves the snapshot to the end of the access order.
    /// </summary>
    public void UpdateDelta(Guid chunkId, ChunkDelta newDelta)
    {
        if (_snapshots.TryGetValue(chunkId, out var existing))
        {
            _snapshots[chunkId] = new ChunkSnapshot(existing.BaseLayout, newDelta);
            MoveToBack(chunkId);
            Debug.Log($"[ChunkCache] Updated delta for {chunkId}");
        }
        else
        {
            Debug.LogWarning($"[ChunkCache] Cannot update delta for {chunkId}: snapshot not found");
        }
    }

    /// <summary>
    /// Removes a chunk from cache. Use when chunk is permanently unloaded.
    /// </summary>
    public void Remove(Guid chunkId)
    {
        if (_snapshots.Remove(chunkId))
        {
            _accessOrder.Remove(chunkId);
        }
    }

    /// <summary>
    /// Clears all cached snapshots.
    /// </summary>
    public void Clear()
    {
        _snapshots.Clear();
        _accessOrder.Clear();
    }

    public int Count => _snapshots.Count;

    /// <summary>
    /// Evicts the least recently used snapshot (front of the access order list).
    /// </summary>
    private void EvictLeastRecentlyUsed()
    {
        if (_accessOrder.Count == 0) return;

        Guid lruId = _accessOrder.First.Value;
        _accessOrder.RemoveFirst();
        _snapshots.Remove(lruId);
        Debug.Log($"[ChunkCache] Evicted LRU snapshot: {lruId}");
    }

    /// <summary>
    /// Moves a chunk ID to the back of the access order list (marks as recently used).
    /// </summary>
    private void MoveToBack(Guid chunkId)
    {
        _accessOrder.Remove(chunkId);
        _accessOrder.AddLast(chunkId);
    }
}