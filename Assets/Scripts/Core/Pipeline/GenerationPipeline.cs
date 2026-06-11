using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Manages async/sync generation requests. Routes tasks to background threads or main thread based on config.
/// </summary>
public class GenerationPipeline
{
    private readonly GenerationProfileSO _config;
    private readonly Func<GenerationRequest, LayoutData> _generationFunc;

    public GenerationPipeline(GenerationProfileSO config, Func<GenerationRequest, LayoutData> generationFunc)
    {
        _config = config;
        _generationFunc = generationFunc;
    }

    /// <summary>
    /// Schedules a generation task. Returns immediately; result delivered via callback on main thread.
    /// </summary>
    public void ScheduleGeneration(GenerationRequest request, Action<LayoutData> onComplete)
    {
        if (_config.EnableMultithreading)
        {
            // Run generation on background thread
            Task.Run(() =>
            {
                try
                {
                    var result = _generationFunc(request);
                    MainThreadDispatcher.Enqueue(() => onComplete?.Invoke(result));
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"Generation failed: {ex.Message}");
                    MainThreadDispatcher.Enqueue(() => onComplete?.Invoke(default));
                }
            });
        }
        else
        {
            // Run synchronously on main thread (for debugging or baseline tests)
            try
            {
                var result = _generationFunc(request);
                onComplete?.Invoke(result);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"Generation failed: {ex.Message}");
                onComplete?.Invoke(default);
            }
        }
    }
}

/// <summary>
/// Input parameters for a single chunk generation request.
/// </summary>
public readonly struct GenerationRequest
{
    public Guid ChunkId { get; }
    public BiomeDefinitionSO Biome { get; }
    public Vector3 WorldOrigin { get; }
    public Vector2Int EntryCell { get; }
    public Vector2Int ExitCell { get; }
    public int Seed { get; }

    public GenerationRequest(Guid chunkId, BiomeDefinitionSO biome, Vector3 origin, int seed)
    {
        ChunkId = chunkId;
        Biome = biome;
        WorldOrigin = origin;
        EntryCell = Vector2Int.zero;
        ExitCell = Vector2Int.zero;
        Seed = seed;


        foreach (var anchor in Biome.ConnectionAnchors)
        {
            if (anchor.ConnectionDirection == Direction.Forward)
            {
                ExitCell = anchor.GridPosition;
            }
            else if (anchor.ConnectionDirection == Direction.Back)
            {
                EntryCell = anchor.GridPosition;
            }
        }
    }
}