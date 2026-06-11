using System;
using System.Diagnostics;
using UnityEngine;

/// <summary>
/// Final step of generation pipeline. Converts mutable GridGraph into immutable LayoutData.
/// Also collects generation metadata for diagnostics and caching.
/// </summary>
public class ChunkAssembler
{
    /// <summary>
    /// Assembles a LayoutData DTO from a completed GridGraph.
    /// Measures generation time and placement attempts for metrics.
    /// </summary>
    public LayoutData Assemble(
        GridGraph grid,
        Guid chunkId,
        BiomeDefinitionSO biomeDef,
        Vector3 worldOrigin,
        Vector2Int entryCell,
        Vector2Int exitCell,
        int seed,
        int placementAttempts)
    {
        var stopwatch = Stopwatch.StartNew();
        // In real implementation, placementAttempts would be tracked by FillPlacer

        var metadata = new GenerationMetadata(seed, stopwatch.ElapsedMilliseconds, placementAttempts);

        return grid.BuildLayoutData(
            chunkId,
            biomeDef,
            worldOrigin,
            entryCell,
            exitCell,
            metadata);
    }
}