using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// Runtime visualization of chunk internals (Path, Anchors, Grid) visible in Game View.
/// Attach to Chunk Prefab alongside ChunkPathVisualizer.
/// Controlled globally via DemoSceneSetup.EnableDebugGizmos.
/// </summary>
[RequireComponent(typeof(Chunk))]
public class ChunkRuntimeVisualizer : MonoBehaviour
{
    [Header("Visualization Settings")]
    [Tooltip("Size of a single grid cell. Must match GenerationProfileSO.CellSize")]
    [SerializeField] private float _cellSize = 2f;

    [Tooltip("Height offset for visual elements to prevent Z-fighting")]
    [SerializeField] private float _visualHeight = 0.3f;

    [Tooltip("Parent transform for all debug visuals (auto-created)")]
    [SerializeField] private Transform _debugRoot;

    [Header("Materials (assign simple unlit materials for best visibility)")]
    [SerializeField] private Material _pathMaterial;
    [SerializeField] private Material _anchorMaterial;
    [SerializeField] private Material _boundsMaterial;

    [Header("Colors (fallback if materials not assigned)")]
    [SerializeField] private Color _pathColor = new Color(0f, 1f, 0f, 0.8f);
    [SerializeField] private Color _anchorColor = new Color(1f, 1f, 0f, 1f);
    [SerializeField] private Color _boundsColor = new Color(1f, 1f, 1f, 0.3f);

    private Chunk _chunk;
    private readonly List<GameObject> _debugObjects = new List<GameObject>();
    private bool _isVisualizing = false;

    /// <summary>
    /// Called when component is enabled. Starts visualization if global toggle is on.
    /// </summary>
    private void OnEnable()
    {
        _chunk = GetComponent<Chunk>();
        if (DemoSceneSetup.EnableDebugGizmos)
        {
            StartVisualization();
        }
    }

    /// <summary>
    /// Called when component is disabled or chunk is destroyed. Cleans up all debug objects.
    /// </summary>
    private void OnDisable()
    {
        ClearVisualization();
    }

    /// <summary>
    /// Listens to global toggle changes and updates visualization state.
    /// </summary>
    private void Update()
    {
        if (DemoSceneSetup.EnableDebugGizmos && !_isVisualizing && _chunk != null && _chunk.layoutData.Grid != null)
        {
            StartVisualization();
        }
        else if (!DemoSceneSetup.EnableDebugGizmos && _isVisualizing)
        {
            ClearVisualization();
        }
    }

    /// <summary>
    /// Creates all debug visual objects for the current chunk.
    /// </summary>
    private void StartVisualization()
    {
        if (_chunk == null || _chunk.layoutData.Grid == null) return;

        ClearVisualization();

        var grid = _chunk.layoutData.Grid;
        var biome = _chunk.layoutData.BiomeDefinition;
        int width = grid.GetLength(0);
        int depth = grid.GetLength(1);

        // Create parent for debug objects
        _debugRoot = new GameObject("DebugVisuals").transform;
        _debugRoot.SetParent(transform, false);

        // 1. Draw chunk boundary (wireframe cube)
        DrawBounds(width, depth);

        // 2. Draw path cells and connections
        DrawPathCells(grid, width, depth);

        // 3. Draw connection anchors
        if (biome != null && biome.ConnectionAnchors != null)
        {
            DrawAnchors(biome.ConnectionAnchors, width, depth);
        }

        _isVisualizing = true;
    }

    /// <summary>
    /// Destroys all debug visual objects and cleans up.
    /// </summary>
    private void ClearVisualization()
    {
        foreach (var obj in _debugObjects)
        {
            if (obj != null) Destroy(obj);
        }
        _debugObjects.Clear();

        if (_debugRoot != null) Destroy(_debugRoot.gameObject);
        _debugRoot = null;
        _isVisualizing = false;
    }

    /// <summary>
    /// Draws a wireframe cube representing chunk boundaries.
    /// </summary>
    private void DrawBounds(int width, int depth)
    {
        float worldWidth = width * _cellSize;
        float worldDepth = depth * _cellSize;

        GameObject boundsObj = CreatePrimitive("Bounds", PrimitiveType.Cube, _boundsMaterial, _boundsColor);
        boundsObj.transform.localScale = new Vector3(worldWidth, 0.1f, worldDepth);
        boundsObj.transform.position = transform.position + Vector3.up * (_visualHeight + 0.05f);

        // Make it wireframe-like by using a transparent material
        var renderer = boundsObj.GetComponent<Renderer>();
        if (renderer != null && renderer.material != null)
        {
            renderer.material.SetFloat("_Mode", 2); // Transparent cutout
            renderer.material.SetColor("_Color", _boundsColor);
        }
    }

