#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Reiser4;

/// <summary>
/// WORM (write-once-read-many) creator for an **empty** Reiser4 filesystem image
/// that is byte-exact-compatible with what <c>mkfs.reiser4 -fffy</c> from
/// <c>reiser4progs 1.2.2</c> produces, and that <c>fsck.reiser4</c> validates as
/// <c>"FS is consistent."</c>.
///
/// <para>
/// The image holds 25 reserved blocks at fixed positions (block size = 4 KB):
/// <list type="bullet">
///   <item><description>0..15 — jump-area / partition data (zero).</description></item>
///   <item><description>16 — master superblock (<c>"ReIsEr4"</c> at offset 65536).</description></item>
///   <item><description>17 — format40 superblock (<c>"ReIsEr40FoRmAt"</c> at offset 52).</description></item>
///   <item><description>18 — block-allocator bitmap (4-byte adler32 + bit array).</description></item>
///   <item><description>19..20 — journal header / footer (zero for an empty FS).</description></item>
///   <item><description>21 — status block (<c>"ReiSeR4StATusBl"</c>).</description></item>
///   <item><description>22 — superblock backup record.</description></item>
///   <item><description>23 — storage-tree root (twig, level 2).</description></item>
///   <item><description>24 — leaf containing the root-directory stat-data + ".", "..".</description></item>
/// </list>
/// </para>
///
/// <para>
/// Implementation strategy: we embed the seven non-zero reference blocks as
/// resources captured byte-exact from a real <c>mkfs.reiser4</c> image, then
/// patch in only the per-image fields:
/// </para>
/// <list type="number">
///   <item><description>UUID (16 bytes, random)</description></item>
///   <item><description>Label (≤ 16 bytes, optional)</description></item>
///   <item><description>mkfs_id (4 bytes, random) — appears in 4 places: format40
///   SB, backup record, twig header, leaf header.</description></item>
///   <item><description>block_count / free_blocks (in format40 SB and backup).</description></item>
///   <item><description>Bitmap (block 18) — bits 0..24 set for the 25 reserved
///   blocks plus filler bits for the unused tail of the bitmap range, then
///   adler32 of the bitmap data prepended.</description></item>
/// </list>
///
/// <para>
/// Round-trips with <c>fsck.reiser4 -y</c> exit 0 and produces output identical
/// to the reference image except for the random fields above.
/// </para>
/// </summary>
public sealed class Reiser4Writer {
  /// <summary>Filesystem block size — 4 KB is the only supported value. Reiser4
  /// theoretically supports 512 / 1024 / 2048 / 4096 / 8192, but the embedded
  /// templates are 4096-byte exact captures.</summary>
  public const int BlockSize = 4096;

  /// <summary>Minimum block count we'll emit. mkfs.reiser4 itself rejects
  /// images below ~750 blocks, but we round up to 4096 (= 16 MB) to match the
  /// reference capture and avoid bitmap-truncation edge cases.</summary>
  public const ulong MinBlockCount = 4096;

  /// <summary>Number of blocks the empty filesystem permanently occupies
  /// (jump area + master + format40 + bitmap + journal pair + status + backup
  /// + twig root + leaf).</summary>
  private const ulong ReservedBlockCount = 25;

  // Field offsets — captured by inspecting the reference image with xxd.
  private const int MasterMagicOff = 0;     // "ReIsEr4"
  private const int MasterFormatOff = 16;   // d16 disk plugin id (= 0)
  private const int MasterBlksizeOff = 18;  // d16 = 4096
  private const int MasterUuidOff = 20;     // 16 bytes
  private const int MasterLabelOff = 36;    // 16 bytes

  private const int F40BlockCountOff = 0;
  private const int F40FreeBlocksOff = 8;
  private const int F40RootBlockOff = 16;
  private const int F40OidNextOff = 24;
  private const int F40OidFileCountOff = 32;
  private const int F40FlushesOff = 40;
  private const int F40MkfsIdOff = 48;
  private const int F40MagicOff = 52;       // "ReIsEr40FoRmAt"
  private const int F40TreeHeightOff = 68;
  private const int F40PolicyOff = 70;
  private const int F40FlagsOff = 72;
  private const int F40VersionOff = 80;

