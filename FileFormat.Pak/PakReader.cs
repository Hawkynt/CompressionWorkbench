using FileFormat.Arc;

namespace FileFormat.Pak;

/// <summary>
/// Reads PAK archives. PAK is an ARC-compatible format (same binary layout).
/// Delegates to <see cref="ArcReader"/> for all operations.
/// </summary>
public sealed class PakReader : IDisposable {
  private readonly ArcReader _inner;

  /// <summary>
  /// Reads a PAK archive from a stream.
  /// </summary>
  public PakReader(Stream stream) => this._inner = new ArcReader(stream);

  /// <summary>Gets the next entry, or null if no more entries.</summary>
  public ArcEntry? GetNextEntry() => this._inner.GetNextEntry();

  /// <summary>Reads the data of the current entry.</summary>
  public byte[] ReadEntryData() => this._inner.ReadEntryData();

  /// <inheritdoc />
  public void Dispose() => this._inner.Dispose();
}
