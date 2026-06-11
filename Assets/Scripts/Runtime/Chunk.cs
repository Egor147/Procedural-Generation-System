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
                if (placedCell.PlacedObject?.Prefab != null)
                {
                    // Calculate world offset relative to chunk's bottom-left corner (WorldOrigin)
                    Vector3 localOffset = new Vector3(
                        (x + 0.5f) * config.CellSize,
                        (0.75f) * config.CellSize, // Slightly above floor to prevent z-fighting
                        (z + 0.5f) * config.CellSize);

                    var obj = Instantiate(placedCell.PlacedObject.Prefab, _contentRoot);

                    // Position object: chunk center -> corner -> cell offset
                    obj.transform.position = transform.position - chunkSize * 0.5f + localOffset;
                    obj.transform.rotation = placedCell.LocalRotation;

                    _instantiatedObjects.Add(obj);
                }
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
    /// Applies runtime delta state (collected loot, destroyed obstacles) to the chunk.
    /// </summary>
    public void ApplyDelta(ChunkDelta delta)
    {
        _appliedDelta = delta;
        Debug.Log($"[Chunk] Applied delta: {delta.CollectedLootIds.Count} items.");
    }

    /// <summary>
    /// Captures current runtime state for caching before chunk recycling.
    /// </summary>
    public ChunkDelta CaptureDelta() => _appliedDelta ?? new ChunkDelta();

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