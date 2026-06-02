#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Qnx6;

/// <summary>
/// WORM writer for QNX6 (Neutrino) filesystem images. Emits a power-safe layout:
/// the primary superblock at file offset 0x2000 plus an identical secondary
/// mirror at the last 512 bytes of the volume. The dual-superblock pairing is
/// the safety contract — a torn write to one copy leaves the other intact.
///
/// On-disk image laid down by <see cref="Build"/>:
///   <code>
///   0x0000..0x1FFF                      boot region (zeroed)
///   0x2000..0x21FF                      primary superblock (qnx6_super_block, 512 B)
///   block 16 (0x4000..)                 inode table (capacity-sized; 128 B per inode)
///   block 17 (0x4400..)                 root directory data block (32-B dirents)
///   block 18..N                         file data, one contiguous extent per file
///   last 512 B of file                  secondary (mirror) superblock
///   </code>
///
/// Inode layout matches <see cref="Qnx6Reader"/>:
///   inode 1 = root directory (size = bytes of dirents, first ptr = root dir block)
///   inode 2..1+N = files (size = file length, first ptr = first data block)
///
/// Field encoding is little-endian to match the reader's
/// <see cref="Qnx6Reader.MagicQnx6"/> probe (0x68191122 LE).
///
/// Constraints (matching reader capability — see <see cref="Qnx6Reader"/>):
///   • The reader walks a single directory block, so the writer caps the root
///     directory at ⌊blockSize/32⌋ = 32 entries.
///   • The reader skips dirents whose name_len &gt; 27. The writer enforces that
///     cap up front — entries with longer names are skipped, mirroring the
///     reader's behaviour. (QNX6's longfile-pointer dirent form is documented
///     in the spec but unreadable through the current Stage-1 reader, so
///     emitting it would yield silently-dropped entries on round-trip.)
///   • Files larger than one block are laid down as one contiguous run starting
///     at the file's first-direct block pointer; the reader's Extract path
///     reads <c>entry.Size</c> bytes from that offset, which spans the whole
///     run.
/// </summary>
public sealed class Qnx6Writer {

  private const int BlockSize = 1024;
  private const int SuperblockOffset = Qnx6Reader.SuperblockOffset; // 0x2000
  private const int InodeSize = Qnx6Reader.InodeSize;               // 128
  private const int SuperblockSize = 512;
  private const int InodeTableBlock = 16;   // 0x4000
  private const int RootDirBlock = 17;      // 0x4400
  private const int FirstDataBlock = 18;    // 0x4800
  private const int DirentSize = 32;
  private const int MaxNameLen = 27;
  internal const int MaxDirents = BlockSize / DirentSize;          // 32

  private sealed record FileEntry(string Name, byte[] Data, uint InodeNumber, uint FirstBlock, uint BlockCount);

