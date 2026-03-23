namespace Compression.UI.Controls;

/// <summary>
/// View model for a single hex dump line, supporting per-byte coloring.
/// </summary>
internal sealed class HexLineViewModel {
  public required string OffsetText { get; init; }
  public required HexByte[] Bytes { get; init; }
  public required int ByteCount { get; init; }
  public required int BytesPerRow { get; init; }
}

/// <summary>
/// Represents a single byte in the hex display with precomputed text.
/// </summary>
internal readonly record struct HexByte(byte Value, string HexText, char AsciiChar);
