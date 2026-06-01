#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

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
///   directory + payload area. Marker = "NILFS1WB" + payload length at +8.</description></item>
/// </list>
/// </summary>
public sealed class Nilfs1Writer {

  /// <summary>Magic prefix that identifies a directory written by this writer.
  /// Lets our reader skip the surface-only path and enumerate real files,
  /// while external readers see a plain (valid-superblock) image with an
  /// opaque segment region.</summary>
  internal static readonly byte[] WriterMagic = "NILFS1WB"u8.ToArray();

  /// <summary>Where the segment begins on disk (right after the 1024 + 1024
  /// boot / superblock region).</summary>
  internal const int SegmentStart = 2048;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Adds a file to the image. Subdirectory paths are encoded with
  /// '/' separators in <paramref name="name"/>.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    _files.Add((name.Replace('\\', '/'), data));
  }

  /// <summary>Builds the NILFS v1 image. <paramref name="blockSize"/> drives the
  /// superblock's <c>s_log_block_size</c> field (must be a power of two between 1024
  /// and 65536). <paramref name="volumeLabel"/> is stored in the superblock's
  /// volume-label area (NILFS v1 has a 16-byte label slot at offset 0x80).
  /// <paramref name="enableChecksum"/> sets the corresponding feature flag bit.
  /// </summary>
  public byte[] Build(int blockSize = 4096, int segmentSize = 0, string? volumeLabel = null, bool enableChecksum = false) {
    if (blockSize < 1024 || blockSize > 65536 || (blockSize & (blockSize - 1)) != 0)
      throw new ArgumentException("blockSize must be a power of two in [1024, 65536].", nameof(blockSize));
    if (segmentSize < 0) throw new ArgumentException("segmentSize must be >= 0.", nameof(segmentSize));
    var logBlockSize = (uint)(System.Numerics.BitOperations.Log2((uint)blockSize) - 10);

    // Compute payload byte count.
    var dirSize = ComputeDirectoryBytes();
    var dataSize = 0L;
    foreach (var (_, data) in _files) dataSize += data.LongLength;
    var bodyBytes = WriterMagic.Length + 8 + dirSize + dataSize;
    // Pad image to at least 64 KB and to a multiple of blockSize so external
    // tools that walk in block units don't read past EOF.
    var minImageBytes = Math.Max(64 * 1024L, SegmentStart + bodyBytes + blockSize);
    var imageBytes = (long)((minImageBytes + blockSize - 1) / blockSize) * blockSize;

    var img = new byte[imageBytes];

    // ── Superblock at offset 1024 ──────────────────────────────────────────
    var sb = img.AsSpan(1024);
    BinaryPrimitives.WriteUInt32LittleEndian(sb, 1u);                                // s_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(sb[4..], 0);                            // s_minor_rev_level
    BinaryPrimitives.WriteUInt16LittleEndian(sb[6..], 0x3434);                       // s_magic
    BinaryPrimitives.WriteUInt16LittleEndian(sb[8..], 1024);                         // s_bytes
    BinaryPrimitives.WriteUInt16LittleEndian(sb[10..], (ushort)(enableChecksum ? 1 : 0)); // s_flags
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x14..], logBlockSize);              // s_log_block_size
    // Segment size: caller may override; default = 8 blocks per segment, capped
    // to at least one block.
    var actualSegBlocks = (uint)Math.Max(1, segmentSize > 0 ? segmentSize / blockSize : 8);
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x18..], 1ul);                       // s_nsegments
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x20..], (ulong)imageBytes);         // s_dev_size
    BinaryPrimitives.WriteUInt32LittleEndian(sb[0x30..], actualSegBlocks);           // s_blocks_per_segment
    BinaryPrimitives.WriteUInt64LittleEndian(sb[0x38..], 1ul);                       // s_last_cno

    // Volume label at +0x80 (16 bytes, ASCII, NUL-padded).
    if (!string.IsNullOrEmpty(volumeLabel)) {
      var lbl = Encoding.ASCII.GetBytes(volumeLabel);
      var copyLen = Math.Min(16, lbl.Length);
      lbl.AsSpan(0, copyLen).CopyTo(sb[0x80..(0x80 + copyLen)]);
    }

    // ── Segment / directory / payload at SegmentStart ──────────────────────
    var seg = img.AsSpan(SegmentStart);
    WriterMagic.CopyTo(seg);
    BinaryPrimitives.WriteInt64LittleEndian(seg[WriterMagic.Length..], dirSize);

    var dirOffset = WriterMagic.Length + 8;
    var payloadOffset = dirOffset + (int)dirSize;
    var payloadCursor = 0L;
    foreach (var (name, data) in _files) {
      // Directory entry layout (variable-size):
      //   u32 name_len, byte[name_len] name (UTF-8),
      //   u64 payload_offset (relative to start of payload region),
      //   u64 size.
      var nameBytes = Encoding.UTF8.GetBytes(name);
      BinaryPrimitives.WriteUInt32LittleEndian(seg[dirOffset..], (uint)nameBytes.Length);
      nameBytes.CopyTo(seg[(dirOffset + 4)..]);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length)..], payloadCursor);
      BinaryPrimitives.WriteInt64LittleEndian(seg[(dirOffset + 4 + nameBytes.Length + 8)..], data.LongLength);
      dirOffset += 4 + nameBytes.Length + 16;

      // Copy payload.
      data.CopyTo(seg[(payloadOffset + (int)payloadCursor)..]);
      payloadCursor += data.LongLength;
    }

    return img;
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
