namespace Jrig.Controls;

/// <summary>Names of the custom drag-and-drop payloads used by the library and the board.</summary>
public static class DragFormats
{
    /// <summary>A CatalogEntry dragged from the pedal library (drop on the board to add a pedal).</summary>
    public const string Catalog = "jrig.catalog";

    /// <summary>A PedalViewModel dragged from the board (drop on the board to reorder, on the library to remove).</summary>
    public const string Pedal = "jrig.pedal";
}
