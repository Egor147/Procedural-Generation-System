using UnityEngine;
using System;

/// <summary>
/// Determines how chunks are discovered and loaded around the player.
/// </summary>
public enum StreamingMode
{
    /// <summary>
    /// Loads chunks only through biome-defined connection anchors.
    /// This is the optimized approach: chunks load when the player
    /// approaches specific connection points, which reduces the number
    /// of simultaneous loads and makes caching more effective since
    /// players tend to follow the same paths.
    /// </summary>
    AnchorBased,

    /// <summary>
    /// Loads all chunks within a fixed radius around the player.
    /// This is the standard/naive approach (like Minecraft): chunks
    /// load based purely on distance, regardless of path connectivity.
    /// Simpler to implement but causes more frequent load/unload cycles
    /// and less effective cache utilization.
    /// </summary>
    DistanceBased
}

/// <summary>
/// Global configuration profile for procedural chunk generation.
/// Controls generation mode, performance limits, and optimization toggles.
/// Designed for easy comparison of algorithms in research scenarios.
/// </summary>
[CreateAssetMenu(fileName = "GenerationProfile", menuName = "Procedural/Generation Profile")]
public class GenerationProfileSO : ScriptableObject
{
    /// <summary>
    /// Maximum number of chunks kept active in memory around the player.
    /// Value of 1 = current chunk only, 2 = current + next, etc.
    /// Used for memory/FPS comparison benchmarks.
    /// </summary>
    [Tooltip("Radius of active chunks around player. Higher = more memory, smoother streaming.")]
    public int ActiveChunkRadius = 2;

    /// <summary>
    /// Enables multithreaded generation. When disabled, all logic runs synchronously on main thread.
    /// Critical toggle for performance comparison studies.
    /// </summary>
    [Tooltip("If unchecked, generation runs on main thread (for debugging or baseline benchmarks).")]
    public bool EnableMultithreading = true;

    /// <summary>
    /// Streaming strategy. AnchorBased loads chunks only through biome anchors
    /// (optimized, cache-friendly). DistanceBased loads all chunks within a
    /// fixed radius around the player (standard approach, like Minecraft).
    /// </summary>
    [Tooltip("AnchorBased = optimized (cache-friendly). DistanceBased = standard (radius-based).")]
    public StreamingMode StreamingMode = StreamingMode.AnchorBased;

    /// <summary>
    /// Radius in chunks for DistanceBased streaming mode.
    /// Ignored in AnchorBased mode.
    /// </summary>
    [Tooltip("How many chunks away from the player should be kept active (DistanceBased only).")]
    [Range(1, 10)]
    public int DistanceBasedRadius = 2;

    /// <summary>
    /// Extra buffer for unloading in DistanceBased mode.
    /// Chunks are unloaded only when they are (Radius + UnloadBuffer) away,
    /// creating hysteresis that prevents constant load/unload at boundaries.
    /// </summary>
    [Tooltip("Extra chunks before unloading in DistanceBased mode (hysteresis buffer).")]
    [Range(0, 5)]
    public int DistanceBasedUnloadBuffer = 1;

    /// <summary>
    /// Allowed generation directions. Horizontal only, vertical only, or omnidirectional.
    /// </summary>
    public GenerationMode GenerationMode = GenerationMode.Horizontal;

    /// <summary>
    /// Grid resolution per chunk (X by Z for horizontal, X by Y for vertical mode).
    /// </summary>
    [Tooltip("Number of cells in each dimension of a chunk's grid.")]
    public Vector2Int GridDimensions = new Vector2Int(10, 10);

    /// <summary>
    /// World size of a single grid cell in meters. Affects object placement precision.
    /// </summary>
    public float CellSize = 2f;

    /// <summary>
    /// Maximum attempts to place an object before skipping it. Prevents infinite loops.
    /// </summary>
    public int MaxPlacementAttempts = 20;

    /// <summary>
    /// If true, enables ChunkCache for state persistence. Disable for naive regeneration baseline.
    /// </summary>
    [Tooltip("Toggle caching to compare 'regenerate' vs 'restore' performance.")]
    public bool EnableChunkCache = true;

    /// <summary>
    /// Seed for random generation. Zero means random each time; non-zero for deterministic tests.
    /// </summary>
    public int RandomSeed = 0;
}

/// <summary>
/// Enum defining allowed chunk propagation directions.
/// </summary>
public enum GenerationMode
{
    Horizontal,   // XZ plane, forward/backward movement
    Vertical,     // XY plane, climbing/falling movement
    Omnidirectional // All 6 directions (3D grid)
}