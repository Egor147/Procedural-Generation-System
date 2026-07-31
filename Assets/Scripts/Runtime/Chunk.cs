using UnityEngine;
using System.Collections.Generic;
using System;

/// <summary>
/// MonoBehaviour wrapper for a generated chunk.
/// Handles instantiation, activation, and delta state application.
/// </summary>
public class Chunk : MonoBehaviour
{
    [SerializeField] private Transform _contentRoot;
    [SerializeField] private Collider _triggerVolume;

    public LayoutData layoutData;
    private ChunkDelta _appliedDelta;
    private readonly List<GameObject> _instantiatedObjects = new List<GameObject>();

    /// <summary>
    /// The exact grid coordinate of this chunk in the global procedural space.
    /// Set during initialization to avoid floating-point drift during streaming.
    /// </summary>
    public Vector2Int GridCoordinate { get; private set; }

    // Runtime delta for this chunk instance. Holds the set of collected loot
    // IDs (and later, destroyed obstacles, activated triggers, etc.).
    // Starts as null and is populated either by ApplyDelta (when restoring
    // from cache) or by MarkLootCollected (when the player picks something up).
    private ChunkDelta _runtimeDelta;

    /// <summary>
    /// Records that a piece of loot has been picked up. Called by LootItem
    /// when the player walks through it. The ID is added to the runtime delta,
    /// which is what CaptureDelta() later returns for cache storage.
    /// </summary>
    public void MarkLootCollected(string lootId)
    {
        if (_runtimeDelta == null)
        {
            _runtimeDelta = new ChunkDelta();
        }

        _runtimeDelta.CollectedLootIds.Add(lootId);
    }

    // Builds a deterministic ID for a piece of loot based on its cell
    // coordinates and the chunk seed. Two regenerations of the same chunk
    // (same seed, same grid position) will produce the same ID, which is
    // the whole reason this system works: the "collected" set in the delta
    // can be matched against freshly spawned loot.
    private static string GenerateLootId(Vector3Int cellCoord, int chunkSeed)
    {
        return $"loot_{chunkSeed}_{cellCoord.x}_{cellCoord.y}_{cellCoord.z}";
    }


