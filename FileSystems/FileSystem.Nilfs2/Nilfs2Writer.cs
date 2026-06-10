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

  private const ushort SuperMagic = 0x3434;
  private const int SuperblockOffset = 1024;

  private readonly List<(string Name, byte[] Data)> _files = [];

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
    // tools that walk in block units don't read past EOF.
    var minImageBytes = Math.Max(64L * 1024, SegmentStart + bodyBytes + blockSize);
    var imageBytes = ((minImageBytes + blockSize - 1) / blockSize) * blockSize;

    var img = new byte[imageBytes];

    // ── Superblock at offset 1024 (NILFS2 layout) ─────────────────────────
    var sb = img.AsSpan(SuperblockOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, 2u);                                // s_rev_level = 2 (NILFS2)
    BinaryPrimitives.WriteUInt16LittleEndian(sb[4..], 0);                            // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(sb[6..], SuperMagic);                   // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(sb[8..], 1024);                         // s_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(sb[10..], 0);                           // s_flags
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x0C..], 0);                         // s_crc_seed
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x10..], 0);                         // s_sum
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x14..], logBlockSize);              // s_log_block_size
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x18..], 1ul);                       // s_nsegments
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x20..], (ulong)imageBytes);         // s_dev_size
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x28..], 1ul);                       // s_first_data_block
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x30..], (uint)(imageBytes / blockSize)); // s_blocks_per_segment
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x34..], 5);                         // s_r_segments_percentage
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x38..], 1ul);                       // s_last_cno (single checkpoint)
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x40..], (ulong)(SegmentStart / blockSize)); // s_last_pseg

    if (!string.IsNullOrEmpty(volumeLabel)) {
      var lbl = Encoding.ASCII.GetBytes(volumeLabel);
      var copyLen = Math.Min(16, lbl.Length);
      lbl.AsSpan(0, copyLen).CopyTo(sb[0x80..(0x80 + copyLen)]);
    }

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
