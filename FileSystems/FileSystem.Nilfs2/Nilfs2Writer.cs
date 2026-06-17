#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Nilfs2;

/// <summary>
/// Writes a minimal NILFS2 image. Emits a spec-compliant superblock
/// (NILFS_SUPER_MAGIC 0x3434 at offset 1030, <c>s_rev_level &gt;= 2</c>) followed
/// by a single segment region carrying a compact writer-private directory
/// plus the file payloads.
/// </summary>
/// <remarks>
/// <para><b>Honest scope.</b> NILFS2's mainline on-disk layout — DAT (Disk
/// Address Translation) B-tree, IFile/CPFile/SUFile metadata files, segment
/// summary headers, checkpoint chain, log replay — is a multi-week effort
/// that's not justified for a single FS slot. What we ship here is enough for
/// self-round-trip via this descriptor's reader: a spec-compliant superblock
/// (so external NILFS2 sniffers see a valid signature) plus a private
/// directory at <see cref="SegmentStart"/> guarded by <see cref="WriterMagic"/>.
/// External NILFS2 tools that do a deep mount will reject the image; that's
/// the deliberate trade. The writer is single-checkpoint by construction —
/// snapshot semantics are out of scope.</para>
///
/// <para><b>On-disk layout.</b></para>
/// <list type="bullet">
///   <item><description>0x000..0x3FF — boot sector area (zeroed).</description></item>
///   <item><description>0x400..0x7FF — NILFS2 superblock with magic 0x3434 at
///   +6 and <c>s_rev_level == 2</c> at +0.</description></item>
///   <item><description>0x800..ZSTART — first segment region. Starts with
///   <see cref="WriterMagic"/> "NILFS2WB" + 8-byte directory length + a sequence
///   of (u32 name_len, name, u64 payload_offset, u64 size) entries, followed by
///   the payload region.</description></item>
/// </list>
/// </remarks>
public sealed class Nilfs2Writer {

  /// <summary>
  /// Magic prefix that identifies a directory written by this writer. Lets our
  /// reader pick up real files without confusing other NILFS2 readers (which
  /// expect a segment summary at this offset, not our magic).
  /// </summary>
  internal static readonly byte[] WriterMagic = "NILFS2WB"u8.ToArray();

  /// <summary>
  /// Magic that prefixes each appended log-segment block written by
  /// <c>Nilfs2InPlaceModifier</c>. Each segment carries a u64 checkpoint number
  /// + a directory + a payload region. The reader merges all segments by
  /// highest-cno-per-name; tombstones drop entries from the listing. This is
  /// the load-bearing primitive that lets NILFS2 advertise R/W with the
  /// continuous-snapshot byte-identical-old-segment invariant intact.
  /// </summary>
  internal static readonly byte[] SegmentMagic = "NILFS2SG"u8.ToArray();

  /// <summary>Where the writer's directory + payload region begins.</summary>
  internal const int SegmentStart = 2048;

  /// <summary>Superblock offset on disk (NILFS2 spec).</summary>
  internal const int SuperblockOffsetOnDisk = 1024;

  /// <summary>Offset of <c>s_last_cno</c> field within the superblock.</summary>
  internal const int LastCnoFieldOffset = 0x38;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>
  /// Deterministic UUID/CRC-seed source. NILFS images carry a per-volume random
  /// UUID and CRC seed; the writer derives them from a fixed seed so output is
  /// reproducible (and the checksum recomputes identically on read-back).
  /// </summary>
  private static readonly byte[] FixedUuid = [
    0xC0, 0x4F, 0x5B, 0x21, 0x77, 0xE5, 0x4A, 0x12,
    0x9D, 0xC1, 0xF1, 0x80, 0x03, 0xBE, 0x8C, 0xD2,
  ];
  private const uint FixedCrcSeed = 0x5A4C3A80u;

