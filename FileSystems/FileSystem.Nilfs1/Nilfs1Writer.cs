#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Nilfs1;

/// <summary>
/// Writes a minimal NILFS v1 image. Emits a fully spec-compliant superblock
/// (NILFS_SUPER_MAGIC 0x3434 at offset 1030, s_rev_level == 1) followed by a
/// single segment containing a compact directory index plus the file payloads.
///
/// <para><b>Scope.</b> Per <c>docs/FILESYSTEMS.md</c> NILFS v1 is the original
/// out-of-tree precursor to the mainline NILFS2 driver — full DAT-tree /
/// segment-usage walking + Linux kernel mount support is a multi-week effort
/// that requires the (sparsely documented) pre-mainline log replay code.
/// What we ship here is enough for round-trip List/Extract via our reader and
/// for external tools that only validate the superblock signature.</para>
///
/// <para><b>On-disk layout.</b></para>
/// <list type="bullet">
///   <item><description>0x000..0x3FF — boot sector area (zeroed, not used).</description></item>
///   <item><description>0x400..0x7FF — NILFS v1 superblock with magic 0x3434 at +6
///   and <c>s_rev_level == 1</c> at +0.</description></item>
///   <item><description>0x800..ZSTART — first segment header marker + our compact
///   directory + payload area. Marker = the writer magic + payload length at +8.</description></item>
/// </list>
/// </summary>
public sealed class Nilfs1Writer {

  /// <summary>Magic prefix that identifies a directory written by this writer.
  /// Lets our reader skip the surface-only path and enumerate real files,
  /// while external readers see a plain (valid-superblock) image with an
  /// opaque segment region.</summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  internal static readonly byte[] WriterMagic =
    [0x93, 0x1C, 0xE0, 0xB7, 0x08, 0xDA, 0x86, 0x11];

  /// <summary>
  /// Magic that prefixes each appended log-segment block written by
  /// <c>Nilfs1InPlaceModifier</c>. Each appended segment carries a u64 checkpoint
  /// number + a directory + a payload region; the reader merges all segments by
  /// highest-cno-per-name, dropping tombstoned entries. This is the
  /// log-structured continuous-snapshot append NILFS v1 shares with NILFS2.
  /// </summary>
  internal static readonly byte[] SegmentMagic =
    [0xC2, 0x19, 0x8D, 0xFB, 0x03, 0xA4, 0x97, 0x1E];

  /// <summary>Where the segment begins on disk (right after the 1024 + 1024
  /// boot / superblock region).</summary>
  internal const int SegmentStart = 2048;

  /// <summary>Superblock offset on disk (NILFS v1 spec).</summary>
  internal const int SuperblockOffsetOnDisk = 1024;

  /// <summary>Offset of <c>s_last_cno</c> within the superblock.</summary>
  internal const int LastCnoFieldOffset = 0x38;

  /// <summary>
  /// Bytes of the superblock covered by the checksum. The <c>nilfs_super_block</c>
  /// struct is shared between NILFS v1 and v2 (only <c>s_rev_level</c> differs), so
  /// the same 280-byte checksum length, crc32_le scheme, and field offsets apply.
  /// </summary>
  internal const ushort SuperBytes = 280;
  internal const int SuperblockSize = 1024;
  internal const int SecondaryBackOffset = 4096;
  internal const ushort StateValid = 0x0001;

  /// <summary>Deterministic per-volume UUID + CRC seed for reproducible output.</summary>
  private static readonly byte[] FixedUuid = [
    0xC0, 0x4F, 0x5B, 0x21, 0x77, 0xE5, 0x4A, 0x12,
    0x9D, 0xC1, 0xF1, 0x80, 0x03, 0xBE, 0x8C, 0xD2,
  ];
  private const uint FixedCrcSeed = 0x5A4C3A80u;

