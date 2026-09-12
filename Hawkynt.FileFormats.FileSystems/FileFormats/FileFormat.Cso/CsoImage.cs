#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.Cso;

internal enum CsoVariant {
  CsoV1,
  CsoV2,
  Zso,
}

internal enum CsoBlockEncoding {
  Stored,
  Deflate,
  Lz4,
}

internal sealed record CsoLayout(
  long FullSize,
  CsoVariant Variant,
  string Magic,
  uint HeaderSize,
  ulong UncompressedSize,
  uint BlockSize,
  byte Version,
  byte Align,
  int BlockCount,
  uint[] IndexRaw) {

  public long IndexEnd => CsoWriter.HeaderSize + this.IndexRaw.Length * sizeof(uint);

  public long DataStart => this.IndexRaw.Length == 0
    ? this.IndexEnd
    : CsoImage.OffsetOf(this.IndexRaw[0], this.Align);

  public long DataEnd => CsoImage.OffsetOf(this.IndexRaw[^1], this.Align);
}

/// <summary>Shared structural parser and logical block reader for CSO v1/v2 and ZSO.</summary>
internal static class CsoImage {
  internal const int MaximumBlockCount = 8_000_000;

  public static CsoLayout ReadLayout(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new InvalidDataException("CSO/ZSO parsing requires a readable, seekable stream.");
    if (stream.Length < CsoWriter.HeaderSize)
      throw new InvalidDataException("CSO/ZSO image is shorter than its 24-byte header.");

    stream.Position = 0;
    Span<byte> header = stackalloc byte[CsoWriter.HeaderSize];
    ReadExact(stream, header);

    var magic = Encoding.ASCII.GetString(header[..4]);
    var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
    var uncompressedSize = BinaryPrimitives.ReadUInt64LittleEndian(header[8..16]);
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
    var version = header[20];
    var align = header[21];

    var variant = (magic, version) switch {
      ("CISO", 0 or 1) => CsoVariant.CsoV1,
      ("CISO", 2) => CsoVariant.CsoV2,
      ("ZISO", 1) => CsoVariant.Zso,
      ("CISO", _) => throw new NotSupportedException($"Unsupported CSO version {version}."),
      ("ZISO", _) => throw new NotSupportedException($"Unsupported ZSO version {version}; ZSO defines version 1."),
      _ => throw new InvalidDataException("Not a CSO/ZSO image (missing CISO/ZISO magic)."),
    };

    if (variant is CsoVariant.CsoV2 or CsoVariant.Zso && headerSize != CsoWriter.HeaderSize)
      throw new InvalidDataException($"{magic} v{version} requires header_size=24, got {headerSize}.");
    if (blockSize == 0)
      throw new InvalidDataException("CSO/ZSO block_size is zero.");
    if (blockSize > int.MaxValue)
      throw new InvalidDataException($"CSO/ZSO block_size {blockSize} is too large for managed buffers.");
    if (align > 31)
      throw new InvalidDataException($"CSO/ZSO index_shift {align} is not representable safely.");

    var blockCount64 = uncompressedSize / blockSize + (uncompressedSize % blockSize == 0 ? 0UL : 1UL);
    if (blockCount64 > MaximumBlockCount)
      throw new InvalidDataException($"CSO/ZSO block count implausible: {blockCount64}.");
    var blockCount = checked((int)blockCount64);
    var indexCount = checked(blockCount + 1);
    var indexBytesLength = checked(indexCount * sizeof(uint));
    if (CsoWriter.HeaderSize + (long)indexBytesLength > stream.Length)
      throw new InvalidDataException("CSO/ZSO index table extends past end of file.");

    var indexBytes = new byte[indexBytesLength];
    stream.Position = CsoWriter.HeaderSize;
    ReadExact(stream, indexBytes);
    var index = new uint[indexCount];
    for (var i = 0; i < index.Length; ++i)
      index[i] = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(i * sizeof(uint), sizeof(uint)));

    if (variant == CsoVariant.CsoV2 && (index[^1] & CsoWriter.IndexUncompressedFlag) != 0)
      throw new InvalidDataException("CSO v2 final index entry must not carry the method flag.");

    var minimumDataOffset = CsoWriter.HeaderSize + (long)indexBytesLength;
    long previous = -1;
    for (var i = 0; i < index.Length; ++i) {
      var offset = OffsetOf(index[i], align);
      if (offset < minimumDataOffset)
        throw new InvalidDataException($"CSO/ZSO index {i} points inside the header/index table.");
      if (offset < previous)
        throw new InvalidDataException($"CSO/ZSO index is not monotonic at entry {i}.");
      if (offset > stream.Length)
        throw new InvalidDataException($"CSO/ZSO index {i} points past end of file.");
      previous = offset;
    }