  /// <summary>Adds a file to the image.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name.Replace('\\', '/'), data));
  }

  /// <summary>
  /// Builds the NILFS2 image. <paramref name="blockSize"/> must be a power of two
  /// in [1024, 65536]. <paramref name="volumeLabel"/> is written into the
  /// superblock's 16-byte volume-label slot at +0x80.
  /// </summary>
  public byte[] Build(int blockSize = 4096, string? volumeLabel = null) {
    if (blockSize < 1024 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("blockSize must be a power of two in [1024, 65536].", nameof(blockSize));
    var logBlockSize = (uint)(System.Numerics.BitOperations.Log2((uint)blockSize) - 10);

    // Compute body sizes.
    var dirSize = this.ComputeDirectoryBytes();
    var dataSize = 0L;
    foreach (var (_, data) in this._files) dataSize += data.LongLength;
    var bodyBytes = WriterMagic.Length + 8 + dirSize + dataSize;

    // Pad image to at least 64 KB and to a multiple of blockSize so external
    // tools that walk in block units don't read past EOF. Reserve one extra
    // tail block of slack so the secondary superblock (placed at
    // imageBytes - 4096) never overlaps the writer body.
    var tailReserve = Math.Max(Nilfs2Superblock.SecondaryBackOffset, blockSize);
    var minImageBytes = Math.Max(64L * 1024, SegmentStart + bodyBytes + tailReserve);
    var imageBytes = ((minImageBytes + blockSize - 1) / blockSize) * blockSize;

    var img = new byte[imageBytes];

    // ── Superblock pair (NILFS2 layout) ───────────────────────────────────
    // Both copies are byte-accurate, CRC-valid superblocks matching the field
    // layout mkfs.nilfs2 emits (s_bytes=280, crc32_le-sealed s_sum, label at
    // +0xA8, secondary copy one block before EOF). The geometry fields are
    // derived from the chosen block size so external NILFS2 sniffers that read
    // s_dev_size / s_nsegments / s_blocks_per_segment see a self-consistent
    // volume description.
    var totalBlocks = (ulong)(imageBytes / blockSize);
    // NILFS reserves the first segment for the boot/super region; blocks per
    // segment defaults to 16 (mkfs -B). nsegments = usable blocks / bps.
    var blocksPerSegment = (uint)Math.Min(16, Math.Max(1, totalBlocks));
    var nSegments = Math.Max(1ul, (totalBlocks - 1) / blocksPerSegment);
    // ctime is fixed so output is byte-reproducible.
    const ulong fixedCtime = 1_700_000_000ul;
    var freeBlocks = nSegments * blocksPerSegment;

    // Primary superblock at file offset 1024 (s_state advertises a clean FS).
    Nilfs2Superblock.Encode(
      img.AsSpan(Nilfs2Superblock.PrimaryOffset),
      logBlockSize, nSegments, (ulong)imageBytes, blocksPerSegment,
      lastCno: 1, lastPseg: 1, lastSeq: 0, freeBlocks: freeBlocks,
      ctime: fixedCtime, state: Nilfs2Superblock.StateValid,
      crcSeed: FixedCrcSeed, uuid: FixedUuid, volumeLabel: volumeLabel);

    // Secondary superblock one block before EOF (mkfs writes a backup here).
    var secondaryOffset = imageBytes - Nilfs2Superblock.SecondaryBackOffset;
    if (secondaryOffset >= SegmentStart + bodyBytes)
      Nilfs2Superblock.Encode(
        img.AsSpan((int)secondaryOffset),
        logBlockSize, nSegments, (ulong)imageBytes, blocksPerSegment,
        lastCno: 1, lastPseg: 1, lastSeq: 0, freeBlocks: freeBlocks,
        ctime: fixedCtime, state: Nilfs2Superblock.StateValid,
        crcSeed: FixedCrcSeed, uuid: FixedUuid, volumeLabel: volumeLabel);

    // ── Segment region at SegmentStart: writer directory + payloads ───────
    var seg = img.AsSpan(SegmentStart);
    WriterMagic.CopyTo(seg);
    BinaryPrimitives.WriteInt64LittleEndian(seg[WriterMagic.Length..], dirSize);

    var dirOffset = WriterMagic.Length + 8;
    var payloadOffset = dirOffset + (int)dirSize;
    var payloadCursor = 0L;
    foreach (var (name, data) in this._files) {
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt32LittleEndian(seg[dirOffset..], (uint)nameBytes.Length);
      nameBytes.CopyTo(seg[(dirOffset + 4)..]);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length)..], payloadCursor);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length + 8)..], data.LongLength);
      dirOffset += 4 + nameBytes.Length + 16;

      data.CopyTo(seg[(payloadOffset + (int)payloadCursor)..]);
      payloadCursor += data.LongLength;
    }

    return img;
  }

  /// <summary>Writes the assembled image to a stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var img = this.Build();
    output.Write(img, 0, img.Length);
  }

  private int ComputeDirectoryBytes() {
    var total = 0;
    foreach (var (name, _) in this._files) {
      var nameLen = Encoding.UTF8.GetByteCount(name);
      total += 4 + nameLen + 16;
    }
    return total;
  }
}
