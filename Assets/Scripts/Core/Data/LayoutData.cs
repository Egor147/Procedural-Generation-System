using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Immutable data transfer object representing a fully generated chunk layout.
/// Contains only serializable primitives and structs. Safe for cross-thread transfer.
/// </summary>
public readonly struct LayoutData
{
    /// <summary>
    /// Unique identifier for this chunk instance. Used for caching and streaming.
    /// </summary>
    public Guid ChunkId { get; }

    /// <summary>
    /// Reference to the biome definition used for generation.
    /// </summary>
    public BiomeDefinitionSO BiomeDefinition { get; }

    /// <summary>
    /// 2D grid of placed objects. Index by [x, z] for horizontal mode.
    /// </summary>
    public PlacedCell[,] Grid { get; }

    /// <summary>
    /// World-space position of the chunk's origin (grid [0,0] corner).
    /// </summary>
    public Vector3 WorldOrigin { get; }

    /// <summary>
    /// Grid coordinate of the player entry point within this chunk.
    /// </summary>
    public Vector2Int EntryCell { get; }

    /// <summary>
    /// Grid coordinate of the exit point (leads to next chunk).
    /// </summary>
    public Vector2Int ExitCell { get; }

    /// <summary>
    /// Metadata about generation: seed, timestamp, performance metrics.
    /// </summary>
    public GenerationMetadata Metadata { get; }

    /// <summary>
    /// Constructor for immutable initialization.
    /// </summary>
    public LayoutData(
        Guid chunkId,
        BiomeDefinitionSO biomeDef,
        PlacedCell[,] grid,
        Vector3 worldOrigin,
        Vector2Int entryCell,
        Vector2Int exitCell,
        GenerationMetadata metadata)
    {
        ChunkId = chunkId;
        BiomeDefinition = biomeDef;
        Grid = grid;
        WorldOrigin = worldOrigin;
        EntryCell = entryCell;
        ExitCell = exitCell;
        Metadata = metadata;
    }
}

/// <summary>
/// Represents a single grid cell after generation.
/// </summary>
public readonly struct PlacedCell
{
    /// <summary>
    /// The object placed in this cell, or null if empty.
    /// </summary>
    public SpawnableObjectSO PlacedObject { get; }

    /// <summary>
    /// True if this cell is part of the reserved player path.
    /// </summary>
    public bool IsPathCell { get; }

    /// <summary>
    /// Local rotation applied to the placed object.
    /// </summary>
    public Quaternion LocalRotation { get; }

    public PlacedCell(SpawnableObjectSO placedObject, bool isPathCell, Quaternion localRotation)
    {
        PlacedObject = placedObject;
        IsPathCell = isPathCell;
        LocalRotation = localRotation;
    }
}

/// <summary>
/// Metadata container for generation diagnostics and caching.
/// </summary>
public readonly struct GenerationMetadata
{
    public int Seed { get; }
    public long GenerationTimeMs { get; }
    public int PlacementAttempts { get; }
    public DateTime Timestamp { get; }

    public GenerationMetadata(int seed, long timeMs, int attempts)
    {
        Seed = seed;
        GenerationTimeMs = timeMs;
        PlacementAttempts = attempts;
        Timestamp = DateTime.UtcNow;
    }
}