    /// <summary>
    /// Draws cubes for path cells and lines connecting them.
    /// </summary>
    private void DrawPathCells(PlacedCell[,] grid, int width, int depth)
    {
        // First pass: collect path cell positions
        var pathPositions = new List<Vector3>();

        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                if (grid[x, z].IsPathCell)
                {
                    Vector3 pos = GetCellWorldPosition(x, z, width, depth) + Vector3.up * _visualHeight;
                    pathPositions.Add(pos);

                    // Draw cell marker (small cube)
                    GameObject cellObj = CreatePrimitive($"Path_{x}_{z}", PrimitiveType.Cube, _pathMaterial, _pathColor);
                    cellObj.transform.localScale = Vector3.one * _cellSize * 0.85f;
                    cellObj.transform.position = pos;
                }
            }
        }

        // Second pass: draw connections between adjacent path cells
        for (int i = 0; i < pathPositions.Count; i++)
        {
            Vector3 current = pathPositions[i];

            // Check right neighbor
            for (int j = i + 1; j < pathPositions.Count; j++)
            {
                Vector3 other = pathPositions[j];
                // Only connect if cells are adjacent (distance ~cellSize)
                if (Vector3.Distance(current, other) <= _cellSize * 1.1f)
                {
                    DrawLine(current, other, _pathColor, 0.05f);
                }
            }
        }
    }

    /// <summary>
    /// Draws spheres for anchors and direction rays.
    /// </summary>
    private void DrawAnchors(ConnectionAnchor[] anchors, int width, int depth)
    {
        foreach (var anchor in anchors)
        {
            // Safety: ensure anchor coordinates are within grid bounds
            if (anchor.GridPosition.x < 0 || anchor.GridPosition.x >= width ||
                anchor.GridPosition.y < 0 || anchor.GridPosition.y >= depth)
                continue;

            Vector3 anchorPos = GetCellWorldPosition(anchor.GridPosition.x, anchor.GridPosition.y, width, depth) + Vector3.up * _visualHeight;

            // Draw anchor sphere
            GameObject anchorObj = CreatePrimitive($"Anchor_{anchor.ConnectionDirection}", PrimitiveType.Sphere, _anchorMaterial, _anchorColor);
            anchorObj.transform.localScale = Vector3.one * _cellSize * 0.5f;
            anchorObj.transform.position = anchorPos;

            // Draw direction ray
            Vector3 dir = GetDirectionVector(anchor.ConnectionDirection);
            DrawLine(anchorPos, anchorPos + dir * _cellSize * 1.5f, _anchorColor, 0.08f);
        }
    }

    /// <summary>
    /// Helper: Creates a primitive GameObject with material/color.
    /// </summary>
    private GameObject CreatePrimitive(string name, PrimitiveType type, Material material, Color color)
    {
        GameObject obj = GameObject.CreatePrimitive(type);
        obj.name = name;
        obj.transform.SetParent(_debugRoot, false);

        // Configure renderer
        var renderer = obj.GetComponent<Renderer>();
        if (renderer != null)
        {
            if (material != null)
            {
                renderer.material = material;
            }
            else
            {
                // Create simple unlit material at runtime if none assigned
                renderer.material = new Material(Shader.Find("Unlit/Color"));
                renderer.material.SetColor("_Color", color);
            }
        }

        _debugObjects.Add(obj);
        return obj;
    }

    /// <summary>
    /// Helper: Draws a line using a thin cube primitive.
    /// </summary>
    private void DrawLine(Vector3 start, Vector3 end, Color color, float thickness)
    {
        GameObject lineObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        lineObj.name = $"Line_{start.GetHashCode()}";
        lineObj.transform.SetParent(_debugRoot, false);

        // Position and rotate line to connect start->end
        Vector3 mid = (start + end) * 0.5f;
        Vector3 direction = end - start;
        float length = direction.magnitude;

        lineObj.transform.position = mid;
        lineObj.transform.rotation = Quaternion.LookRotation(direction);
        lineObj.transform.localScale = new Vector3(thickness, thickness, length);

        // Configure material
        var renderer = lineObj.GetComponent<Renderer>();
        if (renderer != null)
        {
            if (_pathMaterial != null)
                renderer.material = _pathMaterial;
            else
            {
                renderer.material = new Material(Shader.Find("Unlit/Color"));
                renderer.material.SetColor("_Color", color);
            }
        }

        _debugObjects.Add(lineObj);
    }

    /// <summary>
    /// Converts grid coordinates to world position relative to chunk center.
    /// </summary>
    private Vector3 GetCellWorldPosition(int x, int z, int width, int depth)
    {
        float offsetX = (x - width / 2f + 0.5f) * _cellSize;
        float offsetZ = (z - depth / 2f + 0.5f) * _cellSize;
        return transform.position + new Vector3(offsetX, 0f, offsetZ);
    }

    private Vector3 GetDirectionVector(Direction dir)
    {
        return dir switch
        {
            Direction.Right => Vector3.right,
            Direction.Left => Vector3.left,
            Direction.Forward => Vector3.forward,
            Direction.Back => Vector3.back,
            _ => Vector3.zero
        };
    }
}