    /// <summary>
    /// Initializes the chunk with generated layout data and its global grid coordinate.
    /// </summary>
    /// <param name="layoutData">Immutable layout data from the generation pipeline.</param>
    /// <param name="config">Generation profile for cell size and dimensions.</param>
    /// <param name="gridCoord">Explicit global grid coordinate for deterministic streaming.</param>
    public void Init(LayoutData layoutData, GenerationProfileSO config, Vector2Int gridCoord)
    {
        this.layoutData = layoutData;
        GridCoordinate = gridCoord;

        Vector3 chunkSize = new Vector3(
            layoutData.Grid.GetLength(0) * config.CellSize,
            0,
            layoutData.Grid.GetLength(1) * config.CellSize);

        // 1. Position chunk pivot at CENTER of the grid (standard for object pooling)
        transform.position = layoutData.WorldOrigin + chunkSize * 0.5f;
        name = $"Chunk_{layoutData.ChunkId:N}";

        // 2. Instantiate Floor prefab
        if (layoutData.BiomeDefinition.FloorPrefab != null)
        {
            var floor = Instantiate(layoutData.BiomeDefinition.FloorPrefab, _contentRoot);
            floor.transform.localPosition = Vector3.zero;
            floor.transform.localRotation = Quaternion.identity;
            _instantiatedObjects.Add(floor);
        }

        // 3. Instantiate placed objects (obstacles, loot, decorations)
        for (int x = 0; x < layoutData.Grid.GetLength(0); x++)
        {
            for (int z = 0; z < layoutData.Grid.GetLength(1); z++)
            {
                var placedCell = layoutData.Grid[x, z];
                if (placedCell.PlacedObject?.Prefab == null) continue;

                // Check whether this object is loot. We detect it by the presence
                // of a LootItem component on the prefab rather than by tags or
                // naming conventions, so the placer doesn't need to know anything
                // special about loot - it just places whatever the biome says.
                var lootPrefabComponent = placedCell.PlacedObject.Prefab.GetComponent<LootItem>();
                bool isLoot = lootPrefabComponent != null;

                if (isLoot)
                {
                    string lootId = GenerateLootId(new Vector3Int(x, 0, z), layoutData.Metadata.Seed);

                    // If this loot was already collected in a previous visit to this
                    // chunk, skip instantiation entirely. The delta carries that
                    // information forward across chunk reloads.
                    if (_runtimeDelta != null && _runtimeDelta.CollectedLootIds.Contains(lootId))
                    {
                        continue;
                    }
                }

                // Calculate world offset relative to chunk's bottom-left corner (WorldOrigin)
                Vector3 localOffset = new Vector3(
                    (x + 0.5f) * config.CellSize,
                    (0.75f) * config.CellSize,
                    (z + 0.5f) * config.CellSize);

                var obj = Instantiate(placedCell.PlacedObject.Prefab, _contentRoot);

                // Position object: chunk center -> corner -> cell offset
                obj.transform.position = transform.position - chunkSize * 0.5f + localOffset;
                obj.transform.rotation = placedCell.LocalRotation;

                // For loot objects, wire up the LootItem component with its ID and
                // parent chunk reference. Without this step the loot would exist in
                // the scene but would be uncollectable and wouldn't persist its state.
                if (isLoot)
                {
                    string lootId = GenerateLootId(new Vector3Int(x, 0, z), layoutData.Metadata.Seed);
                    var lootComponent = obj.GetComponent<LootItem>();
                    if (lootComponent != null)
                    {
                        lootComponent.Initialize(lootId, this);
                    }
                }

                _instantiatedObjects.Add(obj);
            }
        }

        // 4. Configure trigger collider for streaming boundary detection
        if (_triggerVolume != null && _triggerVolume is BoxCollider box)
        {
            box.size = new Vector3(chunkSize.x, 10f, chunkSize.z);
            box.center = Vector3.zero;
        }
    }

    /// <summary>
    /// Applies a previously captured delta to this chunk instance.
    /// Called by ChunkStreamManager when restoring a chunk from cache.
    /// The key effect is that _runtimeDelta now contains the set of
    /// collected loot IDs, so Init() (or any later logic) can skip them.
    /// </summary>
    public void ApplyDelta(ChunkDelta delta)
    {
        _runtimeDelta = delta ?? new ChunkDelta();
    }

    /// <summary>
    /// Returns the current runtime delta for this chunk. Called by
    /// ChunkStreamManager right before a chunk is returned to the pool,
    /// so the collected-loot state can be saved into the cache snapshot.
    /// </summary>
    public ChunkDelta CaptureDelta() => _runtimeDelta ?? new ChunkDelta();

    /// <summary>
    /// Deactivates the chunk for pooling without destroying GameObjects.
    /// </summary>
    public void Deactivate() => gameObject.SetActive(false);

    /// <summary>
    /// Reactivates a pooled chunk at a new world position.
    /// </summary>
    public void Reactivate(Vector3 newPosition)
    {
        transform.position = newPosition;
        gameObject.SetActive(true);
    }

    /// <summary>
    /// Clears all instantiated objects and resets state for pool return.
    /// </summary>
    public void Clear()
    {
        foreach (var obj in _instantiatedObjects)
            if (obj != null) Destroy(obj);

        _instantiatedObjects.Clear();
        layoutData = default;
        _runtimeDelta = null;
        _appliedDelta = null;
        GridCoordinate = default;
    }

    /// <summary>
    /// Converts grid coordinate to local offset relative to chunk center.
    /// </summary>
    private Vector3 GridToWorld(Vector2Int gridPos, Vector3 totalSize, float cellSize)
    {
        float offsetX = -(totalSize.x / 2) + (cellSize / 2);
        float offsetZ = -(totalSize.z / 2) + (cellSize / 2);

        return new Vector3(
            offsetX + (gridPos.x * cellSize),
            0.5f,
            offsetZ + (gridPos.y * cellSize));
    }
}