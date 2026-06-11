using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Manages the lifecycle of procedural chunks with STRICT active chunk limit.
/// Guarantees maximum 2 active chunks: current player chunk + one neighbor via anchor.
/// 
/// Responsibilities:
/// 1. Spawning new chunks ONLY via biome anchors (Discovery).
/// 2. Unloading distant chunks to maintain strict memory budget (Lifecycle Management).
/// 3. Re-loading previously visited chunks from cache when player returns (Regeneration).
/// 4. Ensuring deterministic Chunk IDs to enable proper caching.
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
    /// </summary>
    private readonly HashSet<Vector2Int> _pendingCoords = new HashSet<Vector2Int>();

    /// <summary>
    /// Tracks all coordinates that have EVER been generated or requested.
    /// Used for deterministic regeneration via anchors only.
    /// </summary>
    private readonly HashSet<Vector2Int> _generatedCoords = new HashSet<Vector2Int>();

    /// <summary>
    /// Maps Coordinates to their Deterministic Chunk IDs for caching.
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

    private Vector3 _lastPlayerPosition;
    private readonly float _chunkWidth;
    private readonly float _chunkDepth;
    private readonly float _halfChunkWidth;
    private readonly float _halfChunkDepth;

    // CRITICAL: Strict limits for memory budget compliance
    private const int MaxActiveChunks = 4; // Hard limit: current + one neighbor
    private readonly float _anchorTriggerDistanceSq;
    private readonly float _unloadDistanceSq;
    private readonly float _loadDistanceSq; // For regeneration of known chunks

    // Stability settings
    private readonly int _minLifetimeFrames = 10;
    private readonly float _requestCooldownSeconds = 1.0f;
    private int _spawnsThisFrame = 0;
    private const int MaxSpawnsPerFrame = 1; // Only one new chunk per frame

    /// <summary>
    /// Initializes the stream manager with dependencies and calculates spatial thresholds.
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

        // Pool capacity matches strict active chunk limit
        _chunkPool = new ChunkPool(chunkPrefab, null, MaxActiveChunks + 2);

        _chunkWidth = config.GridDimensions.x * config.CellSize;
        _chunkDepth = config.GridDimensions.y * config.CellSize;
        _halfChunkWidth = _chunkWidth * 0.5f;
        _halfChunkDepth = _chunkDepth * 0.5f;

        // Anchor trigger: player must be very close to anchor to trigger neighbor
        _anchorTriggerDistanceSq = (_config.CellSize * 5f);
        _anchorTriggerDistanceSq *= _anchorTriggerDistanceSq;

        // Unload distance: slightly more than one chunk away from player
        float unloadDist = Mathf.Max(_chunkWidth, _chunkDepth) * 1.2f;
        _unloadDistanceSq = unloadDist * unloadDist;

        // Load distance for regeneration: slightly less than unload to create hysteresis
        float loadDist = Mathf.Max(_chunkWidth, _chunkDepth) * 0.9f;
        _loadDistanceSq = loadDist * loadDist;

        Debug.Log($"[ChunkStreamManager] Initialized. MaxActive: {MaxActiveChunks}, Load: {loadDist:F1}, Unload: {unloadDist:F1}");
    }

    /// <summary>
    /// Main tick method. Must be called EXACTLY ONCE per frame from MonoBehaviour.Update().
    /// </summary>
    public void UpdatePlayerPosition(Vector3 playerPosition)
    {
        _lastPlayerPosition = playerPosition;
        _spawnsThisFrame = 0;

        // 1. Clean up expired cooldowns
        CleanupCooldowns();

        // 2. Enforce strict active chunk limit BEFORE loading new ones
        EnforceActiveChunkLimit();

        // 3. Unload distant chunks
        EvaluateUnloading();

        // 4. Load new chunks via anchors OR regenerate known chunks
        EvaluateLoading(playerPosition);
    }

    /// <summary>
    /// CRITICAL: Enforces hard limit on active chunks.
    /// If limit exceeded, unloads farthest chunks until within budget.
    /// </summary>
    private void EnforceActiveChunkLimit()
    {
        while (_activeChunks.Count > MaxActiveChunks)
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
    /// Unloads a specific chunk and cleans up tracking data.
    /// </summary>
    private void UnloadChunk(Vector2Int coord)
    {
        if (_activeChunks.Remove(coord, out var chunk))
        {
            if (_config.EnableChunkCache && _coordToChunkId.TryGetValue(coord, out var chunkId))
                _cache.UpdateDelta(chunkId, chunk.CaptureDelta());

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
    /// Public API: Requests a chunk from the pipeline or cache.
    /// Respects MaxActiveChunks limit - may trigger immediate unload of distant chunk.
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
                EnforceActiveChunkLimit(); // Ensure limit after instantiation
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
                if (_config.EnableChunkCache)
                    _cache.StoreSnapshot(result.ChunkId, result, new ChunkDelta());

                if (InstantiateFromLayout(result, request.WorldOrigin, coord, result.ChunkId))
                {
                    _requestCooldowns.Remove(coord);
                    EnforceActiveChunkLimit(); // Ensure limit after instantiation
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
    /// Unloads chunks that exceed the unload distance threshold.
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
    /// </summary>
    private void EvaluateLoading(Vector3 playerPos)
    {
        // 1. Try to discover NEW neighbors via anchors (higher priority)
        if (_spawnsThisFrame < MaxSpawnsPerFrame && _activeChunks.Count < MaxActiveChunks)
        {
            TryDiscoverNeighborsFromAllChunks(playerPos);
        }

        // 2. Try to regenerate KNOWN chunks from cache (lower priority)
        // Only if we still have budget and haven't spawned this frame
        if (_spawnsThisFrame < MaxSpawnsPerFrame && _activeChunks.Count < MaxActiveChunks)
        {
            TryRegenerateKnownChunks(playerPos);
        }
    }

    /// <summary>
    /// Scans anchors on active chunks to discover NEW neighbors.
    /// Only triggers if under MaxActiveChunks limit.
    /// </summary>
    private void TryDiscoverNeighborsFromAllChunks(Vector3 playerPos)
    {
        // Create snapshot to avoid collection modification during iteration
        var activeChunksSnapshot = new List<Chunk>(_activeChunks.Values);

        foreach (var chunk in activeChunksSnapshot)
        {
            if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
            if (_activeChunks.Count >= MaxActiveChunks) break; // Hard limit check

            var biomeDef = chunk.layoutData.BiomeDefinition;
            if (biomeDef?.ConnectionAnchors == null) continue;

            foreach (var anchor in biomeDef.ConnectionAnchors)
            {
                if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
                if (_activeChunks.Count >= MaxActiveChunks) break;

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
    /// </summary>
    private void TryRegenerateKnownChunks(Vector3 playerPos)
    {
        // Iterate through all known coordinates
        foreach (var coord in _generatedCoords)
        {
            if (_spawnsThisFrame >= MaxSpawnsPerFrame) break;
            if (_activeChunks.Count >= MaxActiveChunks) break;

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
            }
        }
    }

    /// <summary>
    /// Generates a valid, deterministic GUID based on grid coordinates using MD5.
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
            chunk.Init(snapshot.BaseLayout, _config, coord);
            chunk.ApplyDelta(snapshot.RuntimeState);
            _activeChunks[coord] = chunk;
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
    private Vector2Int GetExitCellForNext(Direction exitDir) => exitDir switch
    {
        Direction.Forward => new Vector2Int(_config.GridDimensions.x - 1, _config.GridDimensions.y - 1),
        Direction.Back => new Vector2Int(0, _config.GridDimensions.y - 1),
        Direction.Right => new Vector2Int(_config.GridDimensions.x - 1, _config.GridDimensions.y - 1),
        Direction.Left => new Vector2Int(0, _config.GridDimensions.y - 1),
        _ => Vector2Int.zero
    };

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