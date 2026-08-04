#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.VxFs;

/// <summary>
/// Builds a VxFS volume the Linux <c>freevxfs</c> driver mounts.
/// </summary>
/// <remarks>
/// <para>The driver reaches the files by a chain of five hops, and a volume is
/// only a volume if every one of them lands. The superblock names an object
/// location table; the table names a fileset-header inode and the block a raw
/// inode array starts at; that inode describes a file holding two fileset
/// headers, one structural and one primary; the structural one names the inode
/// describing the structural inode list, and the primary one names — inside
/// that list — the inode describing the list the user's files live in. Only
/// then is inode 2 the root directory.</para>
///
/// <para>So the layout below is not a choice of taste. The raw inode array has
/// to hold the three structural inodes at the offsets the driver computes from
/// their numbers, the fileset-header file has to be two pages long because the
/// driver asks for the second header by page index, and the block size has to
/// be 1024 because that is what the driver mounted with before it knew ours —
/// it converts the table's block number with the ratio between the two, and
/// only a ratio of one puts the table where we wrote it.</para>
///
/// <para>Files are laid out in whole blocks, each as a run of direct extents.
/// Ten fit in an inode, which is the ceiling on how many pieces one file may be
/// in.</para>
/// </remarks>
public sealed class VxFsWriter {

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>The volume name written into the superblock.</summary>
  public string VolumeName { get; init; } = "cwb";

  /// <summary>The timestamp stamped on the volume and its inodes.</summary>
  public uint Timestamp { get; init; } = 0x60000000;

