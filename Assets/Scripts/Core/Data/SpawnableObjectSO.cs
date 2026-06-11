using UnityEngine;
using System;

/// <summary>
/// Configuration for a single placable object in procedural generation.
/// Contains placement rules, passability data, and visual/audio references.
/// </summary>
[CreateAssetMenu(fileName = "NewSpawnObject", menuName = "Procedural/Spawnable Object")]
public class SpawnableObjectSO : ScriptableObject
{
    /// <summary>
    /// The actual prefab to instantiate. Must have a valid MeshRenderer or Collider for bounds calculation.
    /// </summary>
    public GameObject Prefab;

    /// <summary>
    /// Pre-calculated bounding box in local space. Set in editor or via custom inspector.
    /// Used for collision checks during placement. Prefer setting manually over runtime Mesh.bounds.
    /// </summary>
    [Tooltip("Manually set bounds for reliable placement. Use CustomInspector to bake from Mesh.")]
    public Bounds LocalBounds;

    /// <summary>
    /// If true, this object blocks player movement (e.g., wall, rock). If false, it's decorative or collectible.
    /// </summary>
    [Tooltip("Mark obstacles as non-walkable. Loot/triggers should be walkable.")]
    public bool IsWalkable = true;

    /// <summary>
    /// Tags for filtering placement context (e.g., "Obstacle", "Loot", "Trap", "Platform").
    /// Used by FillPlacer to respect biome rules and path reservation.
    /// </summary>
    [Tooltip("Tags control where and how this object can be placed.")]
    public string[] PlacementTags;

    /// <summary>
    /// Relative spawn weight. Higher = more likely to be chosen when multiple objects are compatible.
    /// </summary>
    [Range(0.1f, 10f)]
    public float SpawnWeight = 1f;

    /// <summary>
    /// If set, this object can spawn child objects within its bounds (e.g., loot on a table).
    /// Processed recursively after parent placement.
    /// </summary>
    [Tooltip("Optional nested spawn rules for complex objects.")]
    public ChildSpawnRule[] NestedSpawnRules;

    /// <summary>
    /// Priority for placement order. Lower numbers placed first (e.g., platforms before obstacles).
    /// </summary>
    [Range(0, 100)]
    public int PlacementPriority = 50;
}

/// <summary>
/// Rule for spawning child objects within a parent's bounds.
/// </summary>
[Serializable]
public class ChildSpawnRule
{
    /// <summary>
    /// Objects eligible for nested spawning.
    /// </summary>
    public SpawnableObjectSO[] ChildCandidates;

    /// <summary>
    /// Maximum number of children to spawn per parent instance.
    /// </summary>
    public int MaxChildren = 3;

    /// <summary>
    /// Local offset range for child placement (prevents clipping into parent geometry).
    /// </summary>
    public Vector3 OffsetRange = new Vector3(0.5f, 0.2f, 0.5f);
}