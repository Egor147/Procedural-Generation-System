using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Places non-path objects (obstacles, loot, decorations) into available grid cells.
/// Supports IsWalkable objects that can be placed on reserved path cells.
/// Respects object-specific placement rules, weighted random selection, local collision checks,
/// and biome-level placement density for natural variation in clutter density.
/// </summary>
public class FillPlacer
{
    private readonly System.Random _rng;
    private readonly GenerationProfileSO _config;
    private readonly BiomeDefinitionSO _biome;

    /// <summary>
    /// Initializes the placer with biome data, generation config, and deterministic seed.
    /// </summary>
    public FillPlacer(BiomeDefinitionSO biome, GenerationProfileSO config, int seed)
    {
        _biome = biome;
        _config = config;
        _rng = new System.Random(seed);
    }

    /// <summary>
    /// Main placement loop. Iterates available cells and attempts to place compatible objects.
    /// Respects biome placement density: cells may be intentionally left empty for natural variation.
    /// Supports nested object placement and weighted random selection.
    /// </summary>
    public void PlaceObjects(GridGraph grid)
    {
        var availableCells = GetAllAvailableCells(grid);
        Shuffle(availableCells);

        foreach (var cell in availableCells)
        {
            // CRITICAL: Biome-level density check - skip placement with (1 - density) probability
            // This creates natural "breathing room" and prevents over-cluttered levels
            if (!ShouldPlaceInCell())
                continue;

            var candidates = GetCompatibleObjects(cell, grid);
            if (candidates.Length == 0) continue;

            var chosen = SelectWeightedRandom(candidates);
            if (chosen == null) continue;

            if (TryPlaceObject(grid, cell, chosen))
            {
                // Place nested/child objects if the parent object defines spawn rules
                if (chosen.NestedSpawnRules?.Length > 0)
                {
                    PlaceNestedObjects(grid, cell, chosen);
                }
            }
        }
    }

    /// <summary>
    /// Determines whether to attempt placement in a cell based on biome density settings.
    /// Returns true with probability equal to BiomeDefinitionSO.PlacementDensity (0.0 to 1.0).
    /// If density is not set, defaults to 1.0 (always attempt placement).
    /// </summary>
    private bool ShouldPlaceInCell()
    {
        // If biome doesn't define density, default to always placing
        float density = _biome.PlacementDensity > 0f ? _biome.PlacementDensity : 1.0f;

        // Clamp to valid range for safety
        density = Mathf.Clamp01(density);

        // Roll for placement: higher density = more likely to place
        return (float)_rng.NextDouble() <= density;
    }

    /// <summary>
    /// Returns all cells that are eligible for object placement.
    /// Includes both free cells and reserved path cells if the object is walkable.
    /// </summary>
    private List<Cell> GetAllAvailableCells(GridGraph grid)
    {
        var available = new List<Cell>();

        for (int x = 0; x < grid.Dimensions.x; x++)
        {
            for (int z = 0; z < grid.Dimensions.y; z++)
            {
                var cell = grid.GetCell(new Vector2Int(x, z));
                if (cell != null)
                {
                    // Always include non-reserved cells
                    if (!cell.IsReserved)
                    {
                        available.Add(cell);
                    }
                    //===========ÒÓÒ ÏÎÇÂÎËßÅÌ ÑÒÀÂÈÒÜ ÍÀ ÏÓÒßÕ!!!===============================//
                    // Include reserved cells ONLY if we have walkable objects that could use them
                    //else if (_biome.AllowedObjects?.Any(obj => obj.IsWalkable) == true)
                    //{
                    //    available.Add(cell);
                    //}
                }
            }
        }
        return available;
    }

    /// <summary>
    /// Filters objects by placement tags, bounds compatibility, and walkability rules.
    /// </summary>
    private SpawnableObjectSO[] GetCompatibleObjects(Cell cell, GridGraph grid)
    {
        return _biome.AllowedObjects
            .Where(obj => IsCompatibleWithCell(obj, cell, grid))
            .ToArray();
    }

    /// <summary>
    /// Checks if an object can be placed in a specific cell.
    /// Handles walkable objects that can overlap reserved path cells.
    /// </summary>
    private bool IsCompatibleWithCell(SpawnableObjectSO obj, Cell cell, GridGraph grid)
    {
        var boundsInCells = GetBoundsInCells(obj.LocalBounds, _config.CellSize);

        // Check if object footprint fits within grid boundaries
        if (!FitsInGrid(cell.Coordinates, boundsInCells, grid.Dimensions))
            return false;

        // Check for collisions with other objects or invalid cells
        if (OverlapsInvalidCell(cell, obj, grid))
            return false;

        return true;
    }

