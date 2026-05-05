using System.Buffers.Binary;

namespace FileFormat.Mix;

/// <summary>
/// Creates a Westwood TD/RA1 MIX archive (no encryption, no checksum).
/// </summary>
/// <remarks>
/// On <see cref="Finish"/>, the writer:
/// <list type="number">
///   <item>Computes the Westwood ID hash for each entry's filename.</item>
///   <item>Sorts the directory ascending by ID (the game expects this for binary-search lookup).</item>
///   <item>Writes header (6 bytes) + directory (12 × N) + concatenated payloads.</item>
/// </list>
/// </remarks>
public sealed class MixWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(string Name, uint Id, byte[] Data)> _entries = [];
  private readonly HashSet<uint> _ids = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="MixWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the MIX archive to. Must be writable and seekable.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public MixWriter(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;

    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
  }

  /// <summary>
  /// Adds an entry to the archive.
  /// </summary>
  /// <param name="name">The entry's original filename. Used to compute the Westwood ID hash.</param>
  /// <param name="data">The raw entry payload.</param>
  public void AddEntry(string name, byte[] data) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var id = WestwoodCrc.Hash(name);
    if (!this._ids.Add(id))
      throw new InvalidOperationException(
        $"Duplicate Westwood ID 0x{id:X8} for entry '{name}'. MIX archives cannot contain two entries with the same hash.");

    this._entries.Add((name, id, data));
  }

  /// <summary>
  /// Writes the MIX archive to the stream and finishes writing.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    var sorted = this._entries.OrderBy(e => e.Id).ToList();

    long bodySize = 0;
    foreach (var (_, _, data) in sorted)
      bodySize += data.Length;

    if (bodySize > uint.MaxValue)
      throw new InvalidOperationException($"MIX body size {bodySize} exceeds UInt32.MaxValue.");
    if (sorted.Count > MixConstants.MaxFileCount)
      throw new InvalidOperationException($"MIX file count {sorted.Count} exceeds UInt16.MaxValue ({MixConstants.MaxFileCount}).");

    Span<byte> header = stackalloc byte[MixConstants.HeaderSize];
    BinaryPrimitives.WriteUInt16LittleEndian(header[..2], (ushort)sorted.Count);
    BinaryPrimitives.WriteUInt32LittleEndian(header[2..6], (uint)bodySize);
    this._stream.Write(header);

    Span<byte> entryBuf = stackalloc byte[MixConstants.DirectoryEntrySize];
    uint runningOffset = 0;
    foreach (var (_, id, data) in sorted) {
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[0..4], id);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[4..8], runningOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(entryBuf[8..12], (uint)data.Length);
      this._stream.Write(entryBuf);
      runningOffset += (uint)data.Length;
    }

    foreach (var (_, _, data) in sorted) {
      if (data.Length > 0)
        this._stream.Write(data);
    }
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
