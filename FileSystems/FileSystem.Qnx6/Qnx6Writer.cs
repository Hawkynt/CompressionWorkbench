#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Qnx6;

/// <summary>
/// WORM writer for QNX6 (Neutrino) filesystem images. Emits a power-safe layout:
/// the primary superblock at file offset 0x2000 plus an identical secondary
/// mirror at the last 512 bytes of the volume. The dual-superblock pairing is
/// the safety contract — a torn write to one copy leaves the other intact.
///
/// On-disk image laid down by <see cref="Build(IReadOnlyList{ValueTuple{string,byte[]}})"/>:
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

  /// <summary>
  /// The two entries every QNX6 directory opens with. A driver reads the root
  /// directory's first two records and refuses the volume unless they are
  /// named "." and ".." exactly, so they are not optional and they are not
  /// free: they cost two of the entries a directory block holds.
  /// </summary>
  private const int DotEntries = 2;
  internal const int MaxFiles = MaxDirents - DotEntries;

  /// <summary>Pointers an inode holds before it has to point at a block of them.</summary>
  private const int DirectPointers = 16;

  /// <summary>Block pointers one indirect block holds.</summary>
  private const int PointersPerBlock = BlockSize / 4;

  private sealed record FileEntry(
    string Name, FilePayload Payload, uint InodeNumber,
    uint FirstBlock, uint BlockCount, uint IndirectBlock, uint IndirectCount);

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
    return Build(files.Select(f => (f.Name, FilePayload.FromBytes(f.Data))).ToList());
  }

  /// <summary>Materialises an image from payloads that may be streamed.</summary>
  public static byte[] Build(IReadOnlyList<(string Name, FilePayload Payload)> files) {
    var image = BuildCore(files, out var payloads);
    return payloads.Materialise(image);
  }

  /// <summary>
  /// Writes the image into <paramref name="output" />: the blocks the filesystem
  /// populates, then each file's bytes at the block it was allocated. Only a
  /// non-seekable target has to materialise the image, so a seekable one is
  /// bounded by the disk rather than by what a byte[] can address.
  /// </summary>
  public static void WriteTo(Stream output, IReadOnlyList<(string Name, FilePayload Payload)> files) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(files);
    if (!output.CanSeek) {
      var full = Build(files);
      output.Write(full, 0, full.Length);
      return;
    }

    var basePosition = output.Position;
    var image = BuildCore(files, out var payloads);
    image.WriteTo(output);
    payloads.FlushTo(output, basePosition);
    output.Position = basePosition + image.TotalBytes;
    output.Flush();
  }

  private static SparseBlockImage BuildCore(IReadOnlyList<(string Name, FilePayload Payload)> files,
                                            out DeferredPayloads payloads) {
    ArgumentNullException.ThrowIfNull(files);

    // Filter to writable entries: name in 1..27 ASCII bytes, no slashes.
    var accepted = new List<(string Name, FilePayload Payload)>(files.Count);
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
      accepted.Add((leaf, d));
      if (accepted.Count >= MaxFiles) break;
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
    // A block pointer in a QNX6 inode names one block, not the start of a run.
    // Sixteen of them fit inside the inode; past that the inode points at
    // blocks of pointers instead, and says how many levels deep that goes.
    var planned = new List<FileEntry>(accepted.Count);
    for (var i = 0; i < accepted.Count; i++) {
      var (name, payload) = accepted[i];
      var blocks = payload.Size == 0 ? 0u : (uint)((payload.Size + BlockSize - 1) / BlockSize);

      var indirectCount = blocks <= DirectPointers
        ? 0u
        : (blocks + PointersPerBlock - 1) / PointersPerBlock;
      if (indirectCount > DirectPointers)
        throw new InvalidOperationException(
          $"QNX6: '{name}' needs {indirectCount} pointer blocks; an inode holds {DirectPointers}, " +
          "and this writer goes one level deep.");

      var indirectBlock = indirectCount == 0 ? 0u : dataBlockCursor;
      dataBlockCursor += indirectCount;
      var firstBlock = blocks == 0 ? 0u : dataBlockCursor;
      dataBlockCursor += blocks;

      planned.Add(new FileEntry(name, payload, InodeNumber: (uint)(i + 2),
        firstBlock, BlockCount: blocks, indirectBlock, indirectCount));
    }

    // Total volume size — round up to block boundary, then add one extra
    // block to hold the secondary superblock mirror at the tail.
    // The block numbers a QNX6 volume records are not device blocks: the driver
    // adds the boot and superblock areas to every one. So the count in the
    // superblock is the count of blocks after those, and the mirror superblock
    // goes exactly where that count says — not wherever the image happens to
    // end.
    var blocksBefore = Qnx6Geometry.BlocksBefore(BlockSize);
    var filesystemBlocks = (uint)(dataBlockCursor - blocksBefore);
    var mirrorBlock = filesystemBlocks + blocksBefore;
    var totalSizeBytes = mirrorBlock * BlockSize + SuperblockSize;
    var rem = totalSizeBytes % BlockSize;
    if (rem != 0) totalSizeBytes += BlockSize - rem;
    // Only the blocks the filesystem populates are held: file payloads are
    // placed by seek afterwards, so an image past what a byte[] can address
    // costs its metadata rather than its size.
    var image = new SparseBlockImage(BlockSize, totalSizeBytes);
    payloads = new DeferredPayloads();

    // ── Primary superblock ─────────────────────────────────────────────────
    WriteSuperblock(
      image.At(SuperblockOffset, SuperblockSize),
      inodeTablePtr: (uint)(InodeTableBlock - blocksBefore),
      numInodes: (uint)totalInodes,
      numBlocks: filesystemBlocks,
      freeInodes: (uint)Math.Max(0, MaxFiles - accepted.Count),
      freeBlocks: 0,
      serial: 1,
      ctime: (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    // ── Inode table ────────────────────────────────────────────────────────
    var inodeTableOff = InodeTableBlock * BlockSize;

    // Root directory inode (inode 1, offset 0 in inode table).
    var dirSize = (ulong)((accepted.Count + DotEntries) * DirentSize);
    WriteInode(
      image.At(inodeTableOff, InodeSize),
      size: dirSize,
      mode: 0x41ED, // S_IFDIR | 0755
      directory: true,
      levels: 0,
      pointers: [(uint)(rootDirBlockActual - blocksBefore)]);

    // File inodes (inode 2..N).
    foreach (var entry in planned) {
      var inodeOff = inodeTableOff + (entry.InodeNumber - 1) * InodeSize;
      var inode = image.At(inodeOff, InodeSize);

      if (entry.IndirectCount == 0) {
        // Small enough to name every block from inside the inode.
        WriteInode(inode, (ulong)entry.Payload.Size, 0x81A4, directory: false, levels: 0,
          pointers: Enumerable.Range(0, (int)entry.BlockCount)
            .Select(b => (uint)(entry.FirstBlock + b - blocksBefore)).ToArray());
      } else {
        // One level of indirection: the inode names blocks of pointers, and
        // those name the file's blocks.
        for (var p = 0u; p < entry.IndirectCount; ++p) {
          var table = image.At((entry.IndirectBlock + p) * BlockSize, BlockSize);
          for (var k = 0; k < PointersPerBlock; ++k) {
            var logical = p * PointersPerBlock + (uint)k;
            if (logical >= entry.BlockCount) break;
            BinaryPrimitives.WriteUInt32LittleEndian(
              table.Slice(k * 4), (uint)(entry.FirstBlock + logical - blocksBefore));
          }
        }

        WriteInode(inode, (ulong)entry.Payload.Size, 0x81A4, directory: false, levels: 1,
          pointers: Enumerable.Range(0, (int)entry.IndirectCount)
            .Select(b => (uint)(entry.IndirectBlock + b - blocksBefore)).ToArray());
      }
    }

    // ── Root directory dirents ─────────────────────────────────────────────
    var dirOff = rootDirBlockActual * BlockSize;
    WriteDirent(image.At(dirOff, DirentSize), 1, ".");
    WriteDirent(image.At(dirOff + DirentSize, DirentSize), 1, "..");
    for (var i = 0; i < planned.Count; i++) {
      var entry = planned[i];
      var direntOff = dirOff + (i + DotEntries) * DirentSize;
      WriteDirent(image.At(direntOff, DirentSize), entry.InodeNumber, entry.Name);
    }

    // ── File data extents ──────────────────────────────────────────────────
    foreach (var entry in planned) {
      if (entry.Payload.Size == 0) continue;
      payloads.Add((long)entry.FirstBlock * BlockSize, entry.Payload);
    }

    // ── Secondary superblock mirror ────────────────────────────────────────
    // Mirror identical bytes at the tail. This is the power-safe contract:
    // primary and secondary are byte-identical, so torn-write detection just
    // diffs the two halves.
    var secondaryOff = mirrorBlock * BlockSize;
    image.Write(secondaryOff, image.At(SuperblockOffset, SuperblockSize));

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
    // +0x04 sb_checksum — written last, once the rest of the block is final.
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
    // +0x44 sb_allocgroup
    // +0x48 sb_inode_root: size (u64) + 16×ptr (u32) + 4×levels (u8) + 12 pad.
    //         size: total inode table bytes — we record the table extent
    //               here so a recovery tool can size the inode array.
    BinaryPrimitives.WriteUInt64LittleEndian(sb.Slice(0x48), (ulong)numInodes * InodeSize);
    // First ptr at +0x50 = inode table block, as the filesystem numbers it.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x50), inodeTablePtr);
    // Remaining ptrs and levels stay zero (we use a flat array, not a B-tree).
    // The Bitmap, Longfile and Unknown root nodes that follow stay zero too,
    // which reads as "no levels" and is what a driver sanity-checks them for.

    // Last: the checksum, over everything the driver checks — bytes 8 to 511.
    BinaryPrimitives.WriteUInt32LittleEndian(sb.Slice(0x04), Qnx6Geometry.Checksum(sb));
  }

  /// <summary>Writes one 32-byte directory record.</summary>
  private static void WriteDirent(Span<byte> dirent, uint inode, string name) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    BinaryPrimitives.WriteUInt32LittleEndian(dirent, inode);
    dirent[4] = (byte)nameBytes.Length;
    nameBytes.CopyTo(dirent.Slice(5));
    // The rest stays zero; the format pads names with NULs.
  }

  /// <summary>Fills one inode, naming every block it owns.</summary>
  /// <remarks>
  /// Each pointer names a single block. Leaving the rest of them zero — as this
  /// once did, on the assumption that a file's blocks simply follow its first —
  /// gives a file whose first block reads correctly and whose every block after
  /// it reads as whatever happens to sit at the volume's block zero.
  /// </remarks>
  private static void WriteInode(
      Span<byte> inode, ulong size, ushort mode, bool directory, byte levels,
      IReadOnlyList<uint> pointers) {
    // +0x00 di_size
    BinaryPrimitives.WriteUInt64LittleEndian(inode, size);
    // +0x08..0x1F  uid/gid/times — left zero (epoch).
    // +0x20 di_mode
    BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0x20), mode);
    // +0x22 di_ext_mode
    BinaryPrimitives.WriteUInt16LittleEndian(inode.Slice(0x22), 0);
    // +0x24 di_block_ptr[0..15]
    for (var i = 0; i < pointers.Count && i < DirectPointers; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(inode.Slice(0x24 + i * 4), pointers[i]);
    // +0x64 di_filelevels — how many blocks of pointers stand between the
    //                       inode and the file's own blocks.
    inode[0x64] = levels;
    // +0x65 di_status — a directory and a plain file are told apart here as
    //                   well as by the mode, and the two must agree.
    inode[0x65] = directory ? (byte)0x01 : (byte)0x03;
    // +0x66..0x73 di_unknown — zero.
  }
}
