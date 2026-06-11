using UnityEngine;
using System;

/// <summary>
/// Mutable grid structure used during chunk generation.
/// Provides spatial queries and neighbor access for PathCarver and FillPlacer.
/// Thread-safe for read operations; write operations require external synchronization.
/// </summary>
public class GridGraph
{
    /// <summary>
    /// 2D array of cells. Access via [x, z] for horizontal mode.
    /// </summary>
    private readonly Cell[,] _cells;

    /// <summary>
    /// Dimensions of the grid in cells.
    /// </summary>
    public Vector2Int Dimensions { get; }

    /// <summary>
    /// World size of a single cell in meters.
    /// </summary>
    public float CellSize { get; }

    /// <summary>
    /// Creates a new empty grid with specified dimensions.
    /// </summary>
    public GridGraph(Vector2Int dimensions, float cellSize)
    {
        Dimensions = dimensions;
        CellSize = cellSize;
        _cells = new Cell[dimensions.x, dimensions.y];

        for (int x = 0; x < dimensions.x; x++)
        {
            for (int z = 0; z < dimensions.y; z++)
            {
                _cells[x, z] = new Cell(new Vector2Int(x, z));
            }
        }
    }

    /// <summary>
    /// Returns the cell at the given grid coordinates, or null if out of bounds.
    /// </summary>
    public Cell GetCell(Vector2Int coordinates)
    {
        if (IsValidCoordinate(coordinates))
        {
            return _cells[coordinates.x, coordinates.y];
        }
        return null;
    }

    /// <summary>
    /// Returns all valid orthogonal neighbors of a cell (4-directional).
    /// For 8-directional or 3D, extend this method or create a separate Graph3D.
    /// </summary>
    public Cell[] GetNeighbors(Vector2Int coordinates)
    {
        var neighbors = new Cell[4];
        int count = 0;

        Vector2Int[] directions = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

        foreach (var dir in directions)
        {
            var neighborCoord = coordinates + dir;
            var neighbor = GetCell(neighborCoord);
            if (neighbor != null)
            {
                neighbors[count++] = neighbor;
            }
        }

        Array.Resize(ref neighbors, count);
        return neighbors;
    }

    /// <summary>
    /// Converts grid coordinates to world-space position (center of cell).
    /// </summary>
    public Vector3 GridToWorld(Vector2Int gridPos, Vector3 chunkOrigin)
    {
        var cell = GetCell(gridPos);
        float height = cell?.Height ?? 0f;
        return chunkOrigin + new Vector3(
            (gridPos.x + 0.5f) * CellSize,
            height,
            (gridPos.y + 0.5f) * CellSize);
    }

    /// <summary>
    /// Converts world position to grid coordinates (floor division).
    /// </summary>
    public Vector2Int WorldToGrid(Vector3 worldPos, Vector3 chunkOrigin)
    {
        Vector3 local = worldPos - chunkOrigin;
        return new Vector2Int(
            Mathf.FloorToInt(local.x / CellSize),
            Mathf.FloorToInt(local.z / CellSize));
    }

    /// <summary>
    /// Checks if coordinates are within grid bounds.
    /// </summary>
    private bool IsValidCoordinate(Vector2Int coord)
    {
        return coord.x >= 0 && coord.x < Dimensions.x &&
               coord.y >= 0 && coord.y < Dimensions.y;
    }

    /// <summary>
    /// Finalizes the mutable grid into an immutable LayoutData DTO.
    /// Called by ChunkAssembler after generation is complete.
    /// </summary>
    internal LayoutData BuildLayoutData(
        Guid chunkId,
        BiomeDefinitionSO biomeDef,
        Vector3 worldOrigin,
        Vector2Int entryCell,
        Vector2Int exitCell,
        GenerationMetadata metadata)
    {
        var placedGrid = new PlacedCell[Dimensions.x, Dimensions.y];

        for (int x = 0; x < Dimensions.x; x++)
        {
            for (int z = 0; z < Dimensions.y; z++)
            {
                var cell = _cells[x, z];
                placedGrid[x, z] = new PlacedCell(
                    cell.PlacedObject,
                    cell.IsReserved,
                    cell.LocalRotation);
            }
        }

        return new LayoutData(
            chunkId,
            biomeDef,
            placedGrid,
            worldOrigin,
            entryCell,
            exitCell,
            metadata);
    }
}