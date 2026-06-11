using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Defines all rules and assets for a specific biome type.
/// Acts as a factory configuration for chunk generation.
/// </summary>
[CreateAssetMenu(fileName = "NewBiome", menuName = "Procedural/Biome Definition")]
public class BiomeDefinitionSO : ScriptableObject
{
    /// <summary>
    /// Prefab representing the floor/terrain of this biome.
    /// Must have a GridAligner component or defined anchor points.
    /// </summary>
    [Tooltip("Base floor prefab. Should match GridDimensions * CellSize.")]
    public GameObject FloorPrefab;

    /// <summary>
    /// Catalog of objects allowed to spawn in this biome.
    /// Weights and tags are defined per-object in SpawnableObjectSO.
    /// </summary>
    [Tooltip("Pool of placable objects. Filtered by tags during placement.")]
    public SpawnableObjectSO[] AllowedObjects;

    /// <summary>
    /// Connection anchors for stitching chunks together.
    /// Positions are in local chunk space (grid coordinates).
    /// </summary>
    [Tooltip("Defines where adjacent chunks should connect.")]
    public ConnectionAnchor[] ConnectionAnchors;

    /// <summary>
    /// Preferred path width in cells. PathCarver will reserve this many parallel lanes.
    /// </summary>
    [Range(1, 3)]
    public int PathWidth = 1;

    /// <summary>
    /// Minimum slope angle (degrees) allowed for walkable path cells.
    /// Used by PathCarver to avoid placing path on steep surfaces.
    /// </summary>
    [Range(0f, 45f)]
    public float MaxPathSlope = 30f;

    /// <summary>
    /// Post-processing profile to apply when player enters this biome.
    /// Optional, for visual differentiation.
    /// </summary>
    //public VolumeProfile PostProcessingProfile;

    /// <summary>
    /// Probability (0.0 to 1.0) that any given eligible cell will receive an object.
    /// Controls overall clutter density: 1.0 = maximum placement, 0.3 = sparse decoration.
    /// If unset or zero, defaults to 1.0 for backward compatibility.
    /// </summary>
    [Tooltip("Placement density: 1.0 = fill all eligible cells, 0.5 = ~50% of cells, 0.0 = no objects")]
    [Range(0f, 1f)]
    public float PlacementDensity = 1f;
    

    /// <summary>
    /// Percentage (0.0 - 1.0) of the path Wander
    /// </summary>
    [Tooltip("Percentage (0.0 - 1.0) of the chunk max occupied by path")]
    [Range(0.1f, 1f)]
    public float pathWanderFactor = 1f;

    /// <summary>
    /// Percentage (0.0 - 5.0) how path avoid cencer line 
    /// </summary>
    [Tooltip("Percentage (0.0 - 5.0) how path avoid cencer line ")]
    [Range(0f,5f)]
    public float centerAvoidanceStrength = 1f;

}

/// <summary>
/// Defines a connection point for chunk stitching.
/// </summary>
[Serializable]
public class ConnectionAnchor
{
    /// <summary>
    /// Grid coordinate of the anchor point within the chunk.
    /// </summary>
    public Vector2Int GridPosition;

    /// <summary>
    /// Direction this anchor faces (for aligning adjacent chunks).
    /// </summary>
    public Direction ConnectionDirection;

    /// <summary>
    /// Optional tag to enforce biome compatibility (e.g., "Cave" only connects to "Cave").
    /// </summary>
    public string CompatibilityTag;
}

/// <summary>
/// Cardinal and vertical directions for 3D grid navigation.
/// </summary>
public enum Direction
{
    Forward, Back, Left, Right, Up, Down
}