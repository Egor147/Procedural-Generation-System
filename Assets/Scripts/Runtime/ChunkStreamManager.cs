using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Manages the lifecycle of procedural chunks with support for two streaming strategies:
/// 
/// 1. AnchorBased (Optimized): Chunks load only when the player approaches biome-defined
///    connection anchors. This reduces simultaneous loads and makes caching more effective
///    since players follow predictable paths through the world.
/// 
/// 2. DistanceBased (Standard): Chunks load based on a fixed radius around the player,
///    similar to Minecraft. Simpler but causes more frequent load/unload cycles and
///    less effective cache utilization.
/// 
/// The streaming mode is configured via GenerationProfileSO.StreamingMode and can be
/// switched at runtime for comparison studies.
/// </summary>
public class ChunkStreamManager
{
    private readonly GenerationProfileSO _config;
    private readonly GenerationPipeline _pipeline;
    private readonly ChunkPool _chunkPool;
    private readonly ChunkCache _cache;

    /// <summary>
    /// Maps grid coordinates to currently active Chunk instances in the scene.
    /// </summary>
    private readonly Dictionary<Vector2Int, Chunk> _activeChunks = new Dictionary<Vector2Int, Chunk>();

    /// <summary>
    /// Tracks coordinates that are currently being generated asynchronously.
    /// Prevents duplicate generation requests for the same coordinate.
    /// </summary>
    private readonly HashSet<Vector2Int> _pendingCoords = new HashSet<Vector2Int>();

    /// <summary>
    /// Tracks all coordinates that have EVER been generated or requested.
    /// Used for deterministic regeneration via anchors only.
    /// </summary>
    private readonly HashSet<Vector2Int> _generatedCoords = new HashSet<Vector2Int>();

    /// <summary>
    /// Maps Coordinates to their Deterministic Chunk IDs for caching.
    /// This ensures the same coordinate always produces the same chunk,
    /// which is essential for cache hits when the player returns.
    /// </summary>
    private readonly Dictionary<Vector2Int, Guid> _coordToChunkId = new Dictionary<Vector2Int, Guid>();

    /// <summary>
    /// Tracks runtime state per coordinate to prevent thrashing and premature unloading.
    /// </summary>
    private readonly Dictionary<Vector2Int, ChunkRuntimeState> _chunkStates = new Dictionary<Vector2Int, ChunkRuntimeState>();

    /// <summary>
    /// Prevents infinite regeneration loops by enforcing a cooldown on failed requests.
    /// </summary>
    private readonly Dictionary<Vector2Int, float> _requestCooldowns = new Dictionary<Vector2Int, float>();

    // Events for performance monitoring and debugging
    public event System.Action<Vector2Int> OnChunkLoaded;
    public event System.Action<Vector2Int> OnChunkUnloaded;

    private Vector3 _lastPlayerPosition;
    private readonly float _chunkWidth;
    private readonly float _chunkDepth;
    private readonly float _halfChunkWidth;
    private readonly float _halfChunkDepth;

    // Distance-based streaming parameters
    private readonly int _distanceBasedRadius;
    private readonly int _distanceBasedUnloadRadius;

    // Anchor-based streaming parameters
    private readonly int _maxActiveChunks;
    private readonly float _anchorTriggerDistanceSq;
    private readonly float _unloadDistanceSq;
    private readonly float _loadDistanceSq;

    // Stability settings
    private readonly int _minLifetimeFrames = 10;
    private readonly float _requestCooldownSeconds = 1.0f;
    private int _spawnsThisFrame = 0;
    private const int MaxSpawnsPerFrame = 1;

    /// <summary>
    /// Initializes the stream manager with dependencies and calculates spatial thresholds.
    /// The initialization logic branches based on the configured StreamingMode.
    /// </summary>
    public ChunkStreamManager(
        GenerationProfileSO config,
        GenerationPipeline pipeline,
        Chunk chunkPrefab,
        ChunkCache cache)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        _chunkWidth = config.GridDimensions.x * config.CellSize;
        _chunkDepth = config.GridDimensions.y * config.CellSize;
        _halfChunkWidth = _chunkWidth * 0.5f;
        _halfChunkDepth = _chunkDepth * 0.5f;

