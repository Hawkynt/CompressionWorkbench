namespace FileFormat.Rpm;

/// <summary>
/// A single index entry in an RPM header structure.
/// Each entry describes one tag: its type, byte offset into the store, and element count.
/// </summary>
/// <param name="Tag">The tag number identifying the field.</param>
/// <param name="Type">The data type code (see <see cref="RpmConstants"/> TypeXxx constants).</param>
/// <param name="Offset">Byte offset of the tag's data within the header store section.</param>
/// <param name="Count">Number of elements (strings, integers, etc.) stored for this tag.</param>
public sealed record RpmHeaderEntry(int Tag, int Type, int Offset, int Count);
