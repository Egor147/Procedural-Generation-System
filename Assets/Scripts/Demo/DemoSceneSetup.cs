using UnityEngine;
using System;

/// <summary>
/// Minimal MonoBehaviour to bootstrap generation and visualize a single chunk in Editor.
/// Attach to an empty GameObject in a test scene.
/// </summary>
public class DemoSceneSetup : MonoBehaviour
{
    [Header("Configuration")]
    [SerializeField] private GenerationProfileSO _profile;
    [SerializeField] private BiomeDefinitionSO _testBiome;

    [Header("Runtime")]
    [SerializeField] private Chunk _chunkPrefab;
    [SerializeField] private Transform _spawnPoint;

    private ChunkStreamManager _streamManager;
    private GenerationPipeline _pipeline;
    private ChunkCache _cache;
    private Transform _playerTransform;

    [Header("Debug Visualization")]
    [Tooltip("Enable to visualize paths, anchors, and grid structure during Play Mode")]
    [SerializeField] private bool _enableDebugGizmos;

    /// <summary>
    /// Global toggle for debug visualization. Accessed by ChunkPathVisualizer.
    /// </summary>
    public static bool EnableDebugGizmos { get; private set; }
    private void Awake()
    {
        // Ensure dispatcher exists
        _ = MainThreadDispatcher.Instance;

        // Initialize cache
        _cache = new ChunkCache(maxEntries: _profile.ActiveChunkRadius * 4);

        // Setup pipeline with generation function
        _pipeline = new GenerationPipeline(_profile, GenerateChunk);

        // Setup stream manager
        _streamManager = new ChunkStreamManager(_profile, _pipeline, _chunkPrefab, _cache);


        _playerTransform = FindAnyObjectByType<SimplePlayerController>()?.transform;
        if (_playerTransform == null)
        {
            Debug.LogError("No SimplePlayerController found in scene! Streaming won't work.");
        }

        EnableDebugGizmos = _enableDebugGizmos;
    }

    private void Start()
    {
        Vector2Int initialCoord = new Vector2Int(0, 0);

        var request = new GenerationRequest(
            Guid.NewGuid(),
            _testBiome,
            Vector3.zero,
            _profile.RandomSeed != 0 ? _profile.RandomSeed : UnityEngine.Random.Range(int.MinValue, int.MaxValue));

        _streamManager.RequestChunk(initialCoord, request, _spawnPoint.position);
    }

    /// <summary>
    /// Finds all TriggerController components and injects the stream manager reference.
    /// This replaces FindObjectOfType for non-MonoBehaviour dependencies.
    /// </summary>
    private void InjectStreamManagerIntoTriggers()
    {
        var triggers = FindObjectsByType<TriggerController>(FindObjectsSortMode.None);
        foreach (var trigger in triggers)
        {
            // Only inject if not already assigned in inspector
            if (trigger.GetComponent<TriggerController>().GetStreamManager() == null)
            {
                trigger.SetStreamManager(_streamManager);
            }
        }
    }

    /// <summary>
    /// Core generation function passed to pipeline. Pure C# logic.
    /// </summary>
    private LayoutData GenerateChunk(GenerationRequest req)
    {
        // Build grid
        var grid = new GridGraph(_profile.GridDimensions, _profile.CellSize);

        // Carve guaranteed path
        var pathCarver = new PathCarver(req.Biome, _profile, req.Seed);
        pathCarver.CarvePath(grid, req.EntryCell, req.ExitCell);

        // Fill with objects
        var fillPlacer = new FillPlacer(req.Biome, _profile, req.Seed);
        fillPlacer.PlaceObjects(grid);

        // Assemble final DTO
        var assembler = new ChunkAssembler();
        return assembler.Assemble(
            grid,
            req.ChunkId,
            req.Biome,
            req.WorldOrigin,
            req.EntryCell,
            req.ExitCell,
            req.Seed,
            placementAttempts: 0);
    }

    private void Update()
    {
        // Call exactly ONCE per frame with the final player position
        Vector3 targetPos = _playerTransform != null ? _playerTransform.position : _spawnPoint.position;
        _streamManager.UpdatePlayerPosition(targetPos);
    }

    private void OnValidate()
    {
        // Sync inspector value to static property in real-time
        EnableDebugGizmos = _enableDebugGizmos;
    }

    private void OnDestroy()
    {
        _streamManager?.UnloadAll();
    }
}