        // Initialize parameters based on streaming mode
        if (config.StreamingMode == StreamingMode.DistanceBased)
        {
            // Distance-based: need enough slots for (2*radius+1)^2 chunks in worst case
            int r = config.DistanceBasedRadius;
            _maxActiveChunks = (2 * r + 1) * (2 * r + 1) + 2; // +2 for buffer
            _distanceBasedRadius = config.DistanceBasedRadius;
            _distanceBasedUnloadRadius = config.DistanceBasedRadius + config.DistanceBasedUnloadBuffer;

            // Anchor-based parameters (unused but initialized for safety)
            _anchorTriggerDistanceSq = 0;
            _unloadDistanceSq = 0;
            _loadDistanceSq = 0;

            Debug.Log($"[ChunkStreamManager] DistanceBased mode: Radius={_distanceBasedRadius}, " +
                      $"UnloadRadius={_distanceBasedUnloadRadius}, MaxChunks={_maxActiveChunks}");
        }
        else
        {
            // Anchor-based: strict limit on active chunks
            _maxActiveChunks = 5;
            _distanceBasedRadius = 0;
            _distanceBasedUnloadRadius = 0;

            // Anchor trigger: player must be very close to anchor to trigger neighbor
            _anchorTriggerDistanceSq = (_config.CellSize * 5f);
            _anchorTriggerDistanceSq *= _anchorTriggerDistanceSq;

            // Unload distance: slightly more than one chunk away from player
            float unloadDist = Mathf.Max(_chunkWidth, _chunkDepth) * 1.2f;
            _unloadDistanceSq = unloadDist * unloadDist;

            // Load distance for regeneration: slightly less than unload to create hysteresis
            float loadDist = Mathf.Max(_chunkWidth, _chunkDepth) * 0.9f;
            _loadDistanceSq = loadDist * loadDist;

            Debug.Log($"[ChunkStreamManager] AnchorBased mode: MaxActive={_maxActiveChunks}, " +
                      $"Load={loadDist:F1}, Unload={unloadDist:F1}");
        }

