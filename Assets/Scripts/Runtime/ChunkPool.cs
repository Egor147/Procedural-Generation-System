using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Object pool for Chunk MonoBehaviour instances.
/// Prevents expensive Instantiate/Destroy calls during runtime streaming.
/// Reuses deactivated chunks by resetting their state and repositioning.
/// </summary>
public class ChunkPool
{
    private readonly Queue<Chunk> _pool = new Queue<Chunk>();
    private readonly Chunk _chunkPrefab;
    private readonly Transform _poolParent;
    private readonly int _maxCapacity;
    private int _activeCount = 0;

    /// <summary>
    /// Creates a new chunk pool with specified capacity.
    /// </summary>
    public ChunkPool(Chunk chunkPrefab, Transform parent, int maxCapacity)
    {
        _chunkPrefab = chunkPrefab;
        _poolParent = parent ?? new GameObject("ChunkPoolRoot").transform;
        _maxCapacity = maxCapacity;
    }

    /// <summary>
    /// Gets a chunk from the pool or instantiates a new one.
    /// </summary>
    public Chunk Get(Vector3 position, GenerationProfileSO config)
    {
        Chunk chunk;

        if (_pool.Count > 0)
        {
            chunk = _pool.Dequeue();
            chunk.Reactivate(position);
        }
        else if (_activeCount < _maxCapacity)
        {
            chunk = Object.Instantiate(_chunkPrefab, _poolParent);
            chunk.transform.position = position;
            _activeCount++;
        }
        else
        {
            return null;
        }

        
        chunk.gameObject.SetActive(true);

        return chunk;
    }

    /// <summary>
    /// Returns a chunk to the pool for future reuse.
    /// </summary>
    public void Return(Chunk chunk)
    {
        if (chunk == null) return;

        // Capture delta state before deactivation
        var delta = chunk.CaptureDelta();

        chunk.Deactivate();
        chunk.Clear();

        // Store delta in a side dictionary if caching is enabled
        // In full implementation: ChunkCache.Store(chunk.LayoutData.ChunkId, delta);

        if (_pool.Count < _maxCapacity)
        {
            _pool.Enqueue(chunk);
        }
        else
        {
            // Pool full - destroy the chunk
            Object.Destroy(chunk.gameObject);
            _activeCount--;
        }
    }

    /// <summary>
    /// Clears all pooled chunks. Use when unloading a level.
    /// </summary>
    public void Clear()
    {
        while (_pool.Count > 0)
        {
            var chunk = _pool.Dequeue();
            if (chunk != null) Object.Destroy(chunk.gameObject);
        }
        _activeCount = 0;
    }

    /// <summary>
    /// Current number of active (non-pooled) chunks.
    /// </summary>
    public int ActiveCount => _activeCount;

    /// <summary>
    /// Current number of chunks available in the pool.
    /// </summary>
    public int PooledCount => _pool.Count;
}