#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Partclone;

/// <summary>
/// Reader for partclone image format 0002 — the file-system-aware backup format used by
/// Clonezilla. Walks the image descriptor and bitmap to reconstruct the original raw
/// partition one block at a time: used blocks come from the data stream, unused blocks
/// are zeros. Header, bitmap and data-strip checksums are verified.
/// </summary>
/// <remarks>
/// On-disk layout (packed, little-endian), per partclone's <c>IMAGE_FORMATS.md</c> and
/// confirmed byte for byte against images written by partclone 0.3.27:
/// <code>
///   image_head (36 bytes)
///     0   magic[16]        "partclone-image\0"
///     16  ptc_version[14]  ASCII, NUL padded (e.g. "0.3.27")
///     30  version[4]       "0002"
///     34  endianess u16    0xC0DE
///   file_system_info (52 bytes)
///     36  fs[16]                  ASCII fs name ("FAT12", "EXTFS", ...)
///     52  device_size u64
///     60  totalblock u64
///     68  superBlockUsedBlocks u64  used blocks according to the file system
///     76  usedblocks u64            used blocks according to the bitmap
///     84  block_size u32
///   image_options (18 bytes)
///     88  feature_size u32  (18)
///     92  image_version u16 (2)
///     94  cpu_bits u16
///     96  checksum_mode u16  0x00 none, 0x20 CRC32, 0x30 XXH64 (0x31 XXH128 unsupported)
///     98  checksum_size u16  0 / 4 / 8
///     100 blocks_per_checksum u32
///     104 reseed_checksum u8
///     105 bitmap_mode u8     0 none, 1 bit, 8 byte
///   106 crc u32  partclone CRC32 of bytes 0..105
///   110 bitmap   BM_BIT: ceil(totalblock/8) bytes LSB-first; BM_BYTE: one byte per block
///       + u32 partclone CRC32 of the bit-packed bitmap, whenever bitmap_mode != 0 (partclone's own
///       reader expects the 0001 trailer "BiTmAgIc" after a byte map instead; both are accepted)
///   data: used blocks in order; after every blocks_per_checksum blocks, and after the
///         final partial strip, a checksum_size-byte checksum of the strip
/// </code>
/// "partclone CRC32" is the reflected IEEE CRC-32 register seeded with 0xFFFFFFFF and
/// stored without the final inversion. Format 0001 images (partclone before 0.3.0) are
/// recognised and refused with <see cref="NotSupportedException"/>.
/// </remarks>
public sealed class PartcloneReader {

  /// <summary>The 15 printable magic bytes; the on-disk field is 16 bytes with a trailing NUL.</summary>
  public static readonly byte[] Magic = Encoding.ASCII.GetBytes("partclone-image");
  public const int MagicSize = 15;
  public const int MagicFieldSize = 16;
  public const int VersionSizeV2 = 14;
  public const int FsFieldSize = 16;
  public const ushort EndianMagic = 0xC0DE;
  public const int HeaderSize = 110;
  public const int HeaderCrcOffset = 106;
  public const int FeatureSize = 18;

  /// <summary>Bitmap modes (partclone <c>bitmap_mode_t</c>).</summary>
  public const byte BmNone = 0x00;
  public const byte BmBit = 0x01;
  public const byte BmByte = 0x08;

  /// <summary>
  /// On-disk checksum modes (partclone <c>checksum_mode_enum</c>). These are not the numbers the
  /// <c>-a</c> option takes (0/1/2), and IMAGE_FORMATS.md still shows the option numbers; images
  /// written by partclone 0.3.27 carry 0x20 for CRC32.
  /// </summary>
  public const ushort CsNone = 0x00;
  public const ushort CsCrc32 = 0x20;
  public const ushort CsXxh64 = 0x30;
  public const ushort CsXxh128 = 0x31;

