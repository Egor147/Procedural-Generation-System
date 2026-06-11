using UnityEngine;
using System;

/// <summary>
/// Detects player entry/exit from chunk boundaries and notifies the stream manager.
/// Attach to a trigger collider at chunk edges.
/// </summary>
public class TriggerController : MonoBehaviour
{
    [Tooltip("Direction this trigger leads to (for determining next chunk position).")]
    [SerializeField] private Direction _exitDirection;

    /// <summary>
    /// Reference to stream manager. Must be assigned in inspector or via SetStreamManager().
    /// Cannot use FindObjectOfType because ChunkStreamManager is not a MonoBehaviour.
    /// </summary>
    [Tooltip("Assign ChunkStreamManager from your scene bootstrap (e.g., DemoSceneSetup).")]
    [SerializeField] private ChunkStreamManager _streamManager;

    [Tooltip("Tag required on player object to trigger events.")]
    [SerializeField] private string _playerTag = "Player";

    /// <summary>
    /// Event fired when player enters this trigger zone.
    /// </summary>
    public event Action<Direction> OnChunkEntered;

    /// <summary>
    /// Event fired when player exits this trigger zone.
    /// </summary>
    public event Action<Direction> OnChunkExited;

    /// <summary>
    /// Sets the stream manager reference programmatically.
    /// Use this if you prefer dependency injection over inspector assignment.
    /// </summary>
    public void SetStreamManager(ChunkStreamManager manager)
    {
        _streamManager = manager;
    }

    /// <summary>
    /// Sets the exit direction programmatically.
    /// </summary>
    public void SetExitDirection(Direction direction)
    {
        _exitDirection = direction;
    }

    public ChunkStreamManager GetStreamManager() { return _streamManager; }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag(_playerTag))
        {
            OnChunkEntered?.Invoke(_exitDirection);

            // Optional: notify stream manager directly if event subscription is not used
            // _streamManager?.OnTriggerEntered(_exitDirection);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag(_playerTag))
        {
            OnChunkExited?.Invoke(_exitDirection);
        }
    }
}