  /// <summary>Adds a file to the root directory.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var clean = Path.GetFileName(name);
    if (clean.Length is 0 or > 255)
      throw new ArgumentException($"VxFS: '{name}' is not a name this can write.", nameof(name));
    this._files.Add((clean, data));
  }

  // The fixed part of the layout, in blocks. Everything before the data area
  // is placed by hand because the driver's walk depends on all of it.
  private const int SuperblockBlock = 1;
  private const int OltBlock = 2;
  private const int StructuralIlistBlock = 4;
  private const int StructuralIlistBlocks = 4;   // one page: 16 inodes
  private const int FsHeadBlock = 8;
  private const int FsHeadBlocks = 8;            // two pages: the driver asks by page
  private const int PrimaryIlistBlock = 16;

  // Structural inode numbers, as the driver will ask for them.
  private const uint FsHeadInode = 1;
  private const uint StructuralIlistInode = 2;
  private const uint PrimaryIlistInode = 3;

  /// <summary>Lays the volume out and returns its bytes.</summary>
  public byte[] Build() {
    const int bs = VxFsLayout.BlockSize;

    // The primary list holds the root plus one inode per file, and is rounded
    // to whole pages so the driver never asks for a page we did not back.
    var inodeCount = (int)VxFsLayout.RootInode + 1 + this._files.Count;
    var primaryIlistBlocks = RoundUpToPage(Blocks((long)inodeCount * VxFsLayout.InodeSize));

    var directory = BuildDirectory(out var dirEntryInode);
    var dirBlocks = Blocks(directory.LongLength);

    var dataStart = PrimaryIlistBlock + primaryIlistBlocks;
    var dirStart = dataStart;
    var cursor = dirStart + dirBlocks;

    var fileStart = new int[this._files.Count];
    var fileBlocks = new int[this._files.Count];
    for (var i = 0; i < this._files.Count; ++i) {
      fileBlocks[i] = Math.Max(1, Blocks(this._files[i].Data.LongLength));
      fileStart[i] = cursor;
      cursor += fileBlocks[i];
    }

    var totalBlocks = RoundUpToPage(cursor);
    var image = new byte[(long)totalBlocks * bs];

    WriteSuperblock(image, totalBlocks, inodeCount, dataStart);
    WriteOlt(image);

    // The three inodes the driver reads straight out of the raw array, before
    // it can read any inode list as a file.
    var structural = (long)StructuralIlistBlock * bs;
    WriteInode(image, structural + (long)FsHeadInode * VxFsLayout.InodeSize,
      VxFsLayout.ModeFsh | 0x1A4, 1, (long)FsHeadBlocks * bs,
      new (int, int)[] { (FsHeadBlock, FsHeadBlocks) }, 0);
    WriteInode(image, structural + (long)StructuralIlistInode * VxFsLayout.InodeSize,
      VxFsLayout.ModeIlt | 0x1A4, 1, (long)StructuralIlistBlocks * bs,
      new (int, int)[] { (StructuralIlistBlock, StructuralIlistBlocks) }, 0);
    WriteInode(image, structural + (long)PrimaryIlistInode * VxFsLayout.InodeSize,
      VxFsLayout.ModeIlt | 0x1A4, 1, (long)primaryIlistBlocks * bs,
      new (int, int)[] { (PrimaryIlistBlock, primaryIlistBlocks) }, 0);

    WriteFsHeads(image);

    // The primary list: the root directory, then the files.
    var primary = (long)PrimaryIlistBlock * bs;
    WriteInode(image, primary + (long)VxFsLayout.RootInode * VxFsLayout.InodeSize,
      VxFsLayout.ModeDir | 0x1ED, 2, directory.LongLength,
      new (int, int)[] { (dirStart, dirBlocks) }, VxFsLayout.RootInode);

    directory.CopyTo(image, (long)dirStart * bs);

    for (var i = 0; i < this._files.Count; ++i) {
      var data = this._files[i].Data;
      WriteInode(image, primary + (long)dirEntryInode[i] * VxFsLayout.InodeSize,
        VxFsLayout.ModeReg | 0x1A4, 1, data.LongLength,
        new (int, int)[] { (fileStart[i], fileBlocks[i]) }, 0);
      data.CopyTo(image, (long)fileStart[i] * bs);
    }

    return image;
  }

  /// <summary>How many blocks a length occupies.</summary>
  private static int Blocks(long length)
    => (int)((length + VxFsLayout.BlockSize - 1) / VxFsLayout.BlockSize);

  /// <summary>
  /// Rounds a block count up to whole 4 KiB pages, which is the unit the driver
  /// reads a file in.
  /// </summary>
  private static int RoundUpToPage(int blocks) {
    const int perPage = 4096 / VxFsLayout.BlockSize;
    return (blocks + perPage - 1) / perPage * perPage;
  }

  private void WriteSuperblock(byte[] image, int totalBlocks, int inodeCount, int dataStart) {
    var at = VxFsLayout.SuperblockOffset;
    var sb = image.AsSpan(at, VxFsLayout.SuperblockBytes);

    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbMagic..], VxFsLayout.SuperMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbVersion..], 4);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbCtime..], this.Timestamp);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbCutime..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbBsize..], VxFsLayout.BlockSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbSize..], (uint)totalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbDsize..], (uint)totalBlocks);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbOldNinode..], (uint)inodeCount);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbImmedlen..], VxFsLayout.ImmediateBytes);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbNdaddr..], VxFsLayout.DirectExtents);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbFirstau..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbIstart..], StructuralIlistBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbBstart..], (uint)dataStart);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbNindir..], VxFsLayout.BlockSize / 4);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbInopb..], VxFsLayout.BlockSize / VxFsLayout.InodeSize);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbBshift..], 10);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbInoshift..], 8);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbBmask..], unchecked((uint)~(VxFsLayout.BlockSize - 1)));
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbBoffmask..], VxFsLayout.BlockSize - 1);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbFree..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbIfree..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbFlags..], 0);
    sb[VxFsLayout.SbClean] = 0x5A;
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbWtime..], this.Timestamp);
    WriteFixedAscii(sb[VxFsLayout.SbFname..], this.VolumeName, 6);
    WriteFixedAscii(sb[VxFsLayout.SbFpack..], this.VolumeName, 6);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbLogversion..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbOltext..], OltBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[(VxFsLayout.SbOltext + 4)..], OltBlock);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbOltsize..], 1);
    BinaryPrimitives.WriteUInt32LittleEndian(sb[VxFsLayout.SbDinosize..], VxFsLayout.InodeSize);
  }

  /// <summary>
  /// Writes the table naming the fileset-header inode and the block the raw
  /// inode array begins at.
  /// </summary>
  /// <remarks>
  /// The driver walks the entries by their own size fields to the end of the
  /// block, so the block cannot simply be left zero behind them: an entry of
  /// size zero never advances. The remainder is one free entry covering it.
  /// Each of the two it looks for must appear exactly once — a second would
  /// trip an assertion inside the driver.
  /// </remarks>
  private static void WriteOlt(byte[] image) {
    var at = (long)OltBlock * VxFsLayout.BlockSize;
    var olt = image.AsSpan((int)at, VxFsLayout.BlockSize);

    BinaryPrimitives.WriteUInt32LittleEndian(olt, VxFsLayout.OltMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[4..], VxFsLayout.OltHeaderBytes);

    var cursor = VxFsLayout.OltHeaderBytes;
    BinaryPrimitives.WriteUInt32LittleEndian(olt[cursor..], VxFsLayout.OltFsHead);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[(cursor + 4)..], 16);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[(cursor + 8)..], FsHeadInode);
    cursor += 16;

    BinaryPrimitives.WriteUInt32LittleEndian(olt[cursor..], VxFsLayout.OltIlist);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[(cursor + 4)..], 16);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[(cursor + 8)..], StructuralIlistBlock);
    cursor += 16;

    BinaryPrimitives.WriteUInt32LittleEndian(olt[cursor..], VxFsLayout.OltFree);
    BinaryPrimitives.WriteUInt32LittleEndian(olt[(cursor + 4)..], (uint)(VxFsLayout.BlockSize - cursor));
  }

  /// <summary>
  /// Writes the two fileset headers — the structural one first, the primary one
  /// both a block and a page later.
  /// </summary>
  /// <remarks>
  /// Drivers disagree about what "the second header" means. The one shipped as
  /// <c>legacy-fs</c> reads it with the block reader, so it wants file block 1;
  /// the mainline kernel reads it through the page cache by index, so it wants
  /// file byte 4096. The structural header is at zero either way. Writing the
  /// primary one twice costs six spare kilobytes and satisfies both, which is
  /// worth more than picking a side.
  /// </remarks>
  private static void WriteFsHeads(byte[] image) {
    var at = (long)FsHeadBlock * VxFsLayout.BlockSize;
    WriteFsHead(image, at, StructuralIlistInode);
    WriteFsHead(image, at + VxFsLayout.BlockSize, PrimaryIlistInode);
    WriteFsHead(image, at + 4096, PrimaryIlistInode);
  }

  private static void WriteFsHead(byte[] image, long at, uint ilistInode) {
    var fsh = image.AsSpan((int)at, 64);
    BinaryPrimitives.WriteUInt32LittleEndian(fsh, 1);            // fsh_version
    BinaryPrimitives.WriteUInt32LittleEndian(fsh[4..], 0);       // fsh_fsindex
    BinaryPrimitives.WriteUInt32LittleEndian(fsh[40..], 0);      // fsh_maxinode
    BinaryPrimitives.WriteUInt32LittleEndian(fsh[48..], ilistInode);  // fsh_ilistino[0]
  }

  /// <summary>Fills one 256-byte inode slot.</summary>
  /// <param name="dotdot">
  /// The parent a directory records. The driver emits <c>..</c> from this field
  /// rather than from any entry on disk, so a directory that left it zero would
  /// list a parent that is not an inode.
  /// </param>
  private void WriteInode(
      byte[] image, long at, uint mode, uint links, long size,
      IReadOnlyList<(int Block, int Count)> extents, uint dotdot) {
    var node = image.AsSpan((int)at, VxFsLayout.InodeSize);
    node.Clear();

    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InMode..], mode);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InNlink..], links);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InUid..], 0);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InGid..], 0);
    BinaryPrimitives.WriteInt64LittleEndian(node[VxFsLayout.InSize..], size);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InAtime..], this.Timestamp);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InMtime..], this.Timestamp);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InCtime..], this.Timestamp);
    node[VxFsLayout.InAflags] = 0;
    node[VxFsLayout.InOrgtype] = VxFsLayout.OrgExt4;
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InFtarea..], dotdot);

    var blocks = 0;
    foreach (var (_, count) in extents) blocks += count;
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InBlocks..], (uint)blocks);
    BinaryPrimitives.WriteUInt32LittleEndian(node[VxFsLayout.InGen..], 1);
    BinaryPrimitives.WriteInt64LittleEndian(node[VxFsLayout.InVersion..], 1);

    if (extents.Count > VxFsLayout.DirectExtents)
      throw new InvalidOperationException(
        $"VxFS: an inode holds {VxFsLayout.DirectExtents} direct extents; {extents.Count} were asked for.");

    for (var i = 0; i < extents.Count; ++i) {
      var slot = VxFsLayout.Ext4Direct + i * 8;
      BinaryPrimitives.WriteUInt32LittleEndian(node[slot..], (uint)extents[i].Block);
      BinaryPrimitives.WriteUInt32LittleEndian(node[(slot + 4)..], (uint)extents[i].Count);
    }
  }

  /// <summary>
  /// Lays out the root directory's blocks and says which inode each file got.
  /// </summary>
  /// <remarks>
  /// Every block opens with a four-byte header the driver skips before reading
  /// entries, and no entry may straddle a block: a record whose length would
  /// cross the boundary is left out, and the zero bytes after it tell the walk
  /// to move to the next block. <c>.</c> and <c>..</c> are not written — the
  /// driver emits both itself, and writing them would list each twice.
  /// </remarks>
  private byte[] BuildDirectory(out uint[] entryInode) {
    const int bs = VxFsLayout.BlockSize;
    var blocks = new List<byte[]>();
    var block = NewDirectoryBlock();
    var used = VxFsLayout.DirBlockHeaderBytes;

    entryInode = new uint[this._files.Count];
    var next = VxFsLayout.RootInode + 1;

    for (var i = 0; i < this._files.Count; ++i) {
      var name = Encoding.ASCII.GetBytes(this._files[i].Name);
      var length = VxFsLayout.DirEntryLength(name.Length);
      if (length > bs - VxFsLayout.DirBlockHeaderBytes)
        throw new InvalidOperationException(
          $"VxFS: the name '{this._files[i].Name}' does not fit in a directory block.");

      if (used + length > bs) {
        FinishDirectoryBlock(block, used);
        blocks.Add(block);
        block = NewDirectoryBlock();
        used = VxFsLayout.DirBlockHeaderBytes;
      }

      entryInode[i] = next++;
      var entry = block.AsSpan(used, length);
      BinaryPrimitives.WriteUInt32LittleEndian(entry, entryInode[i]);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)length);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)name.Length);
      BinaryPrimitives.WriteUInt16LittleEndian(entry[8..], 0);
      name.CopyTo(entry[VxFsLayout.DirNameOffset..]);
      used += length;
    }

    FinishDirectoryBlock(block, used);
    blocks.Add(block);

    var result = new byte[blocks.Count * bs];
    for (var i = 0; i < blocks.Count; ++i) blocks[i].CopyTo(result, i * bs);
    return result;
  }

  private static byte[] NewDirectoryBlock() {
    var block = new byte[VxFsLayout.BlockSize];
    // d_free is filled in once the block is done; d_nhash stays zero, which is
    // what makes the header four bytes long.
    return block;
  }

  private static void FinishDirectoryBlock(byte[] block, int used)
    => BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)(VxFsLayout.BlockSize - used));

  private static void WriteFixedAscii(Span<byte> target, string value, int width) {
    target[..width].Clear();
    for (var i = 0; i < width && i < value.Length; ++i)
      target[i] = (byte)(value[i] is >= (char)0x20 and < (char)0x7F ? value[i] : '_');
  }
}
