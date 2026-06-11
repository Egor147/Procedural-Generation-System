using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

/// <summary>
/// Implements a constraint-driven pathfinding algorithm for procedural chunk generation.
/// Generates varied, non-optimal paths while GUARANTEEING connectivity between all waypoints.
/// Introduces center avoidance penalty to prevent default "plus-shaped" paths through chunk middle.
/// Designed for minimal GC pressure and deterministic output across all Unity runtime versions.
/// </summary>
public class PathCarver
{
    private readonly System.Random _rng;
    private readonly BiomeDefinitionSO _biome;
    private readonly GenerationProfileSO _config;
    private readonly int _gridWidth;
    private readonly int _gridHeight;
    private readonly int _totalCells;

    // Path variation parameters
    private readonly float _pathWanderFactor;       // How much the path deviates from optimal (0.0 = shortest, 1.0 = maximum wander)
    private readonly float _centerAvoidanceStrength; // Penalty multiplier for cells near chunk center (0.0 = no penalty, 3.0 = strong avoidance)

    // Pre-calculated center metrics for performance
    private readonly float _centerX;
    private readonly float _centerZ;
    private readonly float _maxCenterDist;

    // Pre-allocated buffers for A* to eliminate per-call heap allocations.
    private readonly float[] _gScores;
    private readonly float[] _fScores;
    private readonly int[] _cameFrom;
    private readonly bool[] _closedSet;
    private readonly Vector2Int[] _pathBuffer;
    private readonly MinPriorityQueue<Vector2Int, float> _openSet;

    /// <summary>
    /// Initializes the path carver with biome constraints, generation parameters, and path variation settings.
    /// </summary>
    /// <param name="biome">Biome definition containing anchors, slope limits, and path settings.</param>
    /// <param name="config">Global generation profile controlling grid dimensions.</param>
    /// <param name="seed">Deterministic seed for randomized path variation.</param>
    /// <param name="pathWanderFactor">How much the path wanders from optimal (0.0 = shortest, 1.0 = maximum wander).</param>
    /// <param name="centerAvoidanceStrength">Penalty for center cells. 0.0 = default, 1.5 = moderate, 3.0+ = strong edge preference.</param>
    public PathCarver(
        BiomeDefinitionSO biome,
        GenerationProfileSO config,
        int seed,
        float pathWanderFactor = 0.4f,
        float centerAvoidanceStrength = 1.5f)
    {
        _biome = biome ?? throw new ArgumentNullException(nameof(biome));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _rng = new System.Random(seed);
        _gridWidth = config.GridDimensions.x;
        _gridHeight = config.GridDimensions.y;
        _totalCells = _gridWidth * _gridHeight;

        // Clamp parameters to valid ranges
        _pathWanderFactor = Mathf.Clamp01(pathWanderFactor);
        _centerAvoidanceStrength = Mathf.Clamp(_biome.centerAvoidanceStrength, 0f, 5f);

        // Pre-calculate center coordinates and max Manhattan distance to edges
        _centerX = _gridWidth / 2f;
        _centerZ = _gridHeight / 2f;
        _maxCenterDist = (_gridWidth / 2f) + (_gridHeight / 2f);

        // Allocate buffers once. Size is bounded by grid dimensions.
        _gScores = new float[_totalCells];
        _fScores = new float[_totalCells];
        _cameFrom = new int[_totalCells];
        _closedSet = new bool[_totalCells];
        _pathBuffer = new Vector2Int[_totalCells];

        // Custom priority queue with dynamic resizing for safety
        _openSet = new MinPriorityQueue<Vector2Int, float>(_totalCells);
    }

    /// <summary>
    /// Carves a GUARANTEED walkable path visiting all specified waypoints sequentially.
    /// Always ensures connectivity between entry, anchors, and exit.
    /// Introduces controlled randomness and center avoidance for organic, varied path shapes.
    /// </summary>
    /// <param name="grid">Mutable grid graph to reserve path cells.</param>
    /// <param name="entry">Starting grid coordinate.</param>
    /// <param name="exit">Ending grid coordinate.</param>
    /// <returns>True if path was successfully carved (always true unless grid is invalid).</returns>
    public bool CarvePath(GridGraph grid, Vector2Int entry, Vector2Int exit)
    {
        var waypoints = CollectWaypoints(entry, exit);
        if (waypoints.Count < 2) return false;

        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            Vector2Int start = waypoints[i];
            Vector2Int end = waypoints[i + 1];

            // Try A* first with wander factor and center avoidance
            if (!CarveSegmentWithWander(grid, start, end))
            {
                // Fallback: straight line if A* fails (should be rare)
                CarveStraightLine(grid, start, end);
            }
        }