        // Pool capacity matches the maximum active chunks for this mode
        _chunkPool = new ChunkPool(chunkPrefab, null, _maxActiveChunks + 2);
    }

    /// <summary>
    /// Public accessor for the number of currently active chunks.
    /// Used by PerformanceMonitor for telemetry.
    /// </summary>
    public int ActiveChunkCount => _activeChunks.Count;

    /// <summary>
    /// Main tick method. Must be called EXACTLY ONCE per frame from MonoBehaviour.Update().
    /// Dispatches to the appropriate streaming strategy based on the configured mode.
    /// </summary>
    public void UpdatePlayerPosition(Vector3 playerPosition)
    {
        _lastPlayerPosition = playerPosition;
        _spawnsThisFrame = 0;

        // 1. Clean up expired cooldowns
        CleanupCooldowns();

        // 2. Strategy dispatch: different logic per streaming mode
        if (_config.StreamingMode == StreamingMode.DistanceBased)
        {
            // Distance-based: load/unload based purely on player distance
            EvaluateUnloadingDistanceBased();
            EvaluateLoadingDistanceBased(playerPosition);
        }
        else
        {
            // Anchor-based: load via anchors, unload based on distance
            EnforceActiveChunkLimit();
            EvaluateUnloading();
            EvaluateLoading(playerPosition);
        }
    }

    // ========================================================================
    // DISTANCE-BASED STREAMING (Standard/Naive approach)
    // ========================================================================

    /// <summary>
    /// Computes the chunk coordinate that contains the given world position.
    /// Uses floor division so that negative coordinates work correctly
    /// (player at x=-0.1 should be in chunk -1, not chunk 0).
    /// </summary>
    private Vector2Int WorldToChunkCoord(Vector3 worldPos)
    {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / _chunkWidth),
            Mathf.FloorToInt(worldPos.z / _chunkDepth));
    }

    /// <summary>
    /// Unloads chunks that are outside the (Radius + Buffer) range from the player.
    /// The buffer creates hysteresis so chunks don't flicker on/off at the boundary.
    /// For example, with Radius=2 and Buffer=1, chunks load at distance 2 but
    /// only unload at distance 3, preventing constant load/unload cycles.
    /// </summary>
    private void EvaluateUnloadingDistanceBased()
    {
        Vector2Int playerChunk = WorldToChunkCoord(_lastPlayerPosition);
        var toUnload = new List<Vector2Int>();

        foreach (var kvp in _activeChunks)
        {
            var coord = kvp.Key;

            // Manhattan distance in chunk coordinates
            int dist = Mathf.Abs(coord.x - playerChunk.x) + Mathf.Abs(coord.y - playerChunk.y);

            if (dist > _distanceBasedUnloadRadius)
            {
                toUnload.Add(coord);
            }
        }

        foreach (var coord in toUnload)
        {
            UnloadChunk(coord);
        }
    }

    /// <summary>
    /// Loads all chunks within the configured radius around the player's chunk.
    /// Uses a spiral pattern (closest first) so the player sees nearby chunks
    /// populate before distant ones. This creates a better user experience
    /// than random order loading.
    /// </summary>
    private void EvaluateLoadingDistanceBased(Vector3 playerPos)
    {
        Vector2Int playerChunk = WorldToChunkCoord(playerPos);

        // Generate coordinates in spiral order (Manhattan distance rings)
        // This ensures we load the closest chunks first when under spawn budget
        var coordsInRadius = new List<Vector2Int>();
        for (int ring = 0; ring <= _distanceBasedRadius; ring++)
        {
            if (ring == 0)
            {
                coordsInRadius.Add(playerChunk);
            }
            else
            {
                // Walk the perimeter of the Manhattan-distance ring
                for (int dx = -ring; dx <= ring; dx++)
                {
                    int dz1 = ring - Mathf.Abs(dx);
                    int dz2 = -dz1;
                    coordsInRadius.Add(playerChunk + new Vector2Int(dx, dz1));
                    if (dz1 != dz2)
                    {
                        coordsInRadius.Add(playerChunk + new Vector2Int(dx, dz2));
                    }
                }
            }
        }

        // Try to load each coordinate, respecting per-frame spawn budget
        foreach (var coord in coordsInRadius)
        {
            if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
            if (_activeChunks.Count >= _maxActiveChunks) break;

            if (_activeChunks.ContainsKey(coord)) continue;
            if (_pendingCoords.Contains(coord)) continue;
            if (_requestCooldowns.ContainsKey(coord)) continue;

            // Try cache first, otherwise generate fresh
            Guid chunkId = GenerateChunkId(coord);
            Vector3 worldOrigin = CoordToWorldOrigin(coord);

            if (_config.EnableChunkCache && _cache.TryGetSnapshot(chunkId, out var snapshot))
            {
                var request = new GenerationRequest(
                    chunkId,
                    snapshot.BaseLayout.BiomeDefinition,
                    worldOrigin,
                    _config.RandomSeed != 0 ? _config.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue));

                RequestChunk(coord, request, playerPos);
                _spawnsThisFrame++;
            }
            else
            {
                // Cache miss - generate from scratch. This is the expensive path
                // that the optimized approach avoids on subsequent visits.
                var biome = GetBiomeForCoordinate(coord);
                if (biome != null)
                {
                    int seed = _config.RandomSeed != 0 ? _config.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue);
                    var request = new GenerationRequest(chunkId, biome, worldOrigin, seed);
                    RequestChunk(coord, request, playerPos);
                    _spawnsThisFrame++;
                }
            }
        }
    }

    // ========================================================================
    // ANCHOR-BASED STREAMING (Optimized approach)
    // ========================================================================

    /// <summary>
    /// Enforces hard limit on active chunks for anchor-based mode.
    /// If limit exceeded, unloads farthest chunks until within budget.
    /// This is a safety net - in theory, anchor-based loading should
    /// never exceed the limit, but this prevents bugs from causing
    /// unbounded memory growth.
    /// </summary>
    private void EnforceActiveChunkLimit()
    {
        while (_activeChunks.Count > _maxActiveChunks)
        {
            // Find and unload the farthest chunk from player
            Vector2Int? farthestCoord = null;
            float maxDistSq = -1f;

            foreach (var kvp in _activeChunks)
            {
                float distSq = Vector3.SqrMagnitude(_lastPlayerPosition - kvp.Value.transform.position);
                if (distSq > maxDistSq)
                {
                    maxDistSq = distSq;
                    farthestCoord = kvp.Key;
                }
            }

            if (farthestCoord.HasValue)
            {
                UnloadChunk(farthestCoord.Value);
            }
            else
            {
                break; // Safety fallback
            }
        }
    }

    /// <summary>
    /// Unloads chunks that exceed the unload distance threshold.
    /// Uses hysteresis (load distance < unload distance) to prevent
    /// chunks from flickering on/off when the player is near the boundary.
    /// </summary>
    private void EvaluateUnloading()
    {
        var toUnload = new List<Vector2Int>();
        int currentFrame = Time.frameCount;

        foreach (var kvp in _activeChunks)
        {
            var coord = kvp.Key;
            var chunk = kvp.Value;

            if (!_chunkStates.TryGetValue(coord, out var state)) continue;
            if (currentFrame - state.SpawnFrame < _minLifetimeFrames) continue;

            float distSq = Vector3.SqrMagnitude(_lastPlayerPosition - chunk.transform.position);
            if (distSq > _unloadDistanceSq)
                toUnload.Add(coord);
        }

        foreach (var coord in toUnload)
        {
            UnloadChunk(coord);
        }
    }

    /// <summary>
    /// Evaluates player position to trigger chunk loading.
    /// Prioritizes anchor-based discovery, then regeneration of known chunks.
    /// This two-phase approach ensures that new chunks are discovered through
    /// biome anchors first, and only then do we try to restore old chunks
    /// from cache when the player backtracks.
    /// </summary>
    private void EvaluateLoading(Vector3 playerPos)
    {
        // 1. Try to discover NEW neighbors via anchors (higher priority)
        if (_spawnsThisFrame < MaxSpawnsPerFrame && _activeChunks.Count < _maxActiveChunks)
        {
            TryDiscoverNeighborsFromAllChunks(playerPos);
        }

        // 2. Try to regenerate KNOWN chunks from cache (lower priority)
        // Only if we still have budget and haven't spawned this frame
        if (_spawnsThisFrame < MaxSpawnsPerFrame && _activeChunks.Count < _maxActiveChunks)
        {
            TryRegenerateKnownChunks(playerPos);
        }
    }

    /// <summary>
    /// Scans anchors on active chunks to discover NEW neighbors.
    /// Only triggers if under MaxActiveChunks limit.
    /// This is the core of the anchor-based approach: chunks only load
    /// when the player approaches a specific connection point, which
    /// dramatically reduces the number of simultaneous loads compared
    /// to distance-based loading.
    /// </summary>
    private void TryDiscoverNeighborsFromAllChunks(Vector3 playerPos)
    {
        // Create snapshot to avoid collection modification during iteration
        var activeChunksSnapshot = new List<Chunk>(_activeChunks.Values);

        foreach (var chunk in activeChunksSnapshot)
        {
            if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
            if (_activeChunks.Count >= _maxActiveChunks) break;

            var biomeDef = chunk.layoutData.BiomeDefinition;
            if (biomeDef?.ConnectionAnchors == null) continue;

            foreach (var anchor in biomeDef.ConnectionAnchors)
            {
                if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
                if (_activeChunks.Count >= _maxActiveChunks) break;

                // Calculate anchor position in world space
                Vector3 anchorLocalPos = new Vector3(
                    (anchor.GridPosition.x + 0.5f) * _config.CellSize - _halfChunkWidth,
                    0f,
                    (anchor.GridPosition.y + 0.5f) * _config.CellSize - _halfChunkDepth);

                Vector3 anchorWorldPos = chunk.transform.position + anchorLocalPos;
                float distToAnchorSq = Vector3.SqrMagnitude(playerPos - anchorWorldPos);

                // Only trigger if player is very close to anchor
                if (distToAnchorSq <= _anchorTriggerDistanceSq)
                {
                    Vector2Int neighborCoord = chunk.GridCoordinate + GetDirectionOffset(anchor.ConnectionDirection);

                    // Only spawn if this chunk has never been seen before (NEW world)
                    if (!_activeChunks.ContainsKey(neighborCoord) &&
                        !_pendingCoords.Contains(neighborCoord) &&
                        !_generatedCoords.Contains(neighborCoord))
                    {
                        Vector3 nextOrigin = CalculateNeighborOrigin(chunk.transform.position, anchor.ConnectionDirection);
                        int seed = _config.RandomSeed != 0 ? _config.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue);

                        Guid chunkId = GenerateChunkId(neighborCoord);

                        var request = new GenerationRequest(
                            chunkId,
                            biomeDef,
                            nextOrigin,
                            seed);

                        Debug.Log($"[ChunkStreamManager] ANCHOR TRIGGERED: NEW neighbor {neighborCoord} via {anchor.ConnectionDirection}");
                        RequestChunk(neighborCoord, request, playerPos);
                        _spawnsThisFrame++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Restores KNOWN chunks from cache when player returns to their area.
    /// Only triggers if under MaxActiveChunks limit and chunk is within load distance.
    /// This handles backtracking: when the player goes back to a previously
    /// visited area, we restore from cache instead of regenerating from scratch.
    /// </summary>
    private void TryRegenerateKnownChunks(Vector3 playerPos)
    {
        // Iterate through all known coordinates
        foreach (var coord in _generatedCoords)
        {
            if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
            if (_activeChunks.Count >= _maxActiveChunks) break;

            // Skip if already active or currently generating
            if (_activeChunks.ContainsKey(coord)) continue;
            if (_pendingCoords.Contains(coord)) continue;
            if (_requestCooldowns.ContainsKey(coord)) continue;

            // Check if player is close enough to this known chunk
            Vector3 chunkCenter = CoordToWorldCenter(coord);
            float distSq = Vector3.SqrMagnitude(playerPos - chunkCenter);

            if (distSq <= _loadDistanceSq)
            {
                // Try to restore from cache
                Guid chunkId = GenerateChunkId(coord);
                if (_config.EnableChunkCache && _cache.TryGetSnapshot(chunkId, out var snapshot))
                {
                    Debug.Log($"[ChunkStreamManager] REGENERATING known chunk {coord} from cache (dist: {Mathf.Sqrt(distSq):F1})");

                    var request = new GenerationRequest(
                        chunkId,
                        snapshot.BaseLayout.BiomeDefinition,
                        snapshot.BaseLayout.WorldOrigin,
                        _config.RandomSeed != 0 ? _config.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue));

                    RequestChunk(coord, request, playerPos);
                    _spawnsThisFrame++;
                }
                else
                {
                    // FALLBACK: Snapshot not found in cache - regenerate via pipeline
                    // This can happen if LRU eviction removed the snapshot, or if
                    // the chunk was unloaded before async generation completed.
                    Debug.LogWarning($"[ChunkStreamManager] Cache miss for {coord}, regenerating via pipeline");

                    if (_coordToChunkId.TryGetValue(coord, out var storedChunkId))
                    {
                        var biome = GetBiomeForCoordinate(coord);
                        if (biome != null)
                        {
                            Vector3 worldOrigin = CoordToWorldOrigin(coord);
                            int seed = _config.RandomSeed != 0 ? _config.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue);

                            var request = new GenerationRequest(
                                storedChunkId,
                                biome,
                                worldOrigin,
                                seed);

                            RequestChunk(coord, request, playerPos);
                            _spawnsThisFrame++;
                        }
                    }
                }
            }
        }
    }

    // ========================================================================
    // COMMON METHODS (used by both streaming modes)
    // ========================================================================

    /// <summary>
    /// Public API: Requests a chunk from the pipeline or cache.
    /// Respects MaxActiveChunks limit - may trigger immediate unload of distant chunk.
    /// This is the entry point for all chunk loading, regardless of strategy.
    /// </summary>
    public void RequestChunk(Vector2Int coord, GenerationRequest request, Vector3 playerPosition)
    {
        // Safety checks
        if (_activeChunks.ContainsKey(coord)) return;
        if (_pendingCoords.Contains(coord)) return;

        // Check cooldown to prevent infinite loops on persistent failures
        if (_requestCooldowns.TryGetValue(coord, out var cooldown) && Time.time < cooldown) return;

        _pendingCoords.Add(coord);
        _generatedCoords.Add(coord);
        _coordToChunkId[coord] = request.ChunkId;
        _chunkStates[coord] = new ChunkRuntimeState { SpawnFrame = Time.frameCount };

        // Try cache first (synchronous)
        if (_config.EnableChunkCache && _cache.TryGetSnapshot(request.ChunkId, out var snapshot))
        {
            _pendingCoords.Remove(coord);
            if (InstantiateFromSnapshot(snapshot, request.WorldOrigin, coord, request.ChunkId))
            {
                _requestCooldowns.Remove(coord);
                if (_config.StreamingMode == StreamingMode.AnchorBased)
                {
                    EnforceActiveChunkLimit();
                }
            }
            else
            {
                SetCooldown(coord);
            }
            return;
        }

        // Async generation
        _pipeline.ScheduleGeneration(request, result =>
        {
            _pendingCoords.Remove(coord);

            if (result.ChunkId != Guid.Empty)
            {
                if (InstantiateFromLayout(result, request.WorldOrigin, coord, result.ChunkId))
                {
                    // Save snapshot AFTER successful instantiation to ensure consistency
                    if (_config.EnableChunkCache)
                    {
                        _cache.StoreSnapshot(result.ChunkId, result, new ChunkDelta());
                    }

                    _requestCooldowns.Remove(coord);
                    if (_config.StreamingMode == StreamingMode.AnchorBased)
                    {
                        EnforceActiveChunkLimit();
                    }
                }
                else
                {
                    SetCooldown(coord);
                }
            }
            else
            {
                _generatedCoords.Remove(coord);
                _coordToChunkId.Remove(coord);
                _chunkStates.Remove(coord);
                SetCooldown(coord);
                Debug.LogError($"[ChunkStreamManager] Pipeline returned empty result for {coord}");
            }
        });
    }

    private void SetCooldown(Vector2Int coord)
    {
        _requestCooldowns[coord] = Time.time + _requestCooldownSeconds;
    }

    /// <summary>
    /// Unloads a specific chunk and cleans up tracking data.
    /// Always saves the full snapshot (not just delta) to ensure the chunk
    /// can be restored when the player returns. This is critical for cache
    /// correctness - if we only update delta and the snapshot was evicted
    /// by LRU, we lose the chunk data entirely.
    /// </summary>
    private void UnloadChunk(Vector2Int coord)
    {
        if (_activeChunks.Remove(coord, out var chunk))
        {
            if (_config.EnableChunkCache && _coordToChunkId.TryGetValue(coord, out var chunkId))
            {
                var delta = chunk.CaptureDelta();

                // CRITICAL: Always store the full snapshot, not just update delta.
                // The snapshot might not exist in cache if:
                // 1. It was evicted by LRU before the chunk was unloaded
                // 2. The chunk was unloaded before async generation completed
                // 3. This is the first time the chunk is being unloaded
                // 
                // By always calling StoreSnapshot (which handles both create and update),
                // we ensure the chunk can be restored when the player returns.
                _cache.StoreSnapshot(chunkId, chunk.layoutData, delta);
            }

            // Notify subscribers before returning to pool
            OnChunkUnloaded?.Invoke(coord);

            _chunkPool.Return(chunk);
            _chunkStates.Remove(coord);
        }
    }

    private void CleanupCooldowns()
    {
        var expired = new List<Vector2Int>();
        float now = Time.time;
        foreach (var kvp in _requestCooldowns)
        {
            if (now > kvp.Value) expired.Add(kvp.Key);
        }
        foreach (var coord in expired) _requestCooldowns.Remove(coord);
    }

    /// <summary>
    /// Generates a valid, deterministic GUID based on grid coordinates using MD5.
    /// This ensures the same coordinate always produces the same chunk ID,
    /// which is essential for cache hits. Without determinism, the cache
    /// would never find a match even if it has the chunk data.
    /// </summary>
    private Guid GenerateChunkId(Vector2Int coord)
    {
        string key = $"Chunk_{coord.x}_{coord.y}";
        using (MD5 md5 = MD5.Create())
        {
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(key));
            return new Guid(hash);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 CalculateNeighborOrigin(Vector3 currentCenter, Direction dir)
    {
        Vector3 currentCorner = currentCenter - new Vector3(_halfChunkWidth, 0f, _halfChunkDepth);
        return dir switch
        {
            Direction.Forward => currentCorner + Vector3.forward * _chunkDepth,
            Direction.Back => currentCorner + Vector3.back * _chunkDepth,
            Direction.Right => currentCorner + Vector3.right * _chunkWidth,
            Direction.Left => currentCorner + Vector3.left * _chunkWidth,
            _ => currentCorner
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 CoordToWorldOrigin(Vector2Int coord) => new Vector3(
        coord.x * _chunkWidth, 0f, coord.y * _chunkDepth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 CoordToWorldCenter(Vector2Int coord) => new Vector3(
        coord.x * _chunkWidth + _halfChunkWidth, 0f, coord.y * _chunkDepth + _halfChunkDepth);

    private BiomeDefinitionSO GetBiomeForCoordinate(Vector2Int coord)
    {
        return Resources.Load<BiomeDefinitionSO>("DefaultBiome");
    }

    /// <summary>
    /// Instantiates chunk from cache. Returns true on success, false on failure.
    /// Applies the delta state AFTER initialization so that Init can skip
    /// already-collected loot and destroyed obstacles during instantiation.
    /// </summary>
    private bool InstantiateFromSnapshot(ChunkSnapshot snapshot, Vector3 worldOrigin, Vector2Int coord, Guid chunkId)
    {
        if (_activeChunks.ContainsKey(coord)) return true;

        var chunk = _chunkPool.Get(worldOrigin, _config);
        if (chunk == null)
        {
            Debug.LogError($"[ChunkStreamManager] Pool returned NULL for {coord} (Cache Hit). Check pool capacity.");
            return false;
        }

        try
        {
            // Apply delta BEFORE Init so that Init can skip collected loot
            chunk.ApplyDelta(snapshot.RuntimeState);
            chunk.Init(snapshot.BaseLayout, _config, coord);
            _activeChunks[coord] = chunk;

            // Notify subscribers that a chunk has been loaded
            OnChunkLoaded?.Invoke(coord);

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ChunkStreamManager] Init failed for {coord} (Cache): {ex.Message}");
            _chunkPool.Return(chunk);
            return false;
        }
    }

    /// <summary>
    /// Instantiates chunk from pipeline. Returns true on success, false on failure.
    /// The snapshot is saved by the caller (RequestChunk) after successful instantiation.
    /// </summary>
    private bool InstantiateFromLayout(LayoutData layout, Vector3 worldOrigin, Vector2Int coord, Guid chunkId)
    {
        if (_activeChunks.ContainsKey(coord)) return true;

        var chunk = _chunkPool.Get(worldOrigin, _config);
        if (chunk == null)
        {
            Debug.LogError($"[ChunkStreamManager] Pool returned NULL for {coord} (Pipeline). Check pool capacity.");
            return false;
        }

        try
        {
            chunk.Init(layout, _config, coord);
            _activeChunks[coord] = chunk;

            // Notify subscribers that a chunk has been loaded
            OnChunkLoaded?.Invoke(coord);

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ChunkStreamManager] Init failed for {coord} (Pipeline): {ex.Message}");
            _chunkPool.Return(chunk);
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector2Int GetDirectionOffset(Direction dir) => dir switch
    {
        Direction.Forward => Vector2Int.up,
        Direction.Back => Vector2Int.down,
        Direction.Right => Vector2Int.right,
        Direction.Left => Vector2Int.left,
        _ => Vector2Int.zero
    };

    /// <summary>
    /// Unloads all active chunks and clears all tracking data.
    /// Use when quitting the game or loading a completely different world.
    /// </summary>
    public void UnloadAll()
    {
        foreach (var chunk in _activeChunks.Values)
            _chunkPool.Return(chunk);

        _activeChunks.Clear();
        _pendingCoords.Clear();
        _generatedCoords.Clear();
        _coordToChunkId.Clear();
        _chunkStates.Clear();
        _requestCooldowns.Clear();
    }

    private struct ChunkRuntimeState
    {
        public int SpawnFrame;
    }
}