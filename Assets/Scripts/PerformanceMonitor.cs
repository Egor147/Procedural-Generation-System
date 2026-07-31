using UnityEngine;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;

/// <summary>
/// Collects performance metrics during runtime and saves them to CSV.
/// Designed to run unobtrusively in the background during test scenarios.
/// Data is written to disk on demand (via key press or explicit call)
/// rather than continuously, to avoid I/O overhead during measurement.
/// </summary>
public class PerformanceMonitor : MonoBehaviour
{
    [Header("Sampling")]
    [Tooltip("How often to record a sample in seconds. 0 = every frame.")]
    [SerializeField] private float _sampleInterval = 0.1f;

    [Tooltip("Maximum number of samples to keep in memory. Older samples are discarded.")]
    [SerializeField] private int _maxSamples = 3600;

    [Header("Streaming Integration")]
    [Tooltip("Reference to ChunkStreamManager for tracking load/unload events.")]
    private ChunkStreamManager _streamManager;
    
    
    [SerializeField] private DemoSceneSetup _sceneSetup;

    [Header("Export")]
    [Tooltip("Press this key to export collected data to CSV.")]
    [SerializeField] private KeyCode _exportKey = KeyCode.F8;

    // Ring buffer for samples. Fixed-size array to avoid GC pressure.
    private readonly PerformanceSample[] _samples;
    private int _writeIndex;
    private int _sampleCount;
    private float _lastSampleTime;

    // Event handlers cached to avoid allocations.
    private System.Action<Vector2Int> _onChunkLoaded;
    private System.Action<Vector2Int> _onChunkUnloaded;

    public int SampleCount => _sampleCount;

    public PerformanceMonitor()
    {
        _samples = new PerformanceSample[_maxSamples > 0 ? _maxSamples : 3600];
    }

    private void Awake()
    {
        _writeIndex = 0;
        _sampleCount = 0;
        _lastSampleTime = 0f;

        _onChunkLoaded = coord => RecordEvent($"Chunk loaded: {coord}");
        _onChunkUnloaded = coord => RecordEvent($"Chunk unloaded: {coord}");
    }

    private void Start()
    {
        if (_streamManager == null)
        {
            _streamManager = _sceneSetup.streamManager;
            _streamManager.OnChunkLoaded += _onChunkLoaded;
            _streamManager.OnChunkUnloaded += _onChunkUnloaded;
        }
    }

    private void OnDisable()
    {
        if (_streamManager != null)
        {
            _streamManager.OnChunkLoaded -= _onChunkLoaded;
            _streamManager.OnChunkUnloaded -= _onChunkUnloaded;
        }
    }

    private void Update()
    {
        if (_sampleInterval > 0f && Time.time - _lastSampleTime < _sampleInterval)
        {
            return;
        }
        _lastSampleTime = Time.time;

        RecordSample();

        if (Input.GetKeyDown(_exportKey))
        {
            ExportToCsv();
        }
    }

    private void RecordSample()
    {
        var sample = new PerformanceSample
        {
            Time = Time.time,
            Fps = 1f / Mathf.Max(0.001f, Time.unscaledDeltaTime),
            TotalMemoryMb = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / (1024f * 1024f),
            MonoMemoryMb = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() / (1024f * 1024f),
            GcMemoryMb = System.GC.GetTotalMemory(false) / (1024f * 1024f),
            ActiveChunks = _streamManager != null ? _streamManager.ActiveChunkCount : 0,
            EventType = SampleEventType.None,
            EventLabel = string.Empty
        };

        _samples[_writeIndex] = sample;
        _writeIndex = (_writeIndex + 1) % _samples.Length;
        if (_sampleCount < _samples.Length) _sampleCount++;
    }

    private void RecordEvent(string label)
    {
        if (_sampleCount == 0) return;

        int latestIndex = (_writeIndex - 1 + _samples.Length) % _samples.Length;
        _samples[latestIndex].EventType = SampleEventType.Streaming;
        _samples[latestIndex].EventLabel = label;
    }

    /// <summary>
    /// Exports all collected samples to a CSV file in the TestLogs directory.
    /// Uses InvariantCulture to ensure decimal separators are dots regardless
    /// of system locale, which prevents Excel from misinterpreting numbers.
    /// </summary>
    public void ExportToCsv()
    {
        string logsDir = Path.Combine(Application.dataPath, "..", "TestLogs");
        if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);

        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string filePath = Path.Combine(logsDir, $"performance_{timestamp}.csv");

        var culture = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Time,Fps,TotalMemoryMb,MonoMemoryMb,GcMemoryMb,ActiveChunks,EventType,EventLabel");

        foreach (var s in GetSamplesChronological())
        {
            string eventType = s.EventType.ToString();
            string label = s.EventLabel?.Replace("\"", "\"\"") ?? string.Empty;

            sb.AppendLine(string.Format(culture,
                "{0:F3},{1:F2},{2:F2},{3:F2},{4:F2},{5},{6},\"{7}\"",
                s.Time, s.Fps, s.TotalMemoryMb, s.MonoMemoryMb,
                s.GcMemoryMb, s.ActiveChunks, eventType, label));
        }

        File.WriteAllText(filePath, sb.ToString());
        Debug.Log($"[PerformanceMonitor] Exported {_sampleCount} samples to: {filePath}");
    }

    private IEnumerable<PerformanceSample> GetSamplesChronological()
    {
        if (_sampleCount == 0) yield break;

        int startIndex = _sampleCount < _samples.Length ? 0 : _writeIndex;

        for (int i = 0; i < _sampleCount; i++)
        {
            int index = (startIndex + i) % _samples.Length;
            yield return _samples[index];
        }
    }

    public void Clear()
    {
        _writeIndex = 0;
        _sampleCount = 0;
        System.Array.Clear(_samples, 0, _samples.Length);
    }
}

public struct PerformanceSample
{
    public float Time;
    public float Fps;
    public float TotalMemoryMb;
    public float MonoMemoryMb;
    public float GcMemoryMb;
    public int ActiveChunks;
    public SampleEventType EventType;
    public string EventLabel;
}

public enum SampleEventType
{
    None,
    Streaming
}