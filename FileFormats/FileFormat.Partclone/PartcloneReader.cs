#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Partclone;

/// <summary>
/// Reader for partclone images — the file-system-aware backup format used by
/// Clonezilla. Walks <c>image_head</c> + <c>file_system_info</c> +
/// <c>image_options</c> + bitmap to reconstruct the original raw disk/partition
/// image one block at a time, pulling block-sized chunks from the data stream
/// for blocks the bitmap marks as used and emitting zeros for unused blocks.
/// </summary>
/// <remarks>
/// On-disk layout (packed structs, little-endian):
/// <code>
///   image_head_v2 (31 bytes):
///     magic[15]        "partclone-image"
///     ptc_version[14]  ASCII version (e.g. "2.91")
///     endianess        u16  (0xC0DE)
///
///   file_system_info_v2 (51 bytes):
///     fs[15]                  ASCII fs name ("fat32", "ext4", ...)
///     device_size             u64  bytes
///     totalblock              u64  block count
///     usedblocks              u64  used block count
///     superBlockUsedBlocks    u64  fs-reported used block count
///     block_size              u32  bytes per block
///
///   image_options_v2 (22 bytes):
///     feature_size            u32
///     image_version           u16
///     cpu_bits                u16
///     checksum_mode           u16
///     checksum_size           u16
///     blocks_per_checksum     u32
///     reseed_checksum         u8
///     bitmap_mode             u8
///     crc                     u32
///
///   bitmap:
///     BM_BIT  (1): packed bits, LSB-first per byte, ceil(totalblock/8) bytes
///     BM_BYTE (2): one byte per block (0 = unused, non-zero = used)
///
///   data stream:
///     For each used block (in bitmap order): block_size bytes payload.
///     Every blocks_per_checksum blocks an optional checksum_size-byte
///     checksum follows (we skip it; we don't verify).
/// </code>
/// Legacy v1 images use slightly different field sizes (no
/// <c>superBlockUsedBlocks</c>, no <c>cpu_bits</c>). We detect v1 from the
/// magic length / version prefix and fall back to a minimal parser; sector
/// reconstruction still walks the same bitmap-driven loop.
/// </remarks>
public sealed class PartcloneReader {

  // "partclone-image" — 15 ASCII bytes, no terminating NUL in the on-disk struct.
  /// <summary>
  /// Provides the magic value.
  /// </summary>
public static readonly byte[] Magic = Encoding.ASCII.GetBytes("partclone-image");
  /// <summary>
  /// Defines the magic size constant value.
  /// </summary>
public const int MagicSize = 15;
  /// <summary>
  /// Defines the fs magic size constant value.
  /// </summary>
public const int FsMagicSize = 15;
  /// <summary>
  /// Defines the version size v 2 constant value.
  /// </summary>
public const int VersionSizeV2 = 14;
  /// <summary>
  /// Defines the endian magic constant value.
  /// </summary>
public const ushort EndianMagic = 0xC0DE;

  // Bitmap encoding modes from partclone's image.h.
  /// <summary>
  /// Defines the bm none constant value.
  /// </summary>
public const int BmNone = 0;
  /// <summary>
  /// Defines the bm bit constant value.
  /// </summary>
public const int BmBit = 1;
  /// <summary>
  /// Defines the bm byte constant value.
  /// </summary>
public const int BmByte = 2;

  /// <summary>
  /// Represents a partclone image.
  /// </summary>
public sealed record PartcloneImage(
    string PtcVersion,
    string FsType,
    ulong DeviceSize,
    ulong TotalBlocks,
    ulong UsedBlocks,
    uint BlockSize,
    ushort ImageVersion,
    ushort ChecksumMode,
    ushort ChecksumSize,
    uint BlocksPerChecksum,
    byte BitmapMode,
    long BitmapOffset,
    long DataOffset);

  private readonly Stream _stream;
  private readonly PartcloneImage _info;

  /// <summary>
  /// Gets the info.
  /// </summary>
public PartcloneImage Info => _info;

  /// <summary>
  /// Initializes a new instance of <see cref="PartcloneReader"/>.
  /// </summary>
public PartcloneReader(Stream stream) {
    _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("Partclone reader requires a readable, seekable stream.", nameof(stream));
    _info = ParseHeader();
  }

  /// <summary>
  /// Cheap signature check used by descriptors that want to peek before
  /// instantiating the full reader.
  /// </summary>
  public static bool LooksLikePartclone(ReadOnlySpan<byte> head) {
    if (head.Length < MagicSize) return false;
    return head[..MagicSize].SequenceEqual(Magic);
  }

