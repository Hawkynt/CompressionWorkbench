using FileFormat.Arc;

namespace FileFormat.Pak;

/// <summary>
/// Creates PAK archives. PAK is ARC-compatible (same binary layout).
/// Delegates to <see cref="ArcWriter"/> for all operations.
/// </summary>
public sealed class PakWriter : IDisposable {
  private readonly ArcWriter _inner;

  /// <summary>
  /// Creates a new PAK archive writer.
  /// </summary>
  public PakWriter(Stream stream) => this._inner = new ArcWriter(stream);

  /// <summary>Adds a file entry.</summary>
  public void AddEntry(string fileName, byte[] data) => this._inner.AddEntry(fileName, data);

  /// <summary>Writes the archive end marker.</summary>
  public void Finish() => this._inner.Finish();

  /// <inheritdoc />
  public void Dispose() => this._inner.Dispose();
}