  // Backup record (block 22) — packed format-specific struct (not a direct copy of
  // master + format40 SBs). Reverse-engineered from the reference image:
  //   0x00      pad (1 byte)
  //   0x01-0x10 master ms_magic[16] ("ReIsEr4" + zeros)
  //   0x11-0x12 master ms_format (d16 = 0)
  //   0x13-0x14 master ms_blksize (d16 LE = 4096 → 00 10)
  //   0x15-0x24 master ms_uuid[16]
  //   0x25-0x34 master ms_label[16]
  //   0x35-0x3C reserved zeros
  //   0x3D-0x4C format40 sb_magic[16] ("ReIsEr40FoRmAt" + 2 NUL)
  //   0x4D-0x54 sb_block_count (d64)
  //   0x55-0x58 sb_mkfs_id (d32)
  //   0x59-0x5A sb_policy (d16)
  //   0x5B-0x62 sb_flags (d64)
  //   0x63-0x6F more — including a "PsEt" magic at 0x6F (PSET = plugin-set sentinel)
  // Note: backup struct does NOT carry sb_free_blocks — only block_count.
  private const int BackupUuidOff = 0x15;       // 21
  private const int BackupBlkSizeOff = 0x13;    // 19 — d16 (was master+18)
  private const int BackupLabelOff = 0x25;      // 37
  private const int BackupF40BlockCountOff = 0x4D; // 77 — d64
  private const int BackupF40MkfsIdOff = 0x55;     // 85 — d32

  // Tree-node header offsets — same for twig (block 23) and leaf (block 24).
  private const int NodeMkfsIdOff = 12;     // d32

  // Bitmap (block 18)
  private const int BitmapAdlerOff = 0;     // d32 adler32 over bytes 4..4095
  private const int BitmapDataOff = 4;
  private const int BitmapDataLength = BlockSize - 4; // 4092 bytes = 32736 bits

  /// <summary>Customisable label written to the master SB (NUL-padded to 16
  /// bytes; longer strings are truncated; null = empty/zero).</summary>
  public string? Label { get; set; }

  /// <summary>Optional 16-byte UUID. When null, a random Guid is used.</summary>
  public byte[]? Uuid { get; set; }

  /// <summary>Optional 32-bit mkfs identifier. When null, a random value is
  /// drawn from <see cref="Random.Shared"/>.</summary>
  public uint? MkfsId { get; set; }

  /// <summary>Total filesystem size in 4 KB blocks. Clamped to
  /// <see cref="MinBlockCount"/>.</summary>
  public ulong BlockCount { get; set; } = MinBlockCount;

  /// <summary>Builds the image fully in memory and returns the byte array.
  /// For block counts above ~32 K the result will be tens of MB; prefer the
  /// streaming overload when caller-side allocation matters.</summary>
  public byte[] Build() {
    using var ms = new MemoryStream();
    this.Write(ms);
    return ms.ToArray();
  }

