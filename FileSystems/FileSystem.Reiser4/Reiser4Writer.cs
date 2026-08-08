#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.DiskImage;

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

  /// <summary>The root directory's own object id, and the locality above it.</summary>
  /// <remarks>Fixed: the tree this writer starts from is a byte-exact mkfs capture.</remarks>
  private const ulong RootLocality = 0x29;
  private const ulong RootObjectId = 0x2a;

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

  // ── Payload area (workbench-layout) ────────────────────────────────────────────
  //
  // The reserved blocks above are byte-exact mkfs.reiser4 captures describing an
  // empty tree, and the tree's own item plugins (extent40 bodies keyed by file
  // offset, cde40 directory units) are not reproduced here. Files therefore live
  // in a workbench-owned area past the reserved blocks, announced by a marker in
  // the master superblock's spare region and described by a chained directory.
  //
  // The format's own accounting stays coherent: every block the payload occupies
  // is marked allocated in the block-allocator bitmap and subtracted from
  // sb_free_blocks, so an allocator walking the volume never hands the same block
  // out twice. What this layout is NOT is a reiser4 storage tree, so the payload
  // is invisible to any reader of the format — the same honest scope the
  // workbench's OpenVMS and AmigaPFS writers declare.
  //
  // Measured, not assumed. reiser4progs builds from source without root, and its
  // tools read an image on their own — no kernel driver exists to mount against:
  //
  //   fsck.reiser4 --check -a -f volume.img     → exits 0, the volume is well formed
  //   debugfs.reiser4 -k / volume.img           → the root holds "." and ".." alone
  //   debugfs.reiser4 -t volume.img             → the same two nodes a native volume
  //                                               has, item for item
  //
  // So what this writes is not a volume a reader can tell from an empty one that
  // mkfs.reiser4 made — it is one. What it is not is a volume holding the files it
  // was given, and that is the whole of the gap.
  //
  // What closing it takes, from the plugin sources rather than from memory. A leaf
  // node is a 28-byte header — plugin id, item count, free space, free-space start,
  // the magic 0x52344653, the mkfs id, a flush id, flags, level — then item bodies
  // upward from 28 and an array of item headers growing downward from the node's
  // end, each header a key, an offset, flags and a plugin id. A file needs three
  // things put in that: a stat40 item carrying the light-weight, unix and
  // plugin-set extensions the root's own already shows; an entry added to the
  // root's cde40 item, keyed by the r5 hash of the name; and extent40 items keyed
  // by offset for its blocks. The block allocator bitmap and the superblock's free
  // count already move with the payload area and would move with these instead.
  //
  // A key is four little-endian words: the locality in the top sixty bits of the
  // first with the item type in its low four, an ordering, the object id, and an
  // offset. A file's stat data and its body share locality, ordering and object id
  // and differ only in that type and in the offset.
  //
  // A directory entry's key carries the name itself rather than a hash of it, for
  // any name of twenty-three characters or fewer — which is every name this writer
  // produces. The bytes pack big-endian into the ordering starting one byte in,
  // then into the object id, then into the offset; the top seven bits of the
  // ordering hold a fibre, which is the last character of the name when the one
  // before it is a dot and zero otherwise. Checked rather than assumed: packing
  // ".." that way gives 0x2e2e0000000000, which is what a volume made by
  // mkfs.reiser4 has on it. A name held in its key is stored nowhere else, so its
  // entry is the twenty-four bytes of the target's key alone — which is the spacing
  // a real volume shows between "." and "..".
  //
  // fsck.reiser4 gates it and debugfs.reiser4 -t reads back what was written, which
  // is the same pair of tools that turned four supposed kernel limits in NILFS2
  // into four bugs of this project's own.

  /// <summary>Marker written at <see cref="MasterPayloadMarkerOff" /> of the master superblock.</summary>
  /// <remarks>The value spells nothing: a marker that reads as words names whoever chose them.</remarks>
  internal static readonly byte[] PayloadMarker =
    [0x8D, 0x17, 0x0C, 0xE1, 0x93, 0x1A, 0x0F, 0xB6, 0x81];

  /// <summary>Offset in the master superblock of the marker, past uuid and label.</summary>
  internal const int MasterPayloadMarkerOff = 52;

  /// <summary>Offset in the master superblock of the first directory block number (d64).</summary>
  internal const int MasterPayloadDirOff = MasterPayloadMarkerOff + 12;

  /// <summary>Magic at the head of every payload directory block.</summary>
  internal static readonly byte[] DirMagic =
    [0x9E, 0x0B, 0x14, 0xA7, 0x02, 0xDB, 0x18, 0x8F];

  /// <summary>Directory block head: magic, next-block link, entry count.</summary>
  internal const int DirHeadSize = 8 + 8 + 4;

  /// <summary>One directory entry: a 224-byte name, then the first data block and the byte length.</summary>
  internal const int DirNameLength = 224;
  internal const int DirEntrySize = DirNameLength + 8 + 8;
  internal const int DirEntriesPerBlock = (BlockSize - DirHeadSize) / DirEntrySize;

  /// <summary>
  /// Blocks one bitmap block accounts for. The block's first four bytes hold the
  /// adler32 of the rest, so the bit array is four bytes short of the block.
  /// </summary>
  internal const ulong BlocksPerBitmap = (ulong)(BlockSize - 4) * 8;

  private readonly List<(string Name, FilePayload Payload)> _files = [];

  /// <summary>Adds a regular file to the payload area.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, FilePayload.FromBytes(data)));
  }

  /// <summary>Adds a file whose bytes are pulled from <paramref name="openStream" /> as the image is written.</summary>
  public void AddStreamingFile(string name, long size, Func<Stream> openStream) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(openStream);
    this._files.Add((name, FilePayload.FromStream(size, openStream)));
  }

  /// <summary>
  /// Smallest block count that holds <paramref name="fileSizes" />: the reserved
  /// blocks, the directory chain, every file's data blocks, and the bitmap blocks
  /// interleaved among them.
  /// </summary>
  public static ulong EstimateBlockCount(IEnumerable<long> fileSizes) {
    ArgumentNullException.ThrowIfNull(fileSizes);
    var sizes = fileSizes as IReadOnlyCollection<long> ?? [.. fileSizes];
    var blocks = ReservedBlockCount;
    blocks += (ulong)((sizes.Count + DirEntriesPerBlock - 1) / Math.Max(1, DirEntriesPerBlock));
    foreach (var size in sizes)
      blocks += (ulong)((size + BlockSize - 1) / BlockSize);
    blocks += blocks / BlocksPerBitmap + 2;   // the strided bitmap blocks
    return Math.Max(MinBlockCount, blocks + blocks / 20);
  }

  /// <summary>True when <paramref name="block" /> holds a block-allocator bitmap.</summary>
  private static bool IsBitmapBlock(ulong block)
    => block == 18 || (block != 0 && block % BlocksPerBitmap == 0);

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

    var uuid = this.Uuid ?? Guid.NewGuid().ToByteArray(bigEndian: true);
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


    // ── Backup record (block 22) ─────────────────────────────────────────
    uuid.AsSpan().CopyTo(blk22.AsSpan(BackupUuidOff, 16));
    Array.Clear(blk22, BackupLabelOff, 16);
    label.AsSpan().CopyTo(blk22.AsSpan(BackupLabelOff, 16));
    BinaryPrimitives.WriteUInt64LittleEndian(blk22.AsSpan(BackupF40BlockCountOff, 8), blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(blk22.AsSpan(BackupF40MkfsIdOff, 4), mkfsId);

    // ── Tree nodes — twig (block 23) and leaf (block 24) ─────────────────
    BinaryPrimitives.WriteUInt32LittleEndian(blk23.AsSpan(NodeMkfsIdOff, 4), mkfsId);
    BinaryPrimitives.WriteUInt32LittleEndian(blk24.AsSpan(NodeMkfsIdOff, 4), mkfsId);

    // ── Payload area: directory chain, then each file's data blocks ───────
    // Both step over the strided bitmap blocks, which the bitmap pass below then
    // marks along with everything else in use.
    var cursor = ReservedBlockCount;
    ulong Alloc() {
      while (IsBitmapBlock(cursor)) ++cursor;
      if (cursor >= blocks)
        throw new IOException($"Reiser4: a {blocks:N0}-block image has no room left for the payload.");
      return cursor++;
    }

    var dirBlockCount = (this._files.Count + DirEntriesPerBlock - 1) / Math.Max(1, DirEntriesPerBlock);
    var dirBlocks = new List<ulong>(dirBlockCount);
    for (var i = 0; i < dirBlockCount; ++i)
      dirBlocks.Add(Alloc());

    var payloads = new DeferredPayloads();
    var entries = new List<(string Name, ulong Block, long Size)>(this._files.Count);
    // The same runs the payload area is made of become the extents of the file in
    // the tree — one file's bytes, described twice, sitting in one place.
    var treeFiles = new List<Reiser4Tree.Entry>(this._files.Count);
    var nextObjectId = RootObjectId + 1;
    foreach (var (name, payload) in this._files) {
      var need = (payload.Size + BlockSize - 1) / BlockSize;
      var first = need > 0 ? Alloc() : 0UL;
      // A file's blocks are consecutive apart from any bitmap they straddle, so
      // the body is recorded as one payload per contiguous stretch.
      var runs = new List<Reiser4Tree.Run>();
      var runStart = first;
      var runBlocks = need > 0 ? 1L : 0L;
      var written = 0L;
      for (var i = 1L; i < need; ++i) {
        var next = Alloc();
        if (next == runStart + (ulong)runBlocks) { ++runBlocks; continue; }
        AddRun(payloads, payload, runStart, runBlocks, ref written);
        runs.Add(new Reiser4Tree.Run(runStart, (ulong)runBlocks));
        runStart = next;
        runBlocks = 1;
      }
      if (runBlocks > 0) {
        AddRun(payloads, payload, runStart, runBlocks, ref written);
        runs.Add(new Reiser4Tree.Run(runStart, (ulong)runBlocks));
      }

      entries.Add((name, first, payload.Size));
      if (name.Length <= Reiser4Tree.MaxInlineNameLength)
        treeFiles.Add(new Reiser4Tree.Entry {
          Name = name, ObjectId = nextObjectId++, Size = payload.Size, Runs = runs,
        });
    }

    // ── The tree itself ──────────────────────────────────────────────────
    // Every file gets a stat data and its extents, and the root directory an entry
    // naming it — so what the volume holds is what a reader of the format finds,
    // not only what our own reader knows to look for.
    Reiser4Tree.Build(blk24, BlockSize, mkfsId, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
      RootLocality, RootObjectId, treeFiles);

    var usedBlocks = cursor;   // every block below the cursor is reserved or payload

    // ── Directory chain ──────────────────────────────────────────────────
    var dirImages = new List<byte[]>(dirBlocks.Count);
    for (var i = 0; i < dirBlocks.Count; ++i) {
      var block = new byte[BlockSize];
      DirMagic.CopyTo(block.AsSpan(0, DirMagic.Length));
      var next = i + 1 < dirBlocks.Count ? dirBlocks[i + 1] : 0UL;
      BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(8, 8), next);
      var take = Math.Min(DirEntriesPerBlock, entries.Count - i * DirEntriesPerBlock);
      BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16, 4), (uint)take);
      for (var j = 0; j < take; ++j) {
        var (name, first, size) = entries[i * DirEntriesPerBlock + j];
        var o = DirHeadSize + j * DirEntrySize;
        var nameBytes = Encoding.UTF8.GetBytes(name);
        nameBytes.AsSpan(0, Math.Min(nameBytes.Length, DirNameLength - 1)).CopyTo(block.AsSpan(o, DirNameLength - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(o + DirNameLength, 8), first);
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(o + DirNameLength + 8, 8), size);
      }
      dirImages.Add(block);
    }

    // ── Master superblock: announce the payload area ─────────────────────
    if (dirBlocks.Count > 0) {
      PayloadMarker.CopyTo(blk16.AsSpan(MasterPayloadMarkerOff, PayloadMarker.Length));
      BinaryPrimitives.WriteUInt64LittleEndian(blk16.AsSpan(MasterPayloadDirOff, 8), dirBlocks[0]);
    }

    // ── Free-block accounting and the bitmap blocks ──────────────────────
    BinaryPrimitives.WriteUInt64LittleEndian(blk17.AsSpan(F40FreeBlocksOff, 8), blocks - usedBlocks);
    var bitmaps = BuildBitmaps(blocks, usedBlocks, blk18);

    // ── Emit the metadata prefix, then the payload ───────────────────────
    var basePosition = output.CanSeek ? output.Position : 0;
    var firstDataBlock = ReservedBlockCount + (ulong)dirBlocks.Count;
    var zero = new byte[BlockSize];
    for (var b = 0UL; b < firstDataBlock; b++) {
      var buf = b switch {
        16 => blk16,
        17 => blk17,
        18 => blk18,
        21 => blk21,
        22 => blk22,
        23 => blk23,
        24 => blk24,
        _ => b >= ReservedBlockCount ? dirImages[(int)(b - ReservedBlockCount)] : zero,
      };
      output.Write(buf, 0, BlockSize);
    }
    output.Flush();

    if (!output.CanSeek) {
      for (var b = firstDataBlock; b < blocks; b++)
        output.Write(zero, 0, BlockSize);
      return;
    }

    output.SetLength(basePosition + totalBytes);
    foreach (var (block, bytes) in bitmaps) {
      if (block < firstDataBlock) continue;   // already emitted in the prefix
      output.Position = basePosition + (long)block * BlockSize;
      output.Write(bytes);
    }
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + totalBytes;
    output.Flush();
  }

  /// <summary>Records one contiguous run of a file's blocks as a payload to copy.</summary>
  private static void AddRun(DeferredPayloads payloads, FilePayload payload,
    ulong firstBlock, long blockCount, ref long written) {
    var length = Math.Min(blockCount * BlockSize, payload.Size - written);
    if (length <= 0) return;
    var skip = written;
    payloads.Add((long)firstBlock * BlockSize,
      FilePayload.FromStream(length, () => SkipTo(payload.Open(), skip)));
    written += blockCount * BlockSize;
  }

  /// <summary>Advances <paramref name="source" /> to <paramref name="offset" />, reading through when it cannot seek.</summary>
  private static Stream SkipTo(Stream source, long offset) {
    if (offset <= 0) return source;
    if (source.CanSeek) {
      source.Position = offset;
      return source;
    }
    var buffer = new byte[64 * 1024];
    var remaining = offset;
    while (remaining > 0) {
      var n = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
      if (n <= 0) break;
      remaining -= n;
    }
    return source;
  }

  /// <summary>
  /// Builds every block-allocator bitmap. Bitmap <c>i</c> covers blocks
  /// <c>[i × BlocksPerBitmap, (i+1) × BlocksPerBitmap)</c>; the first lives at the
  /// reserved block 18 and the rest at the stride boundaries. Blocks below
  /// <paramref name="usedBlocks" /> are allocated, as is every bitmap block and
  /// everything past the end of the filesystem — an out-of-range bit must read as
  /// 1 or the allocator would hand out a block that is not there.
  /// </summary>
  private static List<(ulong Block, byte[] Bytes)> BuildBitmaps(ulong blocks, ulong usedBlocks, byte[] first) {
    var result = new List<(ulong Block, byte[] Bytes)>();
    var count = (blocks + BlocksPerBitmap - 1) / BlocksPerBitmap;
    for (var i = 0UL; i < count; ++i) {
      var block = i == 0 ? 18UL : i * BlocksPerBitmap;
      var buf = i == 0 ? first : new byte[BlockSize];
      Array.Clear(buf, 0, buf.Length);
      var baseBlock = i * BlocksPerBitmap;
      for (var bit = 0UL; bit < BlocksPerBitmap; ++bit) {
        var target = baseBlock + bit;
        if (target >= usedBlocks && target < blocks && !IsBitmapBlock(target)) continue;
        buf[BitmapDataOff + (int)(bit >> 3)] |= (byte)(1 << (int)(bit & 7));
      }
      var adler = Adler32.Compute(buf.AsSpan(BitmapDataOff, BitmapDataLength));
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(BitmapAdlerOff, 4), adler);
      result.Add((block, buf));
    }
    return result;
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
