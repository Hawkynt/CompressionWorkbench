namespace FileFormat.TfRecord;

/// <summary>
/// Represents a single record in a TFRecord file.
/// </summary>
public sealed class TfRecordEntry {
  /// <summary>Gets the synthesized record name (e.g. "record_00000.bin").</summary>
  public string Name { get; init; } = "";

  /// <summary>Gets the absolute file offset where the record's data payload begins (past length+length-CRC).</summary>
  public long Offset { get; init; }

  /// <summary>Gets the size of the record's data payload in bytes.</summary>
  public long Size { get; init; }

  /// <summary>Gets a value indicating whether either the length-CRC or data-CRC failed validation.</summary>
  public bool IsCorrupt { get; init; }
}