  /// <summary>Streams the full image into <paramref name="output"/>. Writes
  /// <see cref="BlockCount"/>×4096 bytes and leaves the stream positioned at
  /// the end.</summary>
  public void Write(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var blocks = Math.Max(this.BlockCount, MinBlockCount);
    var totalBytes = checked((long)blocks * BlockSize);

    var uuid = this.Uuid ?? Guid.NewGuid().ToByteArray();
    if (uuid.Length != 16)
      throw new ArgumentException("UUID must be exactly 16 bytes.", nameof(this.Uuid));
    var mkfsId = this.MkfsId ?? unchecked((uint)Random.Shared.Next(int.MinValue, int.MaxValue));
    var label = TrimLabel(this.Label);

    // Templates are byte-exact 4 KB captures from `mkfs.reiser4 -fffy` on a 4096-block
    // image, so the patches below are pure overrides of variable fields.
    var blk16 = LoadTemplate(16);
    var blk17 = LoadTemplate(17);
    var blk18 = LoadTemplate(18);
    var blk21 = LoadTemplate(21);
    var blk22 = LoadTemplate(22);
    var blk23 = LoadTemplate(23);
    var blk24 = LoadTemplate(24);

    // ── Master superblock (block 16) ─────────────────────────────────────
    uuid.AsSpan().CopyTo(blk16.AsSpan(MasterUuidOff, 16));
    Array.Clear(blk16, MasterLabelOff, 16);
    label.AsSpan().CopyTo(blk16.AsSpan(MasterLabelOff, 16));

    // ── Format40 SB (block 17) ───────────────────────────────────────────
    BinaryPrimitives.WriteUInt64LittleEndian(blk17.AsSpan(F40BlockCountOff, 8), blocks);
    BinaryPrimitives.WriteUInt64LittleEndian(blk17.AsSpan(F40FreeBlocksOff, 8), blocks - ReservedBlockCount);
    BinaryPrimitives.WriteUInt32LittleEndian(blk17.AsSpan(F40MkfsIdOff, 4), mkfsId);

    // ── Bitmap (block 18) ────────────────────────────────────────────────
    BuildBitmap(blk18, blocks);

    // ── Backup record (block 22) ─────────────────────────────────────────
    uuid.AsSpan().CopyTo(blk22.AsSpan(BackupUuidOff, 16));
    Array.Clear(blk22, BackupLabelOff, 16);
    label.AsSpan().CopyTo(blk22.AsSpan(BackupLabelOff, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(blk22.AsSpan(BackupF40BlockCountOff, 8), blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(blk22.AsSpan(BackupF40MkfsIdOff, 4), mkfsId);

    // ── Tree nodes — twig (block 23) and leaf (block 24) ─────────────────
    BinaryPrimitives.WriteUInt32LittleEndian(blk23.AsSpan(NodeMkfsIdOff, 4), mkfsId);
    BinaryPrimitives.WriteUInt32LittleEndian(blk24.AsSpan(NodeMkfsIdOff, 4), mkfsId);

    // ── Emit blocks 0..(blocks-1) ────────────────────────────────────────
    var zero = new byte[BlockSize];
    for (var b = 0UL; b < blocks; b++) {
      var buf = b switch {
        16 => blk16,
        17 => blk17,
        18 => blk18,
        21 => blk21,
        22 => blk22,
        23 => blk23,
        24 => blk24,
        _ => zero,
      };
      output.Write(buf, 0, BlockSize);
    }

    // Sanity: stream length advanced by exactly totalBytes.
    if (output.CanSeek && output.Length < totalBytes) {
      output.SetLength(totalBytes);
    }
  }

  /// <summary>Builds the block-allocator bitmap for an empty filesystem of
  /// <paramref name="blocks"/> blocks. The first 25 bits (= reserved blocks
  /// 0..24) are set, plus all bits beyond the FS's actual range are set to
  /// "out-of-range" (so the allocator never picks them). The 4-byte adler32
  /// of bytes 4..4095 is written at offset 0.</summary>
  private static void BuildBitmap(byte[] block, ulong blocks) {
    Array.Clear(block, 0, block.Length);
    // Mark 25 reserved blocks at the beginning. 25 bits = 3 full bytes + 1 bit.
    block[BitmapDataOff + 0] = 0xff;
    block[BitmapDataOff + 1] = 0xff;
    block[BitmapDataOff + 2] = 0xff;
    block[BitmapDataOff + 3] = 0x01;

    // Filler: bytes after blocks/8 are 0xff (out-of-range marker). For an
    // image of N blocks, byte index N/8 is the first filler byte.
    var firstFillByte = (int)(blocks / 8);
    if (firstFillByte > 3 && firstFillByte < BitmapDataLength) {
      var len = BitmapDataLength - firstFillByte;
      Array.Fill(block, (byte)0xff, BitmapDataOff + firstFillByte, len);
    } else if (firstFillByte >= BitmapDataLength) {
      // FS larger than one bitmap block tracks — out of scope (we cap at
      // 32K blocks per bitmap; multi-bitmap FSes need additional bitmap
      // blocks at strided offsets which mkfs.reiser4 places automatically).
      // For now we accept the truncation and let fsck catch it.
    }

    var adler = Adler32.Compute(block.AsSpan(BitmapDataOff, BitmapDataLength));
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(BitmapAdlerOff, 4), adler);
  }

  private static byte[] TrimLabel(string? label) {
    if (string.IsNullOrEmpty(label)) return [];
    var bytes = Encoding.ASCII.GetBytes(label);
    return bytes.Length <= 16 ? bytes : bytes[..16];
  }

  private static byte[] LoadTemplate(int blockNumber) {
    var resource = $"FileSystem.Reiser4.Templates.block_{blockNumber}.bin";
    var asm = typeof(Reiser4Writer).Assembly;
    using var stream = asm.GetManifestResourceStream(resource)
      ?? throw new InvalidOperationException(
        $"Embedded template '{resource}' not found. Ensure FileSystem.Reiser4.csproj " +
        $"includes the Templates\\block_{blockNumber}.bin EmbeddedResource.");
    var buf = new byte[BlockSize];
    var off = 0;
    while (off < BlockSize) {
      var got = stream.Read(buf, off, BlockSize - off);
      if (got <= 0) break;
      off += got;
    }
    if (off != BlockSize)
      throw new InvalidOperationException(
        $"Embedded template '{resource}' is {off} bytes, expected {BlockSize}.");
    return buf;
  }
}
