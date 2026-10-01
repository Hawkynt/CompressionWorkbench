#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ocfs2;

/// <summary>
/// Reads an OCFS2 (Oracle Cluster Filesystem 2) image and surfaces the regular
/// files of its directory tree. Works against images produced by the reference
/// <c>mkfs.ocfs2</c> tool as well as the toolkit's own <see cref="Ocfs2Writer"/>.
///
/// The reader is spec-correct against <c>fs/ocfs2/ocfs2_fs.h</c> rather than
/// matching the writer's historical (incorrect) field placement:
/// <list type="bullet">
///   <item><description>Regular inodes carry the <c>INODE01</c> signature; only
///   the block-2 dinode carries <c>OCFSV2</c>.</description></item>
///   <item><description><c>ocfs2_dinode</c> fields: <c>i_size</c> at +0x20,
///   <c>i_mode</c> at +0x28, <c>i_flags</c> at +0x2C, <c>i_blkno</c> at +0x50,
///   <c>i_dyn_features</c> at +0x76, the <c>id2</c> union at +0xC0.</description></item>
///   <item><description>Inline directories/files store an <c>ocfs2_inline_data</c>
///   header (8 bytes: <c>id_count</c> u16 + 6 reserved) at +0xC0, with the actual
///   bytes beginning at +0xC8.</description></item>
///   <item><description><c>ocfs2_extent_rec</c> is 16 bytes: <c>e_cpos</c> (u32),
///   <c>e_leaf_clusters</c> (u16) / reserved / flags, then <c>e_blkno</c> (u64)
///   at +0x08.</description></item>
/// </list>
/// Block size is taken from the superblock (<c>s_blocksize_bits</c>); the root
/// directory block from <c>s_root_blkno</c>. Read-only.
/// </summary>
internal static class Ocfs2Reader {
  private static readonly byte[] InodeSig = "INODE01"u8.ToArray();
  private static readonly byte[] SuperSig = "OCFSV2"u8.ToArray();

  // ocfs2_dinode field offsets (bytes from the start of the dinode block).
  private const int OffSize = 0x20;          // i_size (u64)
  private const int OffFlags = 0x2C;         // i_flags (u32)
  private const int OffDynFeatures = 0x76;   // i_dyn_features (u16)
  internal const int Id2Offset = 0xC0;        // id2 union

  // ocfs2_super_block field offsets relative to id2 (dinode + 0xC0).
  private const int SbRootBlkno = 0x28;
  private const int SbBlocksizeBits = 0x38;
  private const int SbClustersizeBits = 0x3C;

  // i_dyn_features: OCFS2_INLINE_DATA_FL.
  private const ushort DynInlineData = 0x0001;

  // ocfs2_inline_data: id_count (u16) + id_reserved0 (u16) + id_reserved1 (u32)
  // == 8 bytes of header, then id_data[].
  internal const int InlineHeaderLen = 8;

  // Directory entry file types.
  private const byte FtRegFile = 1;
  private const byte FtDir = 2;
  private const byte FtSymlink = 7;

  /// <summary>
  /// True when <paramref name="image"/> looks like an OCFS2 image: the block-2
  /// dinode carries the <c>OCFSV2</c> superblock signature at one of the
  /// plausible block sizes.
  /// </summary>
  public static bool LooksLikeOcfs2(byte[] image) {
    foreach (var bs in Ocfs2Superblock.PlausibleBlockSizes) {
      var off = (long)bs * Ocfs2Superblock.SuperBlockBlkno;
      if (off + SuperSig.Length > image.Length) continue;
      if (image.AsSpan((int)off, SuperSig.Length).SequenceEqual(SuperSig)) return true;
    }
    return false;
  }

