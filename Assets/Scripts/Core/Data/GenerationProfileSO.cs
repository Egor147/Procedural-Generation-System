using UnityEngine;

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