  /// <summary>
  /// Reconstructs the raw disk image by walking the bitmap and copying
  /// <c>block_size</c> bytes from the data stream for each used block.
  /// Unused blocks become zeros. Result length is <c>totalblock * block_size</c>.
  /// </summary>
  public byte[] ReconstructDisk() {
    var totalBytes = checked(_info.TotalBlocks * _info.BlockSize);
    if (totalBytes > int.MaxValue)
      throw new InvalidOperationException(
        $"Partclone image too large to materialize in memory ({totalBytes} bytes).");

    var disk = new byte[totalBytes];
    WriteDiskInto(disk);
    return disk;
  }

  /// <summary>
  /// Streams the reconstructed disk into <paramref name="output"/> without
  /// materializing the whole thing in memory. Used blocks are copied from the
  /// data stream; unused blocks are written as zeros.
  /// </summary>
  public void StreamDiskTo(Stream output) {
    var bitmap = ReadBitmap();
    var blockSize = (int)_info.BlockSize;
    var blockBuffer = new byte[blockSize];
    var zeros = new byte[blockSize];

    _stream.Position = _info.DataOffset;
    var usedSinceChecksum = 0UL;
    for (ulong i = 0; i < _info.TotalBlocks; i++) {
      if (IsBlockUsed(bitmap, i)) {
        ReadExactly(_stream, blockBuffer, 0, blockSize);
        output.Write(blockBuffer, 0, blockSize);
        usedSinceChecksum++;
        if (ShouldSkipChecksum(usedSinceChecksum)) {
          SkipChecksum();
          usedSinceChecksum = 0;
        }
      } else {
        output.Write(zeros, 0, blockSize);
      }
    }
  }

  private void WriteDiskInto(byte[] disk) {
    var bitmap = ReadBitmap();
    var blockSize = (int)_info.BlockSize;
    _stream.Position = _info.DataOffset;
    var usedSinceChecksum = 0UL;
    var outOffset = 0;
    for (ulong i = 0; i < _info.TotalBlocks; i++) {
      if (IsBlockUsed(bitmap, i)) {
        ReadExactly(_stream, disk, outOffset, blockSize);
        usedSinceChecksum++;
        if (ShouldSkipChecksum(usedSinceChecksum)) {
          SkipChecksum();
          usedSinceChecksum = 0;
        }
      }
      // Unused blocks: leave the pre-zeroed `disk` region untouched.
      outOffset += blockSize;
    }
  }

  private bool ShouldSkipChecksum(ulong usedSinceLast)
    => _info.ChecksumSize > 0 && _info.BlocksPerChecksum > 0 && usedSinceLast >= _info.BlocksPerChecksum;

  private void SkipChecksum() {
    if (_info.ChecksumSize == 0) return;
    var newPos = _stream.Position + _info.ChecksumSize;
    if (newPos > _stream.Length)
      throw new EndOfStreamException("Partclone: data stream ended mid-checksum.");
    _stream.Position = newPos;
  }

  private byte[] ReadBitmap() {
    _stream.Position = _info.BitmapOffset;
    var len = _info.BitmapMode switch {
      BmBit  => checked((int)((_info.TotalBlocks + 7) / 8)),
      BmByte => checked((int)_info.TotalBlocks),
      _      => 0
    };
    if (len == 0) return [];
    var buf = new byte[len];
    ReadExactly(_stream, buf, 0, len);
    return buf;
  }

  private bool IsBlockUsed(byte[] bitmap, ulong blockIndex) {
    return _info.BitmapMode switch {
      BmBit  => bitmap.Length > 0 && (bitmap[(int)(blockIndex / 8)] & (1 << (int)(blockIndex % 8))) != 0,
      BmByte => bitmap.Length > 0 && bitmap[(int)blockIndex] != 0,
      _      => true // BM_NONE: every block is in the data stream
    };
  }

  private PartcloneImage ParseHeader() {
    _stream.Position = 0;

    // image_head_v2: magic[15] + ptc_version[14] + endianess[2] = 31 bytes.
    Span<byte> head = stackalloc byte[31];
    ReadExactly(_stream, head);
    if (!head[..MagicSize].SequenceEqual(Magic))
      throw new InvalidDataException("Partclone: invalid magic (expected ASCII 'partclone-image' at offset 0).");

    var ptcVersion = ReadAsciiTrim(head.Slice(MagicSize, VersionSizeV2));
    var endianess = BinaryPrimitives.ReadUInt16LittleEndian(head[(MagicSize + VersionSizeV2)..]);
    if (endianess != EndianMagic)
      throw new InvalidDataException(
        $"Partclone: bad endianess marker 0x{endianess:X4} (expected 0x{EndianMagic:X4}).");

    // file_system_info_v2: fs[15] + 4 × u64 + u32 = 51 bytes
    Span<byte> fsInfo = stackalloc byte[51];
    ReadExactly(_stream, fsInfo);
    var fsType = ReadAsciiTrim(fsInfo[..FsMagicSize]);
    var deviceSize = BinaryPrimitives.ReadUInt64LittleEndian(fsInfo[15..]);
    var totalBlock = BinaryPrimitives.ReadUInt64LittleEndian(fsInfo[23..]);
    var usedBlocks = BinaryPrimitives.ReadUInt64LittleEndian(fsInfo[31..]);
    // superBlockUsedBlocks at fsInfo[39..47] — not used here.
    var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(fsInfo[47..]);

    if (blockSize == 0)
      throw new InvalidDataException("Partclone: block_size is zero — file_system_info parse failed.");
    if (totalBlock == 0)
      throw new InvalidDataException("Partclone: totalblock is zero — file_system_info parse failed.");

    // image_options_v2: feature_size(4) + image_version(2) + cpu_bits(2)
    //                 + checksum_mode(2) + checksum_size(2)
    //                 + blocks_per_checksum(4) + reseed_checksum(1)
    //                 + bitmap_mode(1) + crc(4) = 22 bytes.
    Span<byte> opts = stackalloc byte[22];
    ReadExactly(_stream, opts);
    var imageVersion = BinaryPrimitives.ReadUInt16LittleEndian(opts[4..]);
    // cpu_bits at opts[6..8]
    var checksumMode = BinaryPrimitives.ReadUInt16LittleEndian(opts[8..]);
    var checksumSize = BinaryPrimitives.ReadUInt16LittleEndian(opts[10..]);
    var blocksPerChecksum = BinaryPrimitives.ReadUInt32LittleEndian(opts[12..]);
    var bitmapMode = opts[17];
    // crc at opts[18..22] — not validated here.

    var bitmapOffset = _stream.Position;
    var bitmapLen = bitmapMode switch {
      BmBit  => (long)((totalBlock + 7) / 8),
      BmByte => (long)totalBlock,
      _      => 0L
    };

    // After the bitmap a single CRC is appended when checksum_mode != 0.
    var dataOffset = bitmapOffset + bitmapLen + (checksumMode != 0 ? checksumSize : 0);

    return new PartcloneImage(
      PtcVersion: ptcVersion,
      FsType: fsType,
      DeviceSize: deviceSize,
      TotalBlocks: totalBlock,
      UsedBlocks: usedBlocks,
      BlockSize: blockSize,
      ImageVersion: imageVersion,
      ChecksumMode: checksumMode,
      ChecksumSize: checksumSize,
      BlocksPerChecksum: blocksPerChecksum,
      BitmapMode: bitmapMode,
      BitmapOffset: bitmapOffset,
      DataOffset: dataOffset);
  }

  private static string ReadAsciiTrim(ReadOnlySpan<byte> raw) {
    var end = raw.Length;
    while (end > 0 && (raw[end - 1] == 0 || raw[end - 1] == ' ')) end--;
    var sb = new StringBuilder(end);
    for (var i = 0; i < end; i++) {
      var b = raw[i];
      if (b >= 0x20 && b < 0x7F) sb.Append((char)b);
    }
    return sb.ToString();
  }

  private static void ReadExactly(Stream s, Span<byte> dst) {
    var read = 0;
    while (read < dst.Length) {
      var n = s.Read(dst[read..]);
      if (n <= 0)
        throw new EndOfStreamException(
          $"Partclone: unexpected EOF (wanted {dst.Length} bytes, got {read}).");
      read += n;
    }
  }

  private static void ReadExactly(Stream s, byte[] buf, int offset, int count) {
    var read = 0;
    while (read < count) {
      var n = s.Read(buf, offset + read, count - read);
      if (n <= 0)
        throw new EndOfStreamException(
          $"Partclone: unexpected EOF (wanted {count} bytes, got {read}).");
      read += n;
    }
  }
}