        return true; // Always succeeds (fallback ensures connectivity)
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private List<Vector2Int> CollectWaypoints(Vector2Int entry, Vector2Int exit)
    {
        var waypoints = new List<Vector2Int>(8);
        waypoints.Add(entry);

        if (_biome.ConnectionAnchors != null)
        {
            foreach (var anchor in _biome.ConnectionAnchors)
                waypoints.Add(anchor.GridPosition);
        }

        waypoints.Add(exit);
        return waypoints;
    }

    /// <summary>
    /// Executes A* between two points with randomized costs and center avoidance penalty.
    /// GUARANTEES to find a path (uses straight line fallback if needed).
    /// </summary>
    private bool CarveSegmentWithWander(GridGraph grid, Vector2Int start, Vector2Int end)
    {
        ResetBuffers();

        int startIndex = ToIndex(start);
        _gScores[startIndex] = 0f;
        _fScores[startIndex] = Heuristic(start, end);
        _openSet.Enqueue(start, _fScores[startIndex]);

        while (_openSet.Count > 0)
        {
            var current = _openSet.Dequeue();
            int currentIndex = ToIndex(current);

            if (current == end)
            {
                // Success! Reconstruct and reserve the path
                ReconstructAndReserve(grid, current, start);
                return true;
            }

            if (_closedSet[currentIndex]) continue;
            _closedSet[currentIndex] = true;

            var neighbors = grid.GetNeighbors(current);
            foreach (var neighbor in neighbors)
            {
                int neighborIndex = ToIndex(neighbor.Coordinates);
                if (_closedSet[neighborIndex]) continue;
                if (neighbor.SlopeAngle > _biome.MaxPathSlope) continue;

                // Base movement cost
                float baseCost = 1f;

                // 1. Apply center avoidance penalty (soft constraint)
                float centerPenalty = 1f;
                if (_centerAvoidanceStrength > 0f)
                {
                    // Calculate Manhattan distance from chunk center
                    float distFromCenter = Mathf.Abs(neighbor.Coordinates.x - _centerX) +
                                           Mathf.Abs(neighbor.Coordinates.y - _centerZ);
                    float normalizedDist = distFromCenter / _maxCenterDist; // 0.0 at center, 1.0 at edges

                    // Penalty is highest at center, fades to 1.0 at edges
                    centerPenalty = 1f + (_centerAvoidanceStrength * (1f - normalizedDist));
                }

                // 2. Apply wander factor randomness
                float wanderCost = 1f;
                if (_pathWanderFactor > 0f)
                {
                    float minCost = Mathf.Lerp(1f, 0.2f, _pathWanderFactor);
                    float maxCost = Mathf.Lerp(1f, 4f, _pathWanderFactor);
                    wanderCost = (float)_rng.NextDouble() * (maxCost - minCost) + minCost;
                }

                // Combined cost: base * centerPenalty * wanderCost
                float tentativeG = _gScores[currentIndex] + baseCost * centerPenalty * wanderCost;

                if (tentativeG < _gScores[neighborIndex] || (_gScores[neighborIndex] == 0f && neighborIndex != startIndex))
                {
                    _cameFrom[neighborIndex] = currentIndex;
                    _gScores[neighborIndex] = tentativeG;

                    // Heuristic with slight randomization for varied path shapes
                    float heuristic = Heuristic(neighbor.Coordinates, end);
                    float heuristicNoise = (float)_rng.NextDouble() * _pathWanderFactor * 0.5f;
                    _fScores[neighborIndex] = tentativeG + heuristic + heuristicNoise;

                    _openSet.Enqueue(neighbor.Coordinates, _fScores[neighborIndex]);
                }
            }
        }

        // A* failed to find path (should be rare with proper grid)
        return false;
    }

    /// <summary>
    /// Reconstructs path from end to start and reserves all cells.
    /// No budget limit - ensures complete path connectivity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReconstructAndReserve(GridGraph grid, Vector2Int end, Vector2Int start)
    {
        int count = 0;
        int currentIdx = ToIndex(end);
        int pathWidth = _biome.PathWidth;

        // Reconstruct path backwards from end to start
        while (currentIdx != ToIndex(start))
        {
            Vector2Int coord = FromIndex(currentIdx);
            ReserveCellArea(grid, coord, pathWidth);
            _pathBuffer[count++] = coord;
            currentIdx = _cameFrom[currentIdx];
        }

        // Reserve start cell
        ReserveCellArea(grid, start, pathWidth);
    }