  /// <summary>Represents a partclone image descriptor.</summary>
  public sealed record PartcloneImage(
    string PtcVersion,
    string FsType,
    ulong DeviceSize,
    ulong TotalBlocks,
    ulong UsedBlocks,
    ulong SuperBlockUsedBlocks,
    uint BlockSize,
    ushort ImageVersion,
    ushort CpuBits,
    ushort ChecksumMode,
    ushort ChecksumSize,
    uint BlocksPerChecksum,
    byte ReseedChecksum,
    byte BitmapMode,
    long BitmapOffset,
    long DataOffset);

  private readonly Stream _stream;
  private readonly PartcloneImage _info;
  private bool _byteMapHasMagic;

  public PartcloneImage Info => this._info;

  /// <summary>Parses and validates the image descriptor.</summary>
  /// <exception cref="InvalidDataException">The descriptor is malformed or its CRC does not match.</exception>
  /// <exception cref="NotSupportedException">The image is format 0001 or big-endian.</exception>
  public PartcloneReader(Stream stream) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("Partclone reader requires a readable, seekable stream.", nameof(stream));
    this._info = this.ParseHeader();
  }

  /// <summary>Cheap signature check for descriptors that peek before instantiating the reader.</summary>
  public static bool LooksLikePartclone(ReadOnlySpan<byte> head)
    => head.Length >= MagicSize && head[..MagicSize].SequenceEqual(Magic);

  /// <summary>
  /// partclone's CRC-32 of <paramref name="data"/>: reflected IEEE polynomial, register seeded with
  /// 0xFFFFFFFF and stored without the final inversion, i.e. the bitwise complement of the usual CRC-32.
  /// </summary>
  public static uint PartcloneCrc32(ReadOnlySpan<byte> data) {
    var crc = new Crc32();
    crc.Update(data);
    return ~crc.Value;
  }

  /// <summary>Reconstructs the whole raw partition in memory.</summary>
  public byte[] ReconstructDisk() {
    var totalBytes = checked(this._info.TotalBlocks * this._info.BlockSize);
    if (totalBytes > int.MaxValue)
      throw new InvalidOperationException($"Partclone image too large to materialize in memory ({totalBytes} bytes).");
    using var ms = new MemoryStream((int)totalBytes);
    this.StreamDiskTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// Streams the reconstructed partition into <paramref name="output"/>, verifying the bitmap CRC
  /// and every strip checksum on the way.
  /// </summary>
  public void StreamDiskTo(Stream output) {
    var bitmap = this.ReadUsageBits();
    var blockSize = (int)this._info.BlockSize;
    var block = new byte[blockSize];
    var zeros = new byte[blockSize];
    var verifier = new StripVerifier(this._info);

    this._stream.Position = this._info.DataOffset;
    for (ulong i = 0; i < this._info.TotalBlocks; ++i) {
      if (!IsSet(bitmap, i, this._info.BitmapMode)) {
        output.Write(zeros);
        continue;
      }
      ReadExactly(this._stream, block);
      output.Write(block);
      if (verifier.Add(block)) verifier.Check(this._stream);
    }
    if (verifier.Pending) verifier.Check(this._stream);
  }

  /// <summary>Returns the allocation map exactly as serialized in the image (bit or byte form).</summary>
  public byte[] ReadAllocationMap() {
    var (raw, _) = this.ReadRawBitmap();
    return raw;
  }

  /// <summary>Bit-packed usage map (LSB-first), verified against the bitmap CRC. Empty for BM_NONE.</summary>
  private byte[] ReadUsageBits() {
    var (raw, bits) = this.ReadRawBitmap();
    if (this._info.BitmapMode == BmNone) return [];
    if (this._byteMapHasMagic) return bits;
    Span<byte> stored = stackalloc byte[4];
    this._stream.Position = this._info.BitmapOffset + raw.Length;
    ReadExactly(this._stream, stored);
    var expected = BinaryPrimitives.ReadUInt32LittleEndian(stored);
    var actual = PartcloneCrc32(bits);
    if (actual != expected)
      throw new InvalidDataException($"Partclone: bitmap CRC mismatch (stored 0x{expected:X8}, computed 0x{actual:X8}).");
    return bits;
  }

  private (byte[] Raw, byte[] Bits) ReadRawBitmap() {
    var info = this._info;
    var bitLength = checked((int)((info.TotalBlocks + 7) / 8));
    switch (info.BitmapMode) {
      case BmBit: {
        var raw = new byte[bitLength];
        this._stream.Position = info.BitmapOffset;
        ReadExactly(this._stream, raw);
        return (raw, raw);
      }
      case BmByte: {
        var raw = new byte[checked((int)info.TotalBlocks)];
        this._stream.Position = info.BitmapOffset;
        ReadExactly(this._stream, raw);
        var bits = new byte[bitLength];
        for (var i = 0; i < raw.Length; ++i)
          if (raw[i] != 0) bits[i >> 3] |= (byte)(1 << (i & 7));
        return (raw, bits);
      }
      default:
        return ([], []);
    }
  }

  private static bool IsSet(byte[] bits, ulong index, byte mode)
    => mode == BmNone || (bits[(int)(index >> 3)] & (1 << (int)(index & 7))) != 0;

  private PartcloneImage ParseHeader() {
    this._stream.Position = 0;
    Span<byte> h = stackalloc byte[HeaderSize];
    var got = ReadUpTo(this._stream, h);
    if (got < 36 || !h[..MagicSize].SequenceEqual(Magic))
      throw new InvalidDataException("Partclone: invalid magic (expected ASCII 'partclone-image' at offset 0).");

    var version = Encoding.ASCII.GetString(h.Slice(30, 4));
    if (version == "0001")
      throw new NotSupportedException("Partclone image format 0001 (partclone < 0.3.0) is not supported.");
    if (version != "0002")
      throw new InvalidDataException($"Partclone: unknown image version '{ReadAsciiTrim(h.Slice(30, 4))}'.");
    var endianess = BinaryPrimitives.ReadUInt16LittleEndian(h[34..]);
    if (endianess == 0xDEC0)
      throw new NotSupportedException("Big-endian partclone images are not supported.");
    if (endianess != EndianMagic)
      throw new InvalidDataException($"Partclone: bad endianess marker 0x{endianess:X4}.");
    if (got < HeaderSize)
      throw new InvalidDataException("Partclone: image descriptor is truncated.");

    var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(h[HeaderCrcOffset..]);
    var computedCrc = PartcloneCrc32(h[..HeaderCrcOffset]);
    if (storedCrc != computedCrc)
      throw new InvalidDataException($"Partclone: image descriptor CRC mismatch (stored 0x{storedCrc:X8}, computed 0x{computedCrc:X8}).");

    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(h[84..]);
    var totalBlock = BinaryPrimitives.ReadUInt64LittleEndian(h[60..]);
    if (blockSize == 0) throw new InvalidDataException("Partclone: block_size is zero.");
    if (totalBlock == 0) throw new InvalidDataException("Partclone: totalblock is zero.");

    var featureSize = BinaryPrimitives.ReadUInt32LittleEndian(h[88..]);
    if (featureSize != FeatureSize)
      throw new InvalidDataException($"Partclone: unexpected image_options size {featureSize} (expected {FeatureSize}).");
    var checksumMode = BinaryPrimitives.ReadUInt16LittleEndian(h[96..]);
    var checksumSize = BinaryPrimitives.ReadUInt16LittleEndian(h[98..]);
    var blocksPerChecksum = BinaryPrimitives.ReadUInt32LittleEndian(h[100..]);
    var bitmapMode = h[105];
    if (bitmapMode is not (BmNone or BmBit or BmByte))
      throw new InvalidDataException($"Partclone: unknown bitmap mode {bitmapMode}.");
    switch (checksumMode) {
      case CsNone: break;
      case CsCrc32 when checksumSize == 4 && blocksPerChecksum > 0: break;
      case CsXxh64 when checksumSize == 8 && blocksPerChecksum > 0: break;
      case CsCrc32 or CsXxh64:
        throw new InvalidDataException($"Partclone: checksum mode {checksumMode} with size {checksumSize} and {blocksPerChecksum} blocks per checksum is inconsistent.");
      default:
        throw new NotSupportedException($"Partclone checksum mode {checksumMode} is not supported.");
    }

    var bitmapLen = bitmapMode switch {
      BmBit => (long)((totalBlock + 7) / 8),
      BmByte => (long)totalBlock,
      _ => 0L,
    };
    var bitmapCrc = bitmapMode == BmNone ? 0 : 4;
    if (bitmapMode == BmByte) {
      Span<byte> trailer = stackalloc byte[8];
      this._stream.Position = HeaderSize + bitmapLen;
      if (ReadUpTo(this._stream, trailer) == 8 && trailer.SequenceEqual("BiTmAgIc"u8)) {
        this._byteMapHasMagic = true;
        bitmapCrc = 8;
      }
    }

    return new PartcloneImage(
      PtcVersion: ReadAsciiTrim(h.Slice(MagicFieldSize, VersionSizeV2)),
      FsType: ReadAsciiTrim(h.Slice(36, FsFieldSize)),
      DeviceSize: BinaryPrimitives.ReadUInt64LittleEndian(h[52..]),
      TotalBlocks: totalBlock,
      UsedBlocks: BinaryPrimitives.ReadUInt64LittleEndian(h[76..]),
      SuperBlockUsedBlocks: BinaryPrimitives.ReadUInt64LittleEndian(h[68..]),
      BlockSize: blockSize,
      ImageVersion: BinaryPrimitives.ReadUInt16LittleEndian(h[92..]),
      CpuBits: BinaryPrimitives.ReadUInt16LittleEndian(h[94..]),
      ChecksumMode: checksumMode,
      ChecksumSize: checksumSize,
      BlocksPerChecksum: blocksPerChecksum,
      ReseedChecksum: h[104],
      BitmapMode: bitmapMode,
      BitmapOffset: HeaderSize,
      DataOffset: HeaderSize + bitmapLen + bitmapCrc);
  }

  /// <summary>Accumulates a data strip and checks its stored checksum.</summary>
  private sealed class StripVerifier(PartcloneImage info) {
    private readonly bool _enabled = info.ChecksumMode != CsNone;
    private readonly Crc32 _crc = new();
    private XxHash64 _xxh = new();
    private uint _inStrip;

    public bool Pending => this._enabled && this._inStrip > 0;

    /// <summary>Adds one block; true when the strip is complete and its checksum follows.</summary>
    public bool Add(ReadOnlySpan<byte> block) {
      if (!this._enabled) return false;
      if (info.ChecksumMode == CsCrc32) this._crc.Update(block);
      else this._xxh.Update(block);
      return ++this._inStrip == info.BlocksPerChecksum;
    }

    public void Check(Stream stream) {
      Span<byte> stored = stackalloc byte[info.ChecksumSize];
      ReadExactly(stream, stored);
      bool ok;
      if (info.ChecksumMode == CsCrc32)
        ok = BinaryPrimitives.ReadUInt32LittleEndian(stored) == ~this._crc.Value;
      else
        ok = BinaryPrimitives.ReadUInt64LittleEndian(stored) == this._xxh.Value;
      if (!ok)
        throw new InvalidDataException($"Partclone: data checksum mismatch at image offset {stream.Position - info.ChecksumSize}.");
      this._inStrip = 0;
      if (info.ReseedChecksum != 0) {
        this._crc.Reset();
        this._xxh = new XxHash64();
      }
    }
  }

  private static string ReadAsciiTrim(ReadOnlySpan<byte> raw) {
    var nul = raw.IndexOf((byte)0);
    if (nul >= 0) raw = raw[..nul];
    var sb = new StringBuilder(raw.Length);
    foreach (var b in raw)
      if (b is >= 0x20 and < 0x7F) sb.Append((char)b);
    return sb.ToString().TrimEnd();
  }

  private static int ReadUpTo(Stream s, Span<byte> dst) {
    var read = 0;
    while (read < dst.Length) {
      var n = s.Read(dst[read..]);
      if (n <= 0) break;
      read += n;
    }
    return read;
  }

  private static void ReadExactly(Stream s, Span<byte> dst) {
    if (ReadUpTo(s, dst) != dst.Length)
      throw new EndOfStreamException($"Partclone: unexpected end of image (wanted {dst.Length} bytes).");
  }
}