    return new CsoLayout(
      stream.Length, variant, magic, headerSize, uncompressedSize, blockSize, version, align,
      blockCount, index);
  }

  public static (long Offset, long Length, CsoBlockEncoding Encoding) GetBlockSpan(CsoLayout layout, int blockIndex) {
    ArgumentNullException.ThrowIfNull(layout);
    if ((uint)blockIndex >= (uint)layout.BlockCount)
      throw new ArgumentOutOfRangeException(nameof(blockIndex));

    var raw = layout.IndexRaw[blockIndex];
    var offset = OffsetOf(raw, layout.Align);
    var next = OffsetOf(layout.IndexRaw[blockIndex + 1], layout.Align);
    var length = next - offset;
    if (length < 0 || next > layout.FullSize)
      throw new InvalidDataException($"CSO/ZSO block {blockIndex} has an invalid physical span.");

    var encoding = layout.Variant switch {
      CsoVariant.CsoV1 => (raw & CsoWriter.IndexUncompressedFlag) != 0
        ? CsoBlockEncoding.Stored : CsoBlockEncoding.Deflate,
      CsoVariant.Zso => (raw & CsoWriter.IndexUncompressedFlag) != 0
        ? CsoBlockEncoding.Stored : CsoBlockEncoding.Lz4,
      CsoVariant.CsoV2 => length >= layout.BlockSize
        ? CsoBlockEncoding.Stored
        : (raw & CsoWriter.IndexUncompressedFlag) != 0 ? CsoBlockEncoding.Lz4 : CsoBlockEncoding.Deflate,
      _ => throw new InvalidDataException("Unknown CSO/ZSO variant."),
    };
    return (offset, length, encoding);
  }

  public static int LogicalBlockLength(CsoLayout layout, int blockIndex) {
    if ((uint)blockIndex >= (uint)layout.BlockCount)
      throw new ArgumentOutOfRangeException(nameof(blockIndex));
    var start = checked((ulong)blockIndex * layout.BlockSize);
    var remaining = layout.UncompressedSize - start;
    return checked((int)Math.Min(remaining, layout.BlockSize));
  }

  /// <summary>
  /// Decodes one block to its full block_size slab. The final logical block may use only a prefix;
  /// writers conventionally pad the remainder before compression and consumers truncate by
  /// uncompressed_size.
  /// </summary>
  public static byte[] DecodeBlock(Stream stream, CsoLayout layout, int blockIndex) {
    var (offset, length, encoding) = GetBlockSpan(layout, blockIndex);
    if (length > int.MaxValue)
      throw new InvalidDataException($"CSO/ZSO block {blockIndex} is too large to decode.");
    var encoded = new byte[(int)length];
    stream.Position = offset;
    ReadExact(stream, encoded);

    var blockSize = checked((int)layout.BlockSize);
    switch (encoding) {
      case CsoBlockEncoding.Stored: {
        var logicalLength = LogicalBlockLength(layout, blockIndex);
        if (encoded.Length < logicalLength)
          throw new InvalidDataException($"Stored CSO/ZSO block {blockIndex} is shorter than its logical bytes.");
        var result = new byte[blockSize];
        encoded.AsSpan(0, Math.Min(encoded.Length, blockSize)).CopyTo(result);
        return result;
      }
      case CsoBlockEncoding.Deflate:
        return InflateExact(encoded, blockSize, blockIndex);
      case CsoBlockEncoding.Lz4:
        return Lz4BlockCodec.Decompress(encoded, blockSize);
      default:
        throw new InvalidDataException("Unknown CSO/ZSO block encoding.");
    }
  }

  public static Stream OpenLogicalStream(Stream image, CsoLayout layout, IReadOnlyDictionary<int, byte[]>? replacements = null)
    => new LogicalStream(image, layout, replacements);

  internal static long OffsetOf(uint rawIndex, byte align) {
    var units = rawIndex & CsoWriter.IndexOffsetMask;
    var offset = (ulong)units << align;
    if (offset > long.MaxValue)
      throw new InvalidDataException("CSO/ZSO index offset exceeds the supported stream range.");
    return (long)offset;
  }

  private static byte[] InflateExact(ReadOnlySpan<byte> source, int expectedSize, int blockIndex) {
    using var input = new MemoryStream(source.ToArray(), writable: false);
    using var deflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: false);
    var output = new byte[expectedSize];
    var written = 0;
    while (written < output.Length) {
      var read = deflate.Read(output, written, output.Length - written);
      if (read <= 0)
        break;
      written += read;
    }
    if (written != expectedSize)
      throw new InvalidDataException(
        $"DEFLATE block {blockIndex} decoded to {written} bytes, expected block_size {expectedSize}.");
    return output;
  }

  internal static void ReadExact(Stream stream, Span<byte> destination) {
    var read = 0;
    while (read < destination.Length) {
      var count = stream.Read(destination[read..]);
      if (count <= 0)
        throw new EndOfStreamException("Unexpected end of CSO/ZSO stream.");
      read += count;
    }
  }

  private sealed class LogicalStream(
      Stream image,
      CsoLayout layout,
      IReadOnlyDictionary<int, byte[]>? replacements) : Stream {
    private int _blockIndex;
    private byte[]? _block;
    private int _offsetInBlock;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => checked((long)layout.UncompressedSize);
    public override long Position {
      get => this._position;
      set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
      => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (buffer.IsEmpty || this._position >= this.Length)
        return 0;

      var total = 0;
      while (!buffer.IsEmpty && this._position < this.Length) {
        if (this._block == null) {
          if (this._blockIndex >= layout.BlockCount)
            throw new InvalidDataException("CSO/ZSO logical stream ended before uncompressed_size.");
          if (replacements != null && replacements.TryGetValue(this._blockIndex, out var replacement)) {
            if (replacement.Length != layout.BlockSize)
              throw new InvalidDataException(
                $"Replacement block {this._blockIndex} has {replacement.Length} bytes; expected {layout.BlockSize}.");
            this._block = replacement;
          } else {
            this._block = DecodeBlock(image, layout, this._blockIndex);
          }
          this._offsetInBlock = 0;
        }

        var logicalLength = LogicalBlockLength(layout, this._blockIndex);
        var available = logicalLength - this._offsetInBlock;
        var take = Math.Min(buffer.Length, available);
        this._block.AsSpan(this._offsetInBlock, take).CopyTo(buffer);
        buffer = buffer[take..];
        this._offsetInBlock += take;
        this._position += take;
        total += take;

        if (this._offsetInBlock == logicalLength) {
          this._block = null;
          this._offsetInBlock = 0;
          ++this._blockIndex;
        }
      }
      return total;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