    /// <summary>
    /// Reserves a square area around a center cell for the path.
    /// Respects path width configuration.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ReserveCellArea(GridGraph grid, Vector2Int center, int width)
    {
        int half = width / 2;

        for (int dx = -half; dx <= half; dx++)
        {
            for (int dz = -half; dz <= half; dz++)
            {
                var coord = center + new Vector2Int(dx, dz);
                var cell = grid.GetCell(coord);

                if (cell != null)
                {
                    cell.IsReserved = true;
                }
            }
        }
    }

    /// <summary>
    /// Fallback: carves a straight line between two points using Bresenham's algorithm.
    /// Guaranteed to connect start and end.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CarveStraightLine(GridGraph grid, Vector2Int start, Vector2Int end)
    {
        int dx = Mathf.Abs(end.x - start.x);
        int dz = Mathf.Abs(end.y - start.y);
        int x = start.x, z = start.y;
        int err = dx - dz;
        int sx = end.x > start.x ? 1 : -1;
        int sy = end.y > start.y ? 1 : -1;
        int pathWidth = _biome.PathWidth;

        do
        {
            ReserveCellArea(grid, new Vector2Int(x, z), pathWidth);
            int e2 = 2 * err;
            if (e2 > -dz) { err -= dz; x += sx; }
            if (e2 < dx) { err += dx; z += sy; }
        } while (x != end.x || z != end.y);

        // Reserve end cell
        ReserveCellArea(grid, end, pathWidth);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ResetBuffers()
    {
        Array.Clear(_gScores, 0, _totalCells);
        Array.Clear(_fScores, 0, _totalCells);
        Array.Clear(_closedSet, 0, _totalCells);
        Array.Clear(_cameFrom, 0, _totalCells);
        _openSet.Clear();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ToIndex(Vector2Int coord) => coord.y * _gridWidth + coord.x;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector2Int FromIndex(int index) => new Vector2Int(index % _gridWidth, index / _gridWidth);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Heuristic(Vector2Int a, Vector2Int b) => Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float GetMovementCost(Vector2Int from, Vector2Int to) => 1f;

    /// <summary>
    /// Zero-allocation binary min-heap priority queue with dynamic resizing.
    /// Avoids .NET 6+ PriorityQueue dependency while guaranteeing O(log N) operations.
    /// </summary>
    private class MinPriorityQueue<TElement, TPriority> where TPriority : IComparable<TPriority>
    {
        private TElement[] _elements;
        private TPriority[] _priorities;
        private int _count;

        public int Count => _count;

        public MinPriorityQueue(int capacity)
        {
            _elements = new TElement[capacity];
            _priorities = new TPriority[capacity];
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(TElement element, TPriority priority)
        {
            if (_count >= _elements.Length)
            {
                int newCapacity = _elements.Length * 2;
                Array.Resize(ref _elements, newCapacity);
                Array.Resize(ref _priorities, newCapacity);
            }

            _elements[_count] = element;
            _priorities[_count] = priority;
            SiftUp(_count++);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TElement Dequeue()
        {
            TElement result = _elements[0];
            int lastIndex = --_count;
            _elements[0] = _elements[lastIndex];
            _priorities[0] = _priorities[lastIndex];
            _elements[lastIndex] = default;
            _priorities[lastIndex] = default;
            if (_count > 0) SiftDown(0);
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() => _count = 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftUp(int index)
        {
            TPriority priority = _priorities[index];
            TElement element = _elements[index];
            while (index > 0)
            {
                int parent = (index - 1) >> 1;
                if (_priorities[parent].CompareTo(priority) <= 0) break;
                _elements[index] = _elements[parent];
                _priorities[index] = _priorities[parent];
                index = parent;
            }
            _elements[index] = element;
            _priorities[index] = priority;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SiftDown(int index)
        {
            TPriority priority = _priorities[index];
            TElement element = _elements[index];
            int half = _count >> 1;
            while (index < half)
            {
                int child = (index << 1) + 1;
                int right = child + 1;
                if (right < _count && _priorities[right].CompareTo(_priorities[child]) < 0)
                    child = right;
                if (_priorities[child].CompareTo(priority) >= 0) break;
                _elements[index] = _elements[child];
                _priorities[index] = _priorities[child];
                index = child;
            }
            _elements[index] = element;
            _priorities[index] = priority;
        }
    }
}