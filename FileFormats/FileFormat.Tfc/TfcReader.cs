using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.Tfc;

/// <summary>
/// Reads bundles from a Mass Effect Texture File Cache (.tfc) stream.
/// </summary>
/// <remarks>
/// TFC files are a flat concatenation of self-describing chunk bundles. Each bundle stores one
/// texture mip-level as either stored bytes or LZX-compressed blocks. This reader walks bundles
/// from offset 0 forward; it does NOT decompress LZX, so compressed bundles are surfaced
/// opaquely (the entry payload contains the raw block-size table plus compressed block data).
/// </remarks>
public sealed class TfcReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private bool _disposed;

  /// <summary>All bundles discovered while walking the TFC from offset 0.</summary>
  public IReadOnlyList<TfcEntry> Entries { get; }

  /// <summary>
  /// Parses bundle headers from <paramref name="stream"/>.
  /// </summary>
  /// <param name="stream">Seekable stream containing the TFC data; positioned anywhere on entry.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  public TfcReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek) throw new ArgumentException("TFC reading requires a seekable stream.", nameof(stream));

    this._stream = stream;
    this._leaveOpen = leaveOpen;

    if (stream.Length < TfcConstants.HeaderSize)
      throw new InvalidDataException("Stream is too small to be a valid TFC bundle.");

    this.Entries = ScanBundles();
  }

  /// <summary>
  /// Reads the bundle's raw payload — the per-block size table followed by all block bytes — into a new buffer.
  /// </summary>
  /// <param name="entry">The entry to read; must have come from this reader's <see cref="Entries"/>.</param>
  /// <returns>The bundle's raw post-header bytes (length == <see cref="TfcEntry.Size"/>).</returns>
  public byte[] Extract(TfcEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (entry.Size == 0)
      return [];

    if (entry.Size > int.MaxValue)
      throw new InvalidDataException($"Bundle payload too large for managed buffer: {entry.Size}.");

    this._stream.Position = entry.Offset + TfcConstants.HeaderSize;
    var buffer = new byte[entry.Size];
    ReadExact(buffer);
    return buffer;
  }

  private List<TfcEntry> ScanBundles() {
    var entries = new List<TfcEntry>();
    Span<byte> header = stackalloc byte[TfcConstants.HeaderSize];
    long offset = 0;

    while (offset + TfcConstants.HeaderSize <= this._stream.Length) {
      this._stream.Position = offset;
      ReadExact(header);

      var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
      if (magic != TfcConstants.Magic) {
        if (entries.Count == 0)
          throw new InvalidDataException(
            $"Invalid TFC bundle magic at offset 0: 0x{magic:X8} (expected 0x{TfcConstants.Magic:X8}).");
        break;
      }

      var blockSize        = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
      var compressedSize   = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
      var uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);

      if (blockSize == 0)
        throw new InvalidDataException(
          $"TFC bundle at offset {offset} declares zero block size.");

      var blockCount = (uncompressedSize + blockSize - 1) / blockSize;
      var tableBytes = (long)blockCount * 8;
      var payloadBytes = tableBytes + compressedSize;
      var bundleEnd = offset + TfcConstants.HeaderSize + payloadBytes;

      if (bundleEnd > this._stream.Length)
        throw new InvalidDataException(
          $"TFC bundle at offset {offset} extends beyond end of stream " +
          $"(needs {payloadBytes} bytes after header, have {this._stream.Length - offset - TfcConstants.HeaderSize}).");

      entries.Add(new TfcEntry {
        Name             = $"bundle_{entries.Count.ToString("D5", CultureInfo.InvariantCulture)}.bin",
        Offset           = offset,
        Size             = payloadBytes,
        CompressedSize   = compressedSize,
        UncompressedSize = uncompressedSize,
        BlockSize        = blockSize,
        IsCompressed     = compressedSize != uncompressedSize,
      });

      offset = bundleEnd;
    }

    return entries;
  }

  private void ReadExact(Span<byte> buffer) {
    var total = 0;
    while (total < buffer.Length) {
      var read = this._stream.Read(buffer[total..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of TFC stream.");
      total += read;
    }
  }

  /// <inheritdoc />
    /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }
}
