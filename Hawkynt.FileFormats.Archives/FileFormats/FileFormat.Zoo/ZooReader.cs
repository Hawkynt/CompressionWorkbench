using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Zoo;

/// <summary>
/// Reads entries from Zoo archives, including canonical Zoo 2.x type-2
/// variable directory records.
/// </summary>
public sealed class ZooReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<ZooEntry> _entries = [];
  private bool _disposed;

  /// <summary>Gets archive entries, including entries marked deleted.</summary>
  public IReadOnlyList<ZooEntry> Entries => this._entries;

  /// <summary>Initializes a reader over a seekable Zoo archive.</summary>
  public ZooReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this.ReadDirectory();
  }

  /// <summary>Extracts and verifies one Zoo entry.</summary>
  public byte[] ExtractEntry(ZooEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.DataOffset < 0 || entry.DataOffset > this._stream.Length - entry.CompressedSize)
      throw new InvalidDataException($"Zoo member '{entry.EffectiveName}' points outside the archive.");

    this._stream.Position = entry.DataOffset;
    var compressed = new byte[entry.CompressedSize];
    ReadFully(this._stream, compressed);

    byte[] data = entry.CompressionMethod switch {
      ZooCompressionMethod.Store => compressed,
      ZooCompressionMethod.Lzw => DecompressLzw(compressed, entry.OriginalSize),
      _ => throw new NotSupportedException($"Unsupported Zoo compression method: {(byte)entry.CompressionMethod}."),
    };

    var computed = Crc16.Compute(data);
    if (computed != entry.Crc16)
      throw new InvalidDataException(
        $"CRC-16 mismatch for '{entry.EffectiveName}': expected 0x{entry.Crc16:X4}, computed 0x{computed:X4}.");

    return data;
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private void ReadDirectory() {
    if (this._stream.Length < ZooConstants.MinimumArchiveHeaderSize)
      throw new InvalidDataException("Stream is too short to contain a Zoo archive header.");

    this._stream.Position = 20;
    Span<byte> core = stackalloc byte[14];
    ReadFully(this._stream, core);
    var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(core);
    if (magic != ZooConstants.Magic)
      throw new InvalidDataException($"Invalid Zoo magic: 0x{magic:X8}.");

    var firstEntryOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(core[4..]);
    if (firstEntryOffset == 0)
      return;
    if (firstEntryOffset < ZooConstants.MinimumArchiveHeaderSize || firstEntryOffset >= this._stream.Length)
      throw new InvalidDataException($"Zoo first-directory offset {firstEntryOffset} is outside the archive.");

    var visited = new HashSet<uint>();
    var nextOffset = firstEntryOffset;
    while (nextOffset != 0) {
      if (!visited.Add(nextOffset))
        throw new InvalidDataException($"Zoo directory chain contains a cycle at offset {nextOffset}.");

      var parsed = ZooDirectoryCodec.ReadHeader(this._stream, nextOffset);
      var entry = parsed.Entry;
      if (entry.DataOffset < 0 || entry.DataOffset > this._stream.Length - entry.CompressedSize)
        throw new InvalidDataException($"Zoo member '{entry.EffectiveName}' points outside the archive.");

      this._entries.Add(entry);
      nextOffset = parsed.NextOffset;
      if (nextOffset != 0 && nextOffset >= this._stream.Length)
        throw new InvalidDataException($"Zoo next-directory offset {nextOffset} is outside the archive.");
    }
  }

  private static byte[] DecompressLzw(byte[] compressed, uint originalSize) {
    if (originalSize > int.MaxValue)
      throw new NotSupportedException("Zoo member exceeds the managed decoder's 2 GiB output limit.");

    using var ms = new MemoryStream(compressed);
    var decoder = new LzwDecoder(
      ms,
      minBits: ZooConstants.LzwMinBits,
      maxBits: ZooConstants.LzwMaxBits,
      useClearCode: true,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    return decoder.Decode((int)originalSize);
  }

  private static void ReadFully(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        throw new EndOfStreamException("Unexpected end of Zoo archive data.");
      offset += read;
    }
  }

  private static void ReadFully(Stream stream, byte[] buffer)
    => ReadFully(stream, buffer.AsSpan());
}
