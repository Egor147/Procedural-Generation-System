using UnityEngine;

/// <summary>
/// Mutable cell state used during generation pipeline.
/// Represents a single grid slot with placement data and reservation flag.
/// </summary>
public class Cell
{
    /// <summary>
    /// Grid coordinates of this cell.
    /// </summary>
    public Vector2Int Coordinates { get; }

    /// <summary>
    /// Height value for 2.5D terrain support.
    /// </summary>
    public float Height { get; set; }

    /// <summary>
    /// If true, this cell is reserved for the guaranteed player path.
    /// FillPlacer will never place obstacles here.
    /// </summary>
    public bool IsReserved { get; set; }

    /// <summary>
    /// Reference to the object placed in this cell (null = empty).
    /// </summary>
    public SpawnableObjectSO PlacedObject { get; set; }

    /// <summary>
    /// Local rotation for the placed object.
    /// </summary>
    public Quaternion LocalRotation { get; set; }

    /// <summary>
    /// Slope angle relative to neighbors. Used by PathCarver to avoid steep climbs.
    /// </summary>
    public float SlopeAngle { get; set; }

    /// <summary>
    /// Creates a new empty cell at specified coordinates.
    /// </summary>
    public Cell(Vector2Int coordinates)
    {
        Coordinates = coordinates;
        Height = 0f;
        IsReserved = false;
        PlacedObject = null;
        LocalRotation = Quaternion.identity;
        SlopeAngle = 0f;
    }
}