  /// <summary>
  /// Builds a complete QNX6 image holding <paramref name="files"/>. Order of
  /// entries in the resulting directory follows the order in
  /// <paramref name="files"/>; duplicate names earn the same fate as the
  /// reader (first-match wins on read). Returns the image bytes ready to be
  /// written to disk or stream.
  /// </summary>
  /// <param name="files">Flat list of <c>(name, data)</c> pairs. Names with
  /// length &gt; 27 are silently skipped to match reader scope.</param>
  /// <exception cref="ArgumentNullException">If <paramref name="files"/> is null.</exception>
  public static byte[] Build(IReadOnlyList<(string Name, byte[] Data)> files) {
    ArgumentNullException.ThrowIfNull(files);

    // Filter to writable entries: name in 1..27 ASCII bytes, no slashes.
    var accepted = new List<(string Name, byte[] Data)>(files.Count);
    foreach (var (n, d) in files) {
      if (string.IsNullOrEmpty(n)) continue;
      // Flatten path: QNX6 reader walks a single directory only — collapse
      // input paths to leaf names so callers pumping in dir/file.txt still
      // get file.txt at the root.
      var leaf = n.Replace('\\', '/');
      var slash = leaf.LastIndexOf('/');
      if (slash >= 0) leaf = leaf.Substring(slash + 1);
      if (string.IsNullOrEmpty(leaf)) continue;
      var nameBytes = Encoding.ASCII.GetByteCount(leaf);
      if (nameBytes is 0 or > MaxNameLen) continue;
      accepted.Add((leaf, d ?? []));
      if (accepted.Count >= MaxDirents) break;
    }

    // Inode layout:
    //   inode 1 (offset 0)     = root directory
    //   inode 2 (offset 128)   = first file
    //   inode 3 (offset 256)   = second file
    //   ...
    // Inode table fills one block (1024 / 128 = 8 inodes per block); we
    // reserve a single block (8 inodes ⇒ 7 files + root). When file count
    // grows past 7 we extend with extra contiguous inode blocks; the reader
    // resolves inode N by absolute offset (inodeTableBlock*blocksize +
    // (N-1)*128), so contiguous extension just works.
    var totalInodes = 1 + accepted.Count;
    var inodeTableBytes = totalInodes * InodeSize;
    var inodeTableBlocks = (inodeTableBytes + BlockSize - 1) / BlockSize;
    // Place root dir directly after the inode table so the root dir block
    // pointer is deterministic; data blocks follow.
    var rootDirBlockActual = (uint)(InodeTableBlock + inodeTableBlocks);
    var dataBlockCursor = rootDirBlockActual + 1;

    // Assign file extents (contiguous, one run per file).
    var planned = new List<FileEntry>(accepted.Count);
    for (var i = 0; i < accepted.Count; i++) {
      var (name, data) = accepted[i];
      var blocks = data.Length == 0 ? 0u : (uint)((data.Length + BlockSize - 1) / BlockSize);
      var firstBlock = blocks == 0 ? 0u : dataBlockCursor;
      planned.Add(new FileEntry(name, data, InodeNumber: (uint)(i + 2), firstBlock, BlockCount: blocks));
      dataBlockCursor += blocks;
    }

    // Total volume size — round up to block boundary, then add one extra
    // block to hold the secondary superblock mirror at the tail.
    var dataBlocksUsed = dataBlockCursor;
    var primaryRegionBlocks = dataBlocksUsed;
    // Need room for: blocks 0..primaryRegionBlocks-1, plus 512 trailing bytes
    // for the secondary superblock. Pad to the nearest block boundary above
    // the secondary so the file size is block-aligned.
    var totalSizeBytes = (long)primaryRegionBlocks * BlockSize + SuperblockSize;
    // Round up to the next block boundary so the image is block-aligned.
    var rem = totalSizeBytes % BlockSize;
    if (rem != 0) totalSizeBytes += BlockSize - rem;
    var image = new byte[totalSizeBytes];

    // ── Primary superblock ─────────────────────────────────────────────────
    WriteSuperblock(
      image.AsSpan(SuperblockOffset, SuperblockSize),
      inodeTablePtr: InodeTableBlock,
      numInodes: (uint)totalInodes,
      numBlocks: (uint)(totalSizeBytes / BlockSize),
      freeInodes: (uint)Math.Max(0, MaxDirents - accepted.Count),
      freeBlocks: 0,
      serial: 1,
      ctime: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ── Inode table ────────────────────────────────────────────────────────
    var inodeTableOff = InodeTableBlock * BlockSize;

    // Root directory inode (inode 1, offset 0 in inode table).
    var dirSize = (ulong)(accepted.Count * DirentSize);
    WriteInode(
      image.AsSpan(inodeTableOff, InodeSize),
      size: dirSize,
      mode: 0x41ED, // S_IFDIR | 0755
      firstBlock: rootDirBlockActual);

    // File inodes (inode 2..N).
    foreach (var entry in planned) {
      var inodeOff = inodeTableOff + (entry.InodeNumber - 1) * InodeSize;
      WriteInode(
        image.AsSpan((int)inodeOff, InodeSize),
        size: (ulong)entry.Data.LongLength,
        mode: 0x81A4, // S_IFREG | 0644
        firstBlock: entry.FirstBlock);
    }

    // ── Root directory dirents ─────────────────────────────────────────────
    var dirOff = rootDirBlockActual * BlockSize;
    for (var i = 0; i < planned.Count; i++) {
      var entry = planned[i];
      var direntOff = dirOff + i * DirentSize;
      var dirent = image.AsSpan((int)direntOff, DirentSize);
      BinaryPrimitives.WriteUInt32LittleEndian(dirent, entry.InodeNumber);
      var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
      dirent[4] = (byte)nameBytes.Length;
      nameBytes.CopyTo(dirent.Slice(5));
      // Remaining bytes of the dirent are left zero (the spec pads with NULs).
    }

    // ── File data extents ──────────────────────────────────────────────────
    foreach (var entry in planned) {
      if (entry.Data.Length == 0) continue;
      var off = (long)entry.FirstBlock * BlockSize;
      entry.Data.CopyTo(image.AsSpan((int)off));
    }

    // ── Secondary superblock mirror ────────────────────────────────────────
    // Mirror identical bytes at the tail. This is the power-safe contract:
    // primary and secondary are byte-identical, so torn-write detection just
    // diffs the two halves.
    var secondaryOff = image.Length - SuperblockSize;
    image.AsSpan(SuperblockOffset, SuperblockSize)
      .CopyTo(image.AsSpan(secondaryOff, SuperblockSize));

    return image;
  }

  private static void WriteSuperblock(
    Span<byte> sb,
    uint inodeTablePtr,
    uint numInodes,
    uint numBlocks,
    uint freeInodes,
    uint freeBlocks,
    ulong serial,
    uint ctime) {

    // +0x00 sb_magic
    BinaryPrimitives.WriteUInt32LittleEndian(sb, Qnx6Reader.MagicQnx6);
    // +0x04 sb_checksum — left zero; the reader does not validate it. (A
    //                    real driver computes CRC32 over the rest of the SB.)
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x04), 0);
    // +0x08 sb_serial
    BinaryPrimitives.WriteUInt64LittleEndian(sb.Slice(0x08), serial);
    // +0x10 sb_ctime
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x10), ctime);
    // +0x14 sb_atime
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x14), ctime);
    // +0x18 sb_flags
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x18), 0);
    // +0x1C sb_version1
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x1C), 1);
    // +0x1E sb_version2
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x1E), 0);
    // +0x20 sb_volumeid — leave zero (caller can override for real volumes).
    // +0x30 sb_blocksize
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x30), BlockSize);
    // +0x34 sb_num_inodes
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x34), numInodes);
    // +0x38 sb_free_inodes
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x38), freeInodes);
    // +0x3C sb_num_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x3C), numBlocks);
    // +0x40 sb_free_blocks
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x40), freeBlocks);
    // +0x44 sb_num_levels
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x44), 0);
    // +0x46 sb_indir_levs
    BinaryPrimitives.WriteUInt16LittleEndian(sb.Slice(0x46), 0);
    // +0x48 sb_inode_root: size (u64) + 16×ptr (u32) + 4×levels (u8) + 12 pad.
    //         size: total inode table bytes — we record the table extent
    //               here so a recovery tool can size the inode array.
    BinaryPrimitives.WriteUInt64LittleEndian(sb.Slice(0x48), (ulong)numInodes * InodeSize);
    // First ptr at +0x50 = inode table block.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x50), inodeTablePtr);
    // Remaining ptrs and levels stay zero (we use a flat array, not a B-tree).
  }

  private static void WriteInode(Span<byte> inode, ulong size, ushort mode, uint firstBlock) {
    // +0x00 di_size
    BinaryPrimitives.WriteUInt64LittleEndian(inode, size);
    // +0x08..0x1F  uid/gid/times — left zero (epoch).
    // +0x20 di_mode
    BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0x20), mode);
    // +0x22 di_ext_mode
    BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0x22), 0);
    // +0x24 di_block_ptr[0] — first direct pointer.
    BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(0x24), firstBlock);
    // +0x28..0x63 di_block_ptr[1..15] — left zero. The reader only consults
    //                                   ptr[0]; large files are laid out
    //                                   contiguously starting at ptr[0].
    // +0x64 di_filelevels — 0 (no indirection — direct ptrs only).
    inode[0x64] = 0;
    // +0x65 di_status — 0x01 = allocated.
    inode[0x65] = 0x01;
    // +0x66..0x73 di_unknown — zero.
  }
}