  /// <summary>
  /// Returns the names of every entry directly in the root directory (files and
  /// subdirectories alike, excluding "." / ".."). Used by the reverse-gate test
  /// to confirm the reader sees the <c>lost+found</c> directory that
  /// <c>mkfs.ocfs2</c> always creates. Empty when the image is not OCFS2.
  /// </summary>
  public static List<string> ReadRootEntryNames(byte[] image) {
    var names = new List<string>();
    if (!TryReadSuperblock(image, out var blockSize, out var rootBlkno)) return names;

    var rootOff = rootBlkno * blockSize;
    if (rootOff < 0 || rootOff + blockSize > image.Length) return names;
    if (!IsDinode(image, (int)rootOff)) return names;

    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)rootOff + OffDynFeatures, 2));
    var dirSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan((int)rootOff + OffSize, 8));

    if ((dynFeatures & DynInlineData) != 0) {
      var inlineStart = (int)rootOff + Id2Offset + InlineHeaderLen;
      var maxInline = blockSize - Id2Offset - InlineHeaderLen;
      var inlineEnd = inlineStart + (int)Math.Clamp(dirSize, 0, maxInline);
      CollectNames(image, inlineStart, inlineEnd, names);
    } else {
      var perCluster = BlocksPerCluster(image, blockSize);
      foreach (var (_, blkno, clusters, _) in ReadExtents(image, (int)rootOff, blockSize))
        for (long b = 0; b < (long)clusters * perCluster; b++) {
          var blockStart = (int)((blkno + b) * blockSize);
          if (blockStart < 0 || blockStart + blockSize > image.Length) break;
          CollectNames(image, blockStart, blockStart + blockSize, names);
        }
    }
    return names;
  }

  /// <summary>Appends the names of all real directory entries in [start, end).</summary>
  private static void CollectNames(byte[] image, int start, int end, List<string> names) {
    var cursor = start;
    while (cursor + 12 <= end && cursor + 12 <= image.Length) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor + 8, 2));
      var nameLen = image[cursor + 10];
      if (recLen < 12 || cursor + recLen > end) break;
      if (inode != 0 && nameLen != 0 && cursor + 12 + nameLen <= image.Length) {
        var name = Encoding.UTF8.GetString(image, cursor + 12, nameLen);
        if (name is not ("." or "..")) names.Add(name);
      }
      cursor += recLen;
    }
  }

  /// <summary>
  /// Describes a regular file's on-disk data placement: its dinode block, the
  /// first data block of its (single-record) extent, byte size, and whether the
  /// bytes are stored inline in the dinode. Used by the descriptor's extent map
  /// to locate real data offsets for cluster-tip wiping.
  /// </summary>
  /// <remarks>
  /// <see cref="Extents"/> lists every leaf record in file order — logical block,
  /// first block, block count, unwritten — and <see cref="HasTree"/> is set when
  /// the extents hang off an interior tree this reader does not follow, in which
  /// case the list is empty and the file's bytes are not resolvable here.
  /// </remarks>
  public readonly record struct FilePlacement(string Name, long DinodeBlkno, long DataBlkno, long Size, bool Inline,
      IReadOnlyList<(long LogicalBlock, long Blkno, long Blocks, bool Unwritten)> Extents, bool HasTree) {
    /// <summary>True when the data is one written run starting at logical block 0 and nothing else.</summary>
    public bool IsSingleRun => !this.Inline && !this.HasTree && this.Extents.Count == 1
                               && this.Extents[0].LogicalBlock == 0 && !this.Extents[0].Unwritten;
  }

  /// <summary>Walks the tree and returns the on-disk placement of every regular file.</summary>
  /// <summary>Block size recorded in the superblock, or 0 when it is unreadable.</summary>
  public static int ReadBlockSize(byte[] image)
    => TryReadSuperblock(image, out var blockSize, out _) ? blockSize : 0;

  /// <summary>
  /// Walks the tree in <paramref name="image"/> and returns the on-disk placement
  /// of every regular file. <paramref name="image"/> may be only the head of the
  /// volume; a file dinode or extent block past it is then read from
  /// <paramref name="volume"/>.
  /// </summary>
  public static List<FilePlacement> ReadFilePlacements(byte[] image, Stream? volume = null) {
    var result = new List<FilePlacement>();
    if (!TryReadSuperblock(image, out var blockSize, out var rootBlkno)) return result;
    var rootOff = checked(rootBlkno * blockSize);
    if (rootOff < 0 || rootOff + blockSize > image.Length) return result;
    if (!IsDinode(image, (int)rootOff)) return result;
    WalkPlacements(image, volume, rootBlkno, blockSize, "", result, []);
    return result;
  }

  private static void WalkPlacements(
      byte[] image, Stream? volume, long dirBlkno, int blockSize, string prefix,
      List<FilePlacement> result, HashSet<long> visited) {
    if (!visited.Add(dirBlkno)) return;
    var dirOff = (int)(dirBlkno * blockSize);
    if (dirOff < 0 || dirOff + blockSize > image.Length || !IsDinode(image, dirOff)) return;

    var subdirs = new List<(long Blkno, string Path)>();
    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOff + OffDynFeatures, 2));
    var dirSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(dirOff + OffSize, 8));

    void Handle(int start, int end) {
      var cursor = start;
      while (cursor + 12 <= end && cursor + 12 <= image.Length) {
        var inode = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(cursor, 8));
        var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor + 8, 2));
        var nameLen = image[cursor + 10];
        var fileType = image[cursor + 11];
        if (recLen < 12 || cursor + recLen > end) break;
        if (inode != 0 && nameLen != 0 && cursor + 12 + nameLen <= image.Length) {
          var name = Encoding.UTF8.GetString(image, cursor + 12, nameLen);
          if (name is not ("." or "..")) {
            var path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (fileType is FtRegFile or FtSymlink)
              result.Add(MakePlacement(image, volume, (long)inode, blockSize, path));
            else if (fileType == FtDir)
              subdirs.Add(((long)inode, path));
          }
        }
        cursor += recLen;
      }
    }

    if ((dynFeatures & DynInlineData) != 0) {
      var inlineStart = dirOff + Id2Offset + InlineHeaderLen;
      var maxInline = blockSize - Id2Offset - InlineHeaderLen;
      Handle(inlineStart, inlineStart + (int)Math.Clamp(dirSize, 0, maxInline));
    } else {
      var perCluster = BlocksPerCluster(image, blockSize);
      foreach (var (_, blkno, clusters, _) in ReadExtents(image, dirOff, blockSize))
        for (long b = 0; b < (long)clusters * perCluster; b++) {
          var bs = (int)((blkno + b) * blockSize);
          if (bs < 0 || bs + blockSize > image.Length) break;
          Handle(bs, bs + blockSize);
        }
    }
    foreach (var (blkno, path) in subdirs)
      WalkPlacements(image, volume, blkno, blockSize, path, result, visited);
  }

  private static FilePlacement MakePlacement(byte[] image, Stream? volume, long dinodeBlkno, int blockSize, string name) {
    var perCluster = BlocksPerCluster(image, blockSize);
    var head = image;
    var off = checked((int)(dinodeBlkno * blockSize));
    if ((long)off + blockSize > image.Length) {
      // A dinode past the buffered head: parse it out of its own block.
      head = ReadBlock(image, volume, dinodeBlkno, blockSize);
      off = 0;
    }
    if (!IsDinode(head, off))
      throw new InvalidDataException($"OCFS2: '{name}' names block {dinodeBlkno}, which is not an inode.");
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(off + OffSize, 8));
    var dyn = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(off + OffDynFeatures, 2));
    if ((dyn & DynInlineData) != 0)
      return new FilePlacement(name, dinodeBlkno, dinodeBlkno, size, Inline: true, [], HasTree: false);
    var extents = ReadExtents(head, off, blockSize, image, volume)
      .Select(e => ((long)e.Cpos * perCluster, e.Blkno, (long)e.Clusters * perCluster, e.Unwritten))
      .ToList();
    var dataBlk = extents.Count > 0 ? extents[0].Item2 : 0;
    return new FilePlacement(name, dinodeBlkno, dataBlk, size, Inline: false, extents, HasExtentTree(head, off));
  }

  /// <summary>
  /// Reads all regular files from the image, surfaced at their full nested path.
  /// Returns an empty list when the image is not a recognisable OCFS2 volume.
  /// </summary>
  public static List<(string Name, byte[] Data)> ReadFiles(byte[] image) {
    var result = new List<(string Name, byte[] Data)>();

    // Locate the superblock and read block size + root block number from it.
    if (!TryReadSuperblock(image, out var blockSize, out var rootBlkno))
      return result;

    var rootOff = checked(rootBlkno * blockSize);
    if (rootOff < 0 || rootOff + blockSize > image.Length) return result;

    // Root must be a dinode (INODE01 — or, on the toolkit's legacy writer,
    // historically OCFSV2; accept either so old images still read).
    if (!IsDinode(image, (int)rootOff)) return result;

    var visited = new HashSet<long>();
    WalkDirectory(image, rootBlkno, blockSize, "", result, visited);
    return result;
  }

  /// <summary>
  /// Reads <c>s_blocksize_bits</c> and <c>s_root_blkno</c> from the block-2
  /// superblock dinode. Tries each plausible block size for the OCFSV2 magic.
  /// </summary>
  private static bool TryReadSuperblock(byte[] image, out int blockSize, out long rootBlkno) {
    blockSize = 0;
    rootBlkno = 0;
    foreach (var bs in Ocfs2Superblock.PlausibleBlockSizes) {
      var sbDinodeOff = (long)bs * Ocfs2Superblock.SuperBlockBlkno;
      if (sbDinodeOff + Id2Offset + 0x40 > image.Length) continue;
      if (!image.AsSpan((int)sbDinodeOff, SuperSig.Length).SequenceEqual(SuperSig)) continue;

      var sb = (int)sbDinodeOff + Id2Offset;
      var bsBits = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(sb + SbBlocksizeBits, 4));
      var root = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(sb + SbRootBlkno, 8));

      // Trust s_blocksize_bits when sane; otherwise fall back to the matched bs.
      var declared = bsBits is >= 9 and <= 16 ? 1 << (int)bsBits : bs;
      blockSize = declared;
      rootBlkno = (long)root;
      if (rootBlkno > 0) return true;
    }
    return false;
  }

  /// <summary>True iff the block at <paramref name="off"/> begins with a dinode
  /// signature (INODE01 for regular inodes, OCFSV2 for the legacy/super case).</summary>
  private static bool IsDinode(byte[] image, int off) {
    if (off + 8 > image.Length) return false;
    var span = image.AsSpan(off, 8);
    return span[..InodeSig.Length].SequenceEqual(InodeSig)
        || span[..SuperSig.Length].SequenceEqual(SuperSig);
  }

  /// <summary>
  /// Walks a directory dinode at <paramref name="dirBlkno"/>: appends regular
  /// files (with their full path) to <paramref name="result"/> and recurses into
  /// subdirectories. Inline directories keep their <c>ocfs2_dir_entry</c> records
  /// in the dinode (after the 8-byte inline header); extent-backed directories
  /// store them in data clusters referenced by the dinode's extent list.
  /// </summary>
  private static void WalkDirectory(
      byte[] image, long dirBlkno, int blockSize, string prefix,
      List<(string Name, byte[] Data)> result, HashSet<long> visited) {
    if (!visited.Add(dirBlkno)) return;

    var dirOff = (int)(dirBlkno * blockSize);
    if (dirOff < 0 || dirOff + blockSize > image.Length) return;
    if (!IsDinode(image, dirOff)) return;

    var subdirs = new List<(long Blkno, string Path)>();

    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dirOff + OffDynFeatures, 2));
    var dirSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(dirOff + OffSize, 8));

    if ((dynFeatures & DynInlineData) != 0) {
      var inlineStart = dirOff + Id2Offset + InlineHeaderLen;
      var maxInline = blockSize - Id2Offset - InlineHeaderLen;
      var inlineEnd = inlineStart + (int)Math.Clamp(dirSize, 0, maxInline);
      ParseDirEntries(image, inlineStart, inlineEnd, prefix, result, subdirs);
    } else {
      // Extent-backed: each extent record points at a run of directory blocks.
      var perCluster = BlocksPerCluster(image, blockSize);
      foreach (var (_, blkno, clusters, _) in ReadExtents(image, dirOff, blockSize)) {
        var clusterBlocks = (long)clusters * perCluster;
        for (long b = 0; b < clusterBlocks; b++) {
          var blockStart = (int)((blkno + b) * blockSize);
          if (blockStart < 0 || blockStart + blockSize > image.Length) break;
          ParseDirEntries(image, blockStart, blockStart + blockSize, prefix, result, subdirs);
        }
      }
    }

    foreach (var (blkno, path) in subdirs)
      WalkDirectory(image, blkno, blockSize, path, result, visited);
  }

  /// <summary>
  /// Parses <c>ocfs2_dir_entry</c> records in <c>[start, end)</c>. Regular files
  /// are appended to <paramref name="result"/>; subdirectories (other than the
  /// system directory and the dot entries) are collected for recursion. A zero or
  /// too-short <c>rec_len</c> ends the run.
  /// </summary>
  private static void ParseDirEntries(
      byte[] image, int start, int end, string prefix,
      List<(string Name, byte[] Data)> result, List<(long Blkno, string Path)> subdirs) {
    var cursor = start;
    while (cursor + 12 <= end && cursor + 12 <= image.Length) {
      var inode = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(cursor, 8));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(cursor + 8, 2));
      var nameLen = image[cursor + 10];
      var fileType = image[cursor + 11];

      if (recLen < 12 || cursor + recLen > end) break;
      if (inode == 0 || nameLen == 0 || cursor + 12 + nameLen > image.Length) {
        cursor += recLen;
        continue;
      }

      var name = Encoding.UTF8.GetString(image, cursor + 12, nameLen);
      cursor += recLen;

      if (name is "." or "..") continue;
      var path = prefix.Length == 0 ? name : prefix + "/" + name;

      switch (fileType) {
        case FtRegFile:
        case FtSymlink:
          result.Add((path, ExtractFileData(image, (long)inode)));
          break;
        case FtDir:
          subdirs.Add(((long)inode, path));
          break;
        default:
          break; // devices/fifos/sockets carry no extractable byte stream
      }
    }
  }

  /// <summary>
  /// Extracts a regular file's bytes from its dinode. Inline files keep their
  /// bytes in the dinode after the 8-byte inline header; extent files store them
  /// in the clusters their extent list points at. The result is clamped to the
  /// dinode's <c>i_size</c>.
  /// </summary>
  private static byte[] ExtractFileData(byte[] image, long dinodeBlkno) {
    // Re-derive block size from the superblock for self-containment.
    if (!TryReadSuperblock(image, out var blockSize, out _)) return [];

    var off = (int)(dinodeBlkno * blockSize);
    if (off < 0 || off + blockSize > image.Length) return [];
    if (!IsDinode(image, off)) return [];

    var fileSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(off + OffSize, 8));
    if (fileSize <= 0) return [];

    var dynFeatures = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + OffDynFeatures, 2));
    if ((dynFeatures & DynInlineData) != 0) {
      var inlineStart = off + Id2Offset + InlineHeaderLen;
      var maxInline = blockSize - Id2Offset - InlineHeaderLen;
      var len = (int)Math.Clamp(fileSize, 0, Math.Min(maxInline, image.Length - inlineStart));
      var inline = new byte[len];
      if (len > 0) Buffer.BlockCopy(image, inlineStart, inline, 0, len);
      return inline;
    }

    // Each record lands at its logical cluster: a hole between records, and an
    // unwritten run, both read as zeros.
    var result = new byte[fileSize];
    var clusterBytes = (long)blockSize * BlocksPerCluster(image, blockSize);
    foreach (var (cpos, blkno, clusters, unwritten) in ReadExtents(image, off, blockSize)) {
      var logical = cpos * clusterBytes;
      if (logical >= fileSize || unwritten) continue;
      var dataOff = blkno * blockSize;
      var copyLen = Math.Min(clusters * clusterBytes, fileSize - logical);
      copyLen = Math.Min(copyLen, Math.Max(0, image.Length - dataOff));
      if (dataOff >= 0 && copyLen > 0)
        Buffer.BlockCopy(image, (int)dataOff, result, (int)logical, (int)copyLen);
    }
    return result;
  }

  /// <summary>Where an extent block keeps its <c>ocfs2_extent_list</c> (after the 0x30-byte header).</summary>
  private const int ExtentBlockListOffset = 0x30;
  /// <summary><c>h_blkno</c> of an extent block: the block it claims to be.</summary>
  private const int ExtentBlockBlknoOffset = 0x18;
  /// <summary>Deeper than any volume the 32-bit cluster space allows; a loop guard.</summary>
  private const int MaxTreeDepth = 8;
  private static readonly byte[] ExtentBlockSig = "EXBLK01"u8.ToArray();

  /// <summary>
  /// Reads every leaf extent record of a dinode in file order: logical cluster,
  /// first block, length in clusters and whether the run is unwritten
  /// (allocated, reads as zeros). A list of depth <c>d &gt; 0</c> holds interior
  /// records (<c>e_cpos</c>, <c>e_int_clusters</c>, <c>e_blkno</c>) that name
  /// extent blocks (<c>EXBLK01</c>) whose own list, at 0x30, has depth
  /// <c>d − 1</c>; they are followed down to the leaves. A block that is not the
  /// extent block it should be raises <see cref="InvalidDataException"/> rather
  /// than reading as zeros.
  /// </summary>
  /// <param name="image">Buffer holding the dinode at <paramref name="dinodeOff"/>.</param>
  /// <param name="volumeHead">Buffered head of the volume, for extent blocks (defaults to <paramref name="image"/>).</param>
  /// <param name="volume">The whole volume, for extent blocks past the head.</param>
  private static List<(uint Cpos, long Blkno, int Clusters, bool Unwritten)> ReadExtents(
      byte[] image, int dinodeOff, int blockSize, byte[]? volumeHead = null, Stream? volume = null) {
    var result = new List<(uint, long, int, bool)>();
    var listOff = dinodeOff + Id2Offset;
    if (listOff + 0x10 > image.Length) return result;
    var end = Math.Min(image.Length, dinodeOff + blockSize);
    CollectExtents(image.AsSpan(listOff, end - listOff), blockSize, volumeHead ?? image, volume, result, expectedDepth: -1);
    return result;
  }

  private static void CollectExtents(
      ReadOnlySpan<byte> list, int blockSize, byte[] head, Stream? volume,
      List<(uint, long, int, bool)> result, int expectedDepth) {
    var depth = BinaryPrimitives.ReadUInt16LittleEndian(list);
    var count = BinaryPrimitives.ReadUInt16LittleEndian(list[2..]);
    var used = BinaryPrimitives.ReadUInt16LittleEndian(list[4..]);
    if (depth > MaxTreeDepth || (expectedDepth >= 0 && depth != expectedDepth) || used > count || 0x10 + used * 16 > list.Length)
      throw new InvalidDataException($"OCFS2: an extent list claims depth {depth}, {used} of {count} records — not a valid tree node.");

    for (var i = 0; i < used; i++) {
      var rec = list.Slice(0x10 + i * 16, 16);
      var cpos = BinaryPrimitives.ReadUInt32LittleEndian(rec);
      var blkno = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec[8..]);
      if (depth == 0) {
        // e_leaf_clusters (u16) @+4, reserved @+6, flags @+7. OCFS2_EXT_UNWRITTEN = 0x01.
        var clusters = BinaryPrimitives.ReadUInt16LittleEndian(rec[4..]);
        if (clusters == 0) continue;
        result.Add((cpos, blkno, clusters, (rec[7] & 0x01) != 0));
        continue;
      }
      // Interior: e_int_clusters (u32) @+4 — what lies below, not needed to walk it.
      if (blkno == 0) continue;
      var block = ReadBlock(head, volume, blkno, blockSize);
      if (!block.AsSpan(0, ExtentBlockSig.Length).SequenceEqual(ExtentBlockSig)
          || (long)BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(ExtentBlockBlknoOffset, 8)) != blkno)
        throw new InvalidDataException($"OCFS2: block {blkno} should be an extent block and is not.");
      CollectExtents(block.AsSpan(ExtentBlockListOffset), blockSize, head, volume, result, depth - 1);
    }
  }

  /// <summary>One block, out of the buffered head when it is there, else out of the volume.</summary>
  private static byte[] ReadBlock(byte[] head, Stream? volume, long blkno, int blockSize) {
    var at = blkno * blockSize;
    var block = new byte[blockSize];
    if (blkno > 0 && at + blockSize <= head.Length) {
      Buffer.BlockCopy(head, (int)at, block, 0, blockSize);
      return block;
    }
    if (volume == null || blkno <= 0 || at + blockSize > volume.Length)
      throw new InvalidDataException($"OCFS2: block {blkno} lies outside the volume that was read.");
    volume.Position = at;
    volume.ReadExactly(block);
    return block;
  }

  /// <summary>Whether a dinode's extents hang off an interior tree rather than its own leaf list.</summary>
  private static bool HasExtentTree(byte[] image, int dinodeOff)
    => dinodeOff + Id2Offset + 2 <= image.Length
       && BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(dinodeOff + Id2Offset, 2)) != 0;

  /// <summary>Blocks per cluster, from the superblock; 1 when it cannot be read.</summary>
  private static int BlocksPerCluster(byte[] image, int blockSize) {
    var sb = (long)blockSize * Ocfs2Superblock.SuperBlockBlkno + Id2Offset;
    if (sb + SbClustersizeBits + 4 > image.Length) return 1;
    var bits = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)sb + SbClustersizeBits, 4));
    return bits is >= 12 and <= 20 && (1 << (int)bits) >= blockSize ? (1 << (int)bits) / blockSize : 1;
  }
}