    /// <summary>
    /// Converts world-space bounds to grid-cell footprint dimensions.
    /// </summary>
    private Vector2Int GetBoundsInCells(Bounds localBounds, float cellSize)
    {
        return new Vector2Int(
            Mathf.CeilToInt(localBounds.size.x / cellSize),
            Mathf.CeilToInt(localBounds.size.z / cellSize));
    }

    /// <summary>
    /// Checks if an object's footprint fits within grid boundaries.
    /// </summary>
    private bool FitsInGrid(Vector2Int center, Vector2Int footprint, Vector2Int gridDims)
    {
        int halfX = footprint.x / 2;
        int halfZ = footprint.y / 2;
        return center.x - halfX >= 0 && center.x + halfX < gridDims.x &&
               center.y - halfZ >= 0 && center.y + halfZ < gridDims.y;
    }

    /// <summary>
    /// Checks for collisions with invalid cells.
    /// Walkable objects can overlap reserved path cells; non-walkable objects cannot.
    /// No object can overlap an already placed object.
    /// </summary>
    private bool OverlapsInvalidCell(Cell centerCell, SpawnableObjectSO obj, GridGraph grid)
    {
        var footprint = GetBoundsInCells(obj.LocalBounds, _config.CellSize);
        int halfX = footprint.x / 2;
        int halfZ = footprint.y / 2;

        for (int dx = -halfX; dx <= halfX; dx++)
        {
            for (int dz = -halfZ; dz <= halfZ; dz++)
            {
                var checkCoord = centerCell.Coordinates + new Vector2Int(dx, dz);
                var checkCell = grid.GetCell(checkCoord);

                if (checkCell == null)
                    return true; // Out of bounds = invalid placement

                // CRITICAL: Walkable objects can be placed on reserved path cells
                // Non-walkable objects (obstacles) cannot block the path
                if (checkCell.IsReserved && !obj.IsWalkable)
                    return true;

                // No two objects can occupy the same cell, regardless of walkability
                if (checkCell.PlacedObject != null)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Weighted random selection based on SpawnWeight property.
    /// Implements roulette wheel selection for probabilistic object choice.
    /// </summary>
    private SpawnableObjectSO SelectWeightedRandom(SpawnableObjectSO[] candidates)
    {
        if (candidates.Length == 0) return null;

        float totalWeight = candidates.Sum(c => c.SpawnWeight);
        if (totalWeight <= 0f) return candidates[0]; // Fallback if all weights are zero

        float roll = (float)_rng.NextDouble() * totalWeight;

        float cumulative = 0f;
        foreach (var candidate in candidates)
        {
            cumulative += candidate.SpawnWeight;
            if (roll <= cumulative)
                return candidate;
        }
        return candidates[candidates.Length - 1];
    }

    /// <summary>
    /// Attempts to place an object in a cell with random 90-degree rotation variance.
    /// </summary>
    private bool TryPlaceObject(GridGraph grid, Cell cell, SpawnableObjectSO obj)
    {
        cell.PlacedObject = obj;
        // Random rotation: 0, 90, 180, or 270 degrees around Y-axis
        cell.LocalRotation = Quaternion.Euler(0, _rng.Next(0, 4) * 90f, 0);
        return true;
    }

    /// <summary>
    /// Recursively places child objects within a parent's neighborhood.
    /// Respects walkability rules for nested objects as well.
    /// </summary>
    private void PlaceNestedObjects(GridGraph grid, Cell parentCell, SpawnableObjectSO parentObj)
    {
        foreach (var rule in parentObj.NestedSpawnRules)
        {
            int placed = 0;
            var candidates = rule.ChildCandidates.ToList();
            Shuffle(candidates);

            while (placed < rule.MaxChildren && candidates.Count > 0)
            {
                var child = candidates[_rng.Next(candidates.Count)];
                var neighbors = grid.GetNeighbors(parentCell.Coordinates);

                foreach (var neighbor in neighbors)
                {
                    // Apply same walkability logic to nested objects
                    if (neighbor.PlacedObject == null &&
                        (child.IsWalkable || !neighbor.IsReserved))
                    {
                        neighbor.PlacedObject = child;
                        neighbor.LocalRotation = Quaternion.Euler(0, _rng.Next(0, 4) * 90f, 0);
                        placed++;
                        break;
                    }
                }
                candidates.Remove(child);
            }
        }
    }

    /// <summary>
    /// Fisher-Yates shuffle for in-place list randomization.
    /// Ensures uniform distribution for placement order.
    /// </summary>
    private void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _rng.Next(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}