  /// <summary>
  /// Linux <c>crc32_le</c> (reflected IEEE polynomial, no input/output inversion).
  /// The <paramref name="seed"/> is the literal LFSR init (NILFS s_crc_seed).
  /// </summary>
  internal static uint Crc32Le(uint seed, ReadOnlySpan<byte> data) {
    var crc = seed;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k)
        crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
    }
    return crc;
  }

  /// <summary>Seals the s_sum checksum over the first <see cref="SuperBytes"/> bytes.</summary>
  internal static void FinalizeSuperblockChecksum(Span<byte> sb, uint seed) {
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], 0);
    var sum = Crc32Le(seed, sb[..SuperBytes]);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], sum);
  }

  private readonly List<(string Name, FilePayload Payload)> _files = [];

  /// <summary>Adds a file to the image. Subdirectory paths are encoded with
  /// '/' separators in <paramref name="name"/>.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name.Replace('\\', '/'), FilePayload.FromBytes(data)));
  }

  /// <summary>
  /// Adds a file whose bytes are produced on demand. <paramref name="size" /> must
  /// match what <paramref name="openStream" /> yields; the directory is laid out
  /// from it before a byte is read.
  /// </summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(openStream);
    _files.Add((name.Replace('\\', '/'), FilePayload.FromStream(size, openStream)));
  }

  /// <summary>When set, Build materialises only the metadata prefix.</summary>
  private bool _prefixOnly;

  /// <summary>
  /// Writes the volume into <paramref name="output" />: the superblock and
  /// directory, then each payload at its recorded offset and the secondary
  /// superblock at the tail. Only a non-seekable target materialises the volume.
  /// </summary>
  public void WriteTo(Stream output, int blockSize = 4096, int segmentSize = 0,
                      string? volumeLabel = null, bool enableChecksum = false) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanSeek) {
      var full = this.Build(blockSize, segmentSize, volumeLabel, enableChecksum);
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    this._prefixOnly = true;
    byte[] prefix;
    DeferredPayloads payloads;
    long totalBytes;
    try {
      prefix = this.BuildCore(blockSize, segmentSize, volumeLabel, enableChecksum, out payloads, out totalBytes,
                              out var secondary);
      output.Write(prefix, 0, prefix.Length);
      output.SetLength(basePosition + totalBytes);
      if (secondary is { } sec) {
        output.Position = basePosition + sec.Offset;
        output.Write(sec.Bytes, 0, sec.Bytes.Length);
      }
    } finally {
      this._prefixOnly = false;
    }
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + totalBytes;
    output.Flush();
  }

  /// <summary>Builds the NILFS v1 image. <paramref name="blockSize"/> drives the
  /// superblock's <c>s_log_block_size</c> field (must be a power of two between 1024
  /// and 65536). <paramref name="volumeLabel"/> is stored in the superblock's
  /// volume-label area (s_volume_name, an 80-byte slot at offset 0xA8).
  /// <paramref name="enableChecksum"/> sets the corresponding feature flag bit.
  /// </summary>
  public byte[] Build(int blockSize = 4096, int segmentSize = 0, string? volumeLabel = null, bool enableChecksum = false) {
    var image = this.BuildCore(blockSize, segmentSize, volumeLabel, enableChecksum,
                               out var payloads, out var totalBytes, out _);
    if (totalBytes > Array.MaxLength)
      throw new InvalidOperationException(
        $"NILFS: a {totalBytes:N0}-byte volume exceeds the array limit; write it to a seekable stream instead.");
    using var target = new MemoryStream(image, writable: true);
    payloads.FlushTo(target);
    return image;
  }

  private byte[] BuildCore(int blockSize, int segmentSize, string? volumeLabel, bool enableChecksum,
                           out DeferredPayloads payloads, out long totalBytes,
                           out (long Offset, byte[] Bytes)? secondarySuperblock) {
    if (blockSize < 1024 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("blockSize must be a power of two in [1024, 65536].", nameof(blockSize));
    if (segmentSize < 0) throw new ArgumentException("segmentSize must be >= 0.", nameof(segmentSize));
    var logBlockSize = (uint)(System.Numerics.BitOperations.Log2((uint)blockSize) - 10);

    // Compute payload byte count.
    var dirSize = ComputeDirectoryBytes();
    var dataSize = 0L;
    foreach (var (_, payload) in _files) dataSize += payload.Size;
    var bodyBytes = WriterMagic.Length + 8 + dirSize + dataSize;
    // Pad image to at least 64 KB and to a multiple of blockSize so external
    // tools that walk in block units don't read past EOF. Reserve a tail block
    // for the secondary superblock at imageBytes - 4096.
    var tailReserve = Math.Max(SecondaryBackOffset, blockSize);
    var minImageBytes = Math.Max(64 * 1024L, SegmentStart + bodyBytes + tailReserve);
    var imageBytes = (long)((minImageBytes + blockSize - 1) / blockSize) * blockSize;

    // Only the metadata prefix is materialised: the payload region follows the
    // directory, and both it and the secondary superblock at the tail are placed
    // by seek. Allocating the whole volume capped NILFS at the array limit.
    payloads = new DeferredPayloads();
    totalBytes = imageBytes;
    var prefixBytes = SegmentStart + WriterMagic.Length + 8 + dirSize;
    var img = new byte[this._prefixOnly ? prefixBytes : imageBytes];

    var actualSegBlocks = (uint)Math.Max(1, segmentSize > 0 ? segmentSize / blockSize : 8);
    var totalBlocks = (ulong)(imageBytes / blockSize);
    var nSegments = Math.Max(1ul, (totalBlocks - 1) / actualSegBlocks);
    const ulong fixedCtime = 1_700_000_000ul;

    // ── Superblock pair (shared nilfs_super_block layout, s_rev_level=1) ────
    // Byte-accurate to the v1/v2 struct: s_bytes=280, crc32_le-sealed s_sum,
    // label at +0xA8, secondary copy one block before EOF. NILFS v1 predates the
    // mainline driver and has no mkfs/mount tool, so there is no external gate;
    // the hardening keeps the superblock structurally faithful regardless.
    WriteSuperblock(img.AsSpan(1024), 1u, logBlockSize, nSegments, (ulong)imageBytes,
      actualSegBlocks, enableChecksum, fixedCtime, volumeLabel);

    var secondaryOffset = imageBytes - SecondaryBackOffset;
    secondarySuperblock = null;
    if (secondaryOffset >= SegmentStart + bodyBytes) {
      if (this._prefixOnly) {
        // Past the materialised prefix: hand it to the caller to place by seek.
        var mirror = new byte[SuperblockSize];
        WriteSuperblock(mirror, 1u, logBlockSize, nSegments,
          (ulong)imageBytes, actualSegBlocks, enableChecksum, fixedCtime, volumeLabel);
        secondarySuperblock = (secondaryOffset, mirror);
      } else {
        WriteSuperblock(img.AsSpan((int)secondaryOffset), 1u, logBlockSize, nSegments,
          (ulong)imageBytes, actualSegBlocks, enableChecksum, fixedCtime, volumeLabel);
      }
    }

    // ── Segment / directory / payload at SegmentStart ──────────────────────
    var seg = img.AsSpan(SegmentStart);
    WriterMagic.CopyTo(seg);
    BinaryPrimitives.WriteInt64LittleEndian(seg[WriterMagic.Length..], dirSize);

    var dirOffset = WriterMagic.Length + 8;
    var payloadOffset = dirOffset + (int)dirSize;
    var payloadCursor = 0L;
    foreach (var (name, payload) in _files) {
      // Directory entry layout (variable-size):
      //   u32 name_len, byte[name_len] name (UTF-8),
      //   u64 payload_offset (relative to start of payload region),
      //   u64 size.
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt32LittleEndian(seg[dirOffset..], (uint)nameBytes.Length);
      nameBytes.CopyTo(seg[(dirOffset + 4)..]);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length)..], payloadCursor);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length + 8)..], payload.Size);
      dirOffset += 4 + nameBytes.Length + 16;

      // The payload belongs after the directory; it is written separately so a
      // volume larger than a byte[] can address is still producible.
      payloads.Add(SegmentStart + payloadOffset + payloadCursor, payload);
      payloadCursor += payload.Size;
    }

    return img;
  }

  /// <summary>
  /// Encodes a complete 1024-byte NILFS superblock (shared v1/v2 struct) at
  /// <paramref name="dest"/> and seals it with a valid crc32_le checksum.
  /// </summary>
  private static void WriteSuperblock(
      Span<byte> dest, uint revLevel, uint logBlockSize, ulong nSegments,
      ulong devSize, uint blocksPerSegment, bool enableChecksum, ulong ctime,
      string? volumeLabel) {
    dest[..SuperblockSize].Clear();
    BinaryPrimitives.WriteUInt32LittleEndian(dest, revLevel);                  // s_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], 0);                    // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], 0x3434);             // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(dest[8..], SuperBytes);           // s_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(dest[10..], (ushort)(enableChecksum ? 1 : 0)); // s_flags
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x0C..], FixedCrcSeed);      // s_crc_seed
    // 0x10 s_sum filled below.
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x14..], logBlockSize);      // s_log_block_size
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x18..], nSegments);         // s_nsegments
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x20..], devSize);           // s_dev_size
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x28..], 1ul);             // s_first_data_block
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x30..], blocksPerSegment);  // s_blocks_per_segment
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x34..], 5u);              // s_r_segments_percentage
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x38..], 1ul);             // s_last_cno
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x40..], 1ul);             // s_last_pseg
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x58..], ctime);             // s_ctime
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x60..], ctime);             // s_mtime
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x68..], ctime);             // s_wtime
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x72..], 50);              // s_max_mnt_count
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x74..], StateValid);        // s_state
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x76..], 1);               // s_errors=CONTINUE
    BinaryPrimitives.WriteUInt64LittleEndian(dest[0x78..], ctime);             // s_lastcheck
    BinaryPrimitives.WriteUInt32LittleEndian(dest[0x8C..], 11u);             // s_first_ino
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x90..], 128);             // s_inode_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x92..], 32);              // s_dat_entry_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x94..], 192);            // s_checkpoint_size
    BinaryPrimitives.WriteUInt16LittleEndian(dest[0x96..], 16);             // s_segment_usage_size
    FixedUuid.AsSpan().CopyTo(dest[0x98..]);                                   // s_uuid

    if (!string.IsNullOrEmpty(volumeLabel)) {
      var lbl = Encoding.ASCII.GetBytes(volumeLabel);
      lbl.AsSpan(0, Math.Min(80, lbl.Length)).CopyTo(dest[0xA8..]);            // s_volume_name
    }

    FinalizeSuperblockChecksum(dest, FixedCrcSeed);
  }

  private int ComputeDirectoryBytes() {
    var total = 0;
    foreach (var (name, _) in _files) {
      var nameLen = Encoding.UTF8.GetByteCount(name);
      total += 4 + nameLen + 16;
    }
    return total;
  }
}
