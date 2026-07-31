using UnityEngine;

/// <summary>
/// Handles pickup detection for loot objects and notifies the parent chunk
/// so the collection can be persisted in the chunk delta.
/// 
/// The loot ID is assigned externally at spawn time and is deterministic
/// (derived from cell coordinates and chunk seed), which means the same
/// piece of loot will always receive the same ID regardless of how many
/// times the chunk is regenerated. This is what makes the "collected"
/// state survive chunk reloads.
/// </summary>
[RequireComponent(typeof(Collider))]
public class LootItem : MonoBehaviour
{
    [Tooltip("Unique ID of this loot within its chunk. Set by Chunk at spawn time.")]
    [SerializeField] private string _lootId;

    // Reference to the chunk that owns this loot. Used to push the
    // "collected" event into the chunk's delta when the player picks it up.
    private Chunk _parentChunk;

    public string LootId => _lootId;

    /// <summary>
    /// Called by Chunk immediately after instantiating this prefab.
    /// Wires up the ID and the parent chunk reference, and makes sure
    /// the collider is configured as a trigger so pickup actually fires.
    /// </summary>
    public void Initialize(string lootId, Chunk parentChunk)
    {
        _lootId = lootId;
        _parentChunk = parentChunk;

        // Defensive check: if someone forgot to tick "is trigger" on the
        // prefab collider, we fix it here. Without this, OnTriggerEnter
        // will never fire and the loot will be uncollectable.
        var collider = GetComponent<Collider>();
        if (collider != null)
        {
            collider.isTrigger = true;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other == null) return;
        if (!other.CompareTag("Player")) return;

        // Tell the chunk "this loot is gone" before we destroy ourselves.
        // The chunk writes the ID into its ChunkDelta, which is what gets
        // saved to cache when the chunk unloads.
        if (_parentChunk != null)
        {
            _parentChunk.MarkLootCollected(_lootId);
        }

        Destroy(gameObject);
    }
}