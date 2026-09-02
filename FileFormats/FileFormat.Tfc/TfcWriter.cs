using System.Buffers.Binary;

namespace FileFormat.Tfc;

/// <summary>
/// Creates a Mass Effect TFC cache by emitting one stored bundle per added payload.
/// </summary>
/// <remarks>
/// All bundles are emitted with <c>CompressedSize == UncompressedSize</c> and each per-block
/// size-table slot duplicated (compressed-block-size == uncompressed-block-size). LZX block
/// compression is not implemented; this is a write-once-read-many "stored" producer suitable
/// for round-trip tooling and synthetic test fixtures.
/// </remarks>
public sealed class TfcWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<(byte[] Data, uint BlockSize)> _bundles = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="TfcWriter"/>.
  /// </summary>
  /// <param name="stream">The output stream; bundles are flushed on <see cref="Finish"/> or dispose.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TfcWriter(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Queues a stored bundle to be emitted on flush.
  /// </summary>
  /// <param name="uncompressedData">The raw bundle bytes; copied by reference (caller must not mutate after enqueue).</param>
  /// <param name="blockSize">Nominal block size; defaults to <see cref="TfcConstants.DefaultBlockSize"/>.</param>
  public void AddBundle(byte[] uncompressedData, uint blockSize = TfcConstants.DefaultBlockSize) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add bundles after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(uncompressedData);

    if (blockSize == 0)
      throw new ArgumentException("Block size must be positive.", nameof(blockSize));

    this._bundles.Add((uncompressedData, blockSize));
  }

  /// <summary>
  /// Writes all queued bundles to the stream and marks the writer as finished.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    Span<byte> header = stackalloc byte[TfcConstants.HeaderSize];
    Span<byte> tableSlot = stackalloc byte[8];

    foreach (var (data, blockSize) in this._bundles) {
      var totalSize = (uint)data.Length;

      BinaryPrimitives.WriteUInt32LittleEndian(header[..4],   TfcConstants.Magic);
      BinaryPrimitives.WriteUInt32LittleEndian(header[4..8],  blockSize);
      BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], totalSize);
      BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], totalSize);
      this._stream.Write(header);

      var remaining = totalSize;
      while (remaining > 0) {
        var thisBlock = Math.Min(blockSize, remaining);
        BinaryPrimitives.WriteUInt32LittleEndian(tableSlot[..4],  thisBlock);
        BinaryPrimitives.WriteUInt32LittleEndian(tableSlot[4..8], thisBlock);
        this._stream.Write(tableSlot);
        remaining -= thisBlock;
      }

      if (data.Length > 0)
        this._stream.Write(data);
    }
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
