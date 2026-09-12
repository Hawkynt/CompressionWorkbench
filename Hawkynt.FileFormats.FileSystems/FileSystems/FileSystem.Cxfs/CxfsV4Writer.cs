#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FileSystem.Cxfs;

/// <summary>
/// Writes the conservative XFS v4 / <c>crc=0</c> profile used for CXFS-compatible
/// standalone filesystem images.
///
/// <para>CXFS itself does not add another file/directory on-disk format: SGI
/// documents it as the XFS filesystem structure plus external cluster/XVM state.
/// This writer therefore emits an old-style XFS filesystem rather than stamping a
/// fictitious CXFS feature bit into the superblock.</para>
///
/// <para>The authoring profile is intentionally narrow and fail-closed: 4 KiB
/// filesystem blocks, 512-byte sectors, 256-byte v2 dinodes, a dir2 short-form
/// root directory, one extent per regular file, no attributes, no realtime data,
/// no quotas, no ftype and no CRC/v5-only metadata. Only root-level regular files
/// are authored. Broader XFS images remain readable through <c>XfsReader</c>, but
/// mutation rebuilds refuse structures this writer cannot reproduce losslessly.</para>
/// </summary>
internal sealed class CxfsV4Writer {
  internal const int BlockSize = 4096;
  internal const int SectorSize = 512;
  internal const int InodeSize = 256;
  internal const int InodesPerBlock = BlockSize / InodeSize;
  internal const int InodesPerChunk = 64;
  internal const int InodeChunkBlock = 8;
  internal const int InodeChunkBlocks = InodesPerChunk / InodesPerBlock;
  internal const ulong RootIno = (ulong)InodeChunkBlock * InodesPerBlock;
  internal const int DataStartBlock = InodeChunkBlock + InodeChunkBlocks;
  internal const int MinAgBlocks = 4096;
  internal const int AgCount = 2;
  internal const int LogStartAgBlock = 4;
  internal const int LogBlocks = 1024;

  // v4 + NLINK + ALIGN + LOGV2 + EXTFLG + DIRV2.
  internal const ushort SuperblockVersion = 0x34A4;

  private const uint XfsMagic = 0x58465342;     // XFSB
  private const uint AgfMagic = 0x58414746;     // XAGF
  private const uint AgiMagic = 0x58414749;     // XAGI
  private const uint BnobtV4Magic = 0x41425442; // ABTB
  private const uint CntbtV4Magic = 0x41425443; // ABTC
  private const uint InobtV4Magic = 0x49414254; // IABT
  private const ushort InodeMagic = 0x494E;     // IN

  private const byte BlockLog = 12;
  private const byte SectorLog = 9;
  private const byte InodeLog = 8;
  private const byte InoPbLog = 4;
  private const int BnobtBlock = 1;
  private const int CntbtBlock = 2;
  private const int InobtBlock = 3;
  private const int BtreeRecordOffset = 16;
  private const int ForkOffset = 100;
  private const int ShortFormCapacity = InodeSize - ForkOffset;
  private const uint NullAgBlock = uint.MaxValue;
  private const uint NullAgIno = uint.MaxValue;

  private static readonly byte[] UuidBytes = new Guid("9c639e95-fd2d-4b95-9f87-d427f564c250").ToByteArray();

  private readonly List<(string Name, byte[] Data)> _files = [];
  private readonly List<string> _directories = [];
  private string _volumeLabel = "";

  public void SetVolumeLabel(string label) => this._volumeLabel = label ?? "";

  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    var normalized = NormalizeName(name);
    this._files.RemoveAll(x => string.Equals(x.Name, normalized, StringComparison.Ordinal));
    this._files.Add((normalized, data));
    this.RegisterAncestors(normalized);
  }

  /// <summary>Registers an explicitly requested (possibly empty) directory and all of its ancestors.</summary>
  public void AddDirectory(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var normalized = NormalizeName(name);
    this.RegisterDirectory(normalized);
    this.RegisterAncestors(normalized);
  }

  private void RegisterAncestors(string path) {
    for (var cut = path.LastIndexOf('/'); cut > 0; cut = path.LastIndexOf('/', cut - 1))
      this.RegisterDirectory(path[..cut]);
  }

  private void RegisterDirectory(string path) {
    if (path.Length == 0)
      return;
    if (!this._directories.Contains(path, StringComparer.Ordinal))
      this._directories.Add(path);
  }

  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("CXFS v4 creation requires a writable, seekable stream.", nameof(output));

    var files = this._files.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();

    // A directory must own an inode before any of its children reference it, so
    // order the directory inodes shallowest-first; the file inodes follow.
    var directories = this._directories
      .Distinct(StringComparer.Ordinal)
      .OrderBy(x => x.Count(c => c == '/'))
      .ThenBy(x => x, StringComparer.Ordinal)
      .ToArray();

    var usedInodes = 3 + directories.Length + files.Length; // root + rbm + rsum + dirs + files
    if (usedInodes > InodesPerChunk)
      throw new NotSupportedException(
        $"CXFS v4 mutation profile is limited to one 64-inode chunk ({usedInodes} inodes requested).");

    // Inode slots: 0 root, 1 rbm, 2 rsum, then the directories, then the files.
    var inodeOfDirectory = new Dictionary<string, ulong>(StringComparer.Ordinal) { [""] = RootIno };
    for (var i = 0; i < directories.Length; ++i)
      inodeOfDirectory[directories[i]] = RootIno + (ulong)(3 + i);

    var children = new Dictionary<string, List<DirectoryChild>>(StringComparer.Ordinal) { [""] = [] };
    foreach (var directory in directories) {
      children[directory] = [];
      children[ParentOf(directory)].Add(new DirectoryChild(NameOf(directory), inodeOfDirectory[directory], true));
    }
    for (var i = 0; i < files.Length; ++i)
      children[ParentOf(files[i].Name)].Add(
        new DirectoryChild(NameOf(files[i].Name), RootIno + (ulong)(3 + directories.Length + i), false));

    foreach (var (path, entries) in children)
      ValidateShortFormDirectory(path, entries);

    var dataBlocks = new int[files.Length];
    var fileStarts = new int[files.Length];
    var nextBlock = DataStartBlock;
    for (var i = 0; i < files.Length; ++i) {
      var blocks = checked((files[i].Data.Length + BlockSize - 1) / BlockSize);
      if (blocks > 0x1FFFFF)
        throw new NotSupportedException("CXFS v4 profile supports at most one 21-bit XFS extent per file.");
      dataBlocks[i] = blocks;
      fileStarts[i] = nextBlock;
      nextBlock = checked(nextBlock + blocks);
    }

    var agBlocks = MinAgBlocks;
    while (agBlocks <= nextBlock) {
      if (agBlocks >= 1 << 18)
        throw new NotSupportedException("CXFS v4 writer currently supports images below 2 GiB.");
      agBlocks <<= 1;
    }

    var totalBlocks = checked((long)agBlocks * AgCount);
    var totalBytes = checked(totalBlocks * BlockSize);
    if (totalBytes > int.MaxValue)
      throw new NotSupportedException("CXFS v4 writer currently supports images below 2 GiB.");

    var image = new byte[(int)totalBytes];
    var agBlkLog = checked((byte)BitOperations.Log2((uint)agBlocks));
    var freeInodes = InodesPerChunk - usedInodes;
    var freeStartAg0 = nextBlock;
    var freeLenAg0 = agBlocks - freeStartAg0;
    var freeStartAg1 = LogStartAgBlock + LogBlocks;
    var freeLenAg1 = agBlocks - freeStartAg1;
    var fdBlocks = checked((ulong)(freeLenAg0 + freeLenAg1));
    var logStart = checked((ulong)agBlocks + LogStartAgBlock);

    for (var ag = 0; ag < AgCount; ++ag) {
      var agBase = checked(ag * agBlocks * BlockSize);
      WriteSuperblock(image.AsSpan(agBase, SectorSize), totalBlocks, logStart, agBlocks, agBlkLog,
        icount: InodesPerChunk, ifree: (ulong)freeInodes, fdblocks: fdBlocks, this._volumeLabel);

      var freeStart = ag == 0 ? freeStartAg0 : freeStartAg1;
      var freeLength = ag == 0 ? freeLenAg0 : freeLenAg1;
      WriteAgf(image.AsSpan(agBase + SectorSize, SectorSize), (uint)ag, (uint)agBlocks, (uint)freeLength);
      WriteAgi(image.AsSpan(agBase + 2 * SectorSize, SectorSize), (uint)ag, (uint)agBlocks,
        inodeCount: ag == 0 ? InodesPerChunk : 0,
        freeInodes: ag == 0 ? freeInodes : 0,
        newIno: ag == 0 ? (uint)RootIno : NullAgIno);
      WriteAgflV4(image.AsSpan(agBase + 3 * SectorSize, SectorSize));
      WriteFreeBtree(image.AsSpan(agBase + BnobtBlock * BlockSize, BlockSize), BnobtV4Magic,
        (uint)freeStart, (uint)freeLength);
      WriteFreeBtree(image.AsSpan(agBase + CntbtBlock * BlockSize, BlockSize), CntbtV4Magic,
        (uint)freeStart, (uint)freeLength);
      WriteInobt(image.AsSpan(agBase + InobtBlock * BlockSize, BlockSize), ag == 0 ? usedInodes : 0);
    }

    // xfs_repair walks every slot in an allocated inode chunk, including the free
    // slots. Stamp each one with a valid v2 core, then overwrite allocated slots.
    for (var slot = 0; slot < InodesPerChunk; ++slot)
      WriteInodeCoreV2(image.AsSpan(InodeOffset(slot), InodeSize), mode: 0, format: 0, nlink: 0);

    WriteDirectoryInode(image.AsSpan(InodeOffset(0), InodeSize), RootIno, children[""]);
    WriteInodeCoreV2(image.AsSpan(InodeOffset(1), InodeSize), mode: 0x8000, format: 2, nlink: 1);
    WriteInodeCoreV2(image.AsSpan(InodeOffset(2), InodeSize), mode: 0x8000, format: 2, nlink: 1);

    for (var i = 0; i < directories.Length; ++i)
      WriteDirectoryInode(image.AsSpan(InodeOffset(3 + i), InodeSize),
        inodeOfDirectory[ParentOf(directories[i])], children[directories[i]]);

    var fileSlotBase = 3 + directories.Length;
    for (var i = 0; i < files.Length; ++i) {
      var inode = image.AsSpan(InodeOffset(fileSlotBase + i), InodeSize);
      WriteInodeCoreV2(inode, mode: 0x81A4, format: 2, nlink: 1);
      BinaryPrimitives.WriteUInt64BigEndian(inode[56..], (ulong)files[i].Data.Length);
      BinaryPrimitives.WriteUInt64BigEndian(inode[64..], (ulong)dataBlocks[i]);
      BinaryPrimitives.WriteUInt32BigEndian(inode[76..], dataBlocks[i] == 0 ? 0u : 1u);
      if (dataBlocks[i] > 0)
        WriteExtent(inode[ForkOffset..], (ulong)fileStarts[i], (ulong)dataBlocks[i]);
      files[i].Data.CopyTo(image, fileStarts[i] * BlockSize);
    }

    FormatLog(image, checked((agBlocks + LogStartAgBlock) * BlockSize), LogBlocks * BlockSize, cycle: 1);

    output.Position = 0;
    output.SetLength(0);
    output.Write(image);
    output.Position = 0;
  }

  internal static bool IsSupportedMutationProfile(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanRead || image.Length < SectorSize)
      return false;

    var original = image.Position;
    try {
      Span<byte> sb = stackalloc byte[SectorSize];
      image.Position = 0;
      image.ReadExactly(sb);
      if (BinaryPrimitives.ReadUInt32BigEndian(sb) != XfsMagic)
        return false;
      var version = BinaryPrimitives.ReadUInt16BigEndian(sb[100..]);
      return (version & 0xF) == 4
        && BinaryPrimitives.ReadUInt32BigEndian(sb[4..]) == BlockSize
        && BinaryPrimitives.ReadUInt16BigEndian(sb[102..]) == SectorSize
        && BinaryPrimitives.ReadUInt16BigEndian(sb[104..]) == InodeSize
        && sb[192] == 0
        && (version & 0x8000) == 0;
    } finally {
      image.Position = original;
    }
  }

  private static string NormalizeName(string name) {
    var normalized = name.Replace('\\', '/').Trim('/');
    if (normalized.Length == 0)
      throw new ArgumentException("CXFS v4 file name must not be empty.", nameof(name));
    if (normalized.Contains("//", StringComparison.Ordinal))
      throw new ArgumentException("CXFS v4 path must not contain an empty component.", nameof(name));

    foreach (var segment in normalized.Split('/')) {
      if (segment is "." or "..")
        throw new ArgumentException("CXFS v4 path must not contain '.' or '..' components.", nameof(name));
      var bytes = Encoding.UTF8.GetByteCount(segment);
      if (bytes is <= 0 or > 255)
        throw new NotSupportedException("CXFS v4 directory entry names must be 1..255 UTF-8 bytes.");
    }

    return normalized;
  }

  private static string ParentOf(string path) {
    var cut = path.LastIndexOf('/');
    return cut < 0 ? "" : path[..cut];
  }

  private static string NameOf(string path) {
    var cut = path.LastIndexOf('/');
    return cut < 0 ? path : path[(cut + 1)..];
  }

  private static void ValidateShortFormDirectory(string path, IReadOnlyList<DirectoryChild> entries) {
    var size = 6;
    foreach (var entry in entries)
      size += 7 + Encoding.UTF8.GetByteCount(entry.Name);
    if (size > ShortFormCapacity)
      throw new NotSupportedException(
        $"CXFS v4 mutation profile requires every directory to remain short-form; " +
        $"'{(path.Length == 0 ? "/" : path)}' needs {size} > {ShortFormCapacity} bytes.");
  }

  private readonly record struct DirectoryChild(string Name, ulong Inode, bool IsDirectory);

  private static int InodeOffset(int slot)
    => checked(InodeChunkBlock * BlockSize + slot * InodeSize);

  private static void WriteSuperblock(Span<byte> sb, long totalBlocks, ulong logStart, int agBlocks,
      byte agBlkLog, ulong icount, ulong ifree, ulong fdblocks, string volumeLabel) {
    sb.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(sb[0..], XfsMagic);
    BinaryPrimitives.WriteUInt32BigEndian(sb[4..], BlockSize);
    BinaryPrimitives.WriteUInt64BigEndian(sb[8..], (ulong)totalBlocks);
    UuidBytes.CopyTo(sb[32..]);
    BinaryPrimitives.WriteUInt64BigEndian(sb[48..], logStart);
    BinaryPrimitives.WriteUInt64BigEndian(sb[56..], RootIno);
    BinaryPrimitives.WriteUInt64BigEndian(sb[64..], RootIno + 1);
    BinaryPrimitives.WriteUInt64BigEndian(sb[72..], RootIno + 2);
    BinaryPrimitives.WriteUInt32BigEndian(sb[80..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(sb[84..], (uint)agBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(sb[88..], AgCount);
    BinaryPrimitives.WriteUInt32BigEndian(sb[96..], LogBlocks);
    BinaryPrimitives.WriteUInt16BigEndian(sb[100..], SuperblockVersion);
    BinaryPrimitives.WriteUInt16BigEndian(sb[102..], SectorSize);
    BinaryPrimitives.WriteUInt16BigEndian(sb[104..], InodeSize);
    BinaryPrimitives.WriteUInt16BigEndian(sb[106..], InodesPerBlock);
    if (!string.IsNullOrEmpty(volumeLabel)) {
      Span<byte> label = stackalloc byte[12];
      _ = Encoding.ASCII.GetBytes(volumeLabel.AsSpan(0, Math.Min(12, volumeLabel.Length)), label);
      label.CopyTo(sb[108..]);
    }
    sb[120] = BlockLog;
    sb[121] = SectorLog;
    sb[122] = InodeLog;
    sb[123] = InoPbLog;
    sb[124] = agBlkLog;
    sb[127] = 25;
    BinaryPrimitives.WriteUInt64BigEndian(sb[128..], icount);
    BinaryPrimitives.WriteUInt64BigEndian(sb[136..], ifree);
    BinaryPrimitives.WriteUInt64BigEndian(sb[144..], fdblocks);
    BinaryPrimitives.WriteUInt32BigEndian(sb[180..], 2);
    sb[192] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(sb[196..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(sb[200..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(sb[204..], 0);
  }

  private static void WriteAgf(Span<byte> agf, uint agNumber, uint agBlocks, uint freeBlocks) {
    agf.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(agf[0..], AgfMagic);
    BinaryPrimitives.WriteUInt32BigEndian(agf[4..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(agf[8..], agNumber);
    BinaryPrimitives.WriteUInt32BigEndian(agf[12..], agBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(agf[16..], BnobtBlock);
    BinaryPrimitives.WriteUInt32BigEndian(agf[20..], CntbtBlock);
    BinaryPrimitives.WriteUInt32BigEndian(agf[28..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(agf[32..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(agf[40..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(agf[44..], 127);
    BinaryPrimitives.WriteUInt32BigEndian(agf[48..], 0);
    BinaryPrimitives.WriteUInt32BigEndian(agf[52..], freeBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(agf[56..], freeBlocks);
  }

  private static void WriteAgi(Span<byte> agi, uint agNumber, uint agBlocks, int inodeCount,
      int freeInodes, uint newIno) {
    agi.Clear();
    BinaryPrimitives.WriteUInt32BigEndian(agi[0..], AgiMagic);
    BinaryPrimitives.WriteUInt32BigEndian(agi[4..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(agi[8..], agNumber);
    BinaryPrimitives.WriteUInt32BigEndian(agi[12..], agBlocks);
    BinaryPrimitives.WriteUInt32BigEndian(agi[16..], (uint)inodeCount);
    BinaryPrimitives.WriteUInt32BigEndian(agi[20..], InobtBlock);
    BinaryPrimitives.WriteUInt32BigEndian(agi[24..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(agi[28..], (uint)freeInodes);
    BinaryPrimitives.WriteUInt32BigEndian(agi[32..], newIno);
    BinaryPrimitives.WriteUInt32BigEndian(agi[36..], NullAgIno);
    for (var i = 0; i < 64; ++i)
      BinaryPrimitives.WriteUInt32BigEndian(agi[(40 + i * 4)..], NullAgIno);
  }

  private static void WriteAgflV4(Span<byte> agfl) {
    for (var offset = 0; offset + 4 <= agfl.Length; offset += 4)
      BinaryPrimitives.WriteUInt32BigEndian(agfl[offset..], NullAgBlock);
  }

  private static void WriteFreeBtree(Span<byte> block, uint magic, uint freeStart, uint freeLength) {
    block.Clear();
    WriteV4BtreeHeader(block, magic, freeLength > 0 ? (ushort)1 : (ushort)0);
    if (freeLength == 0)
      return;
    BinaryPrimitives.WriteUInt32BigEndian(block[BtreeRecordOffset..], freeStart);
    BinaryPrimitives.WriteUInt32BigEndian(block[(BtreeRecordOffset + 4)..], freeLength);
  }

  private static void WriteInobt(Span<byte> block, int usedSlots) {
    block.Clear();
    var hasChunk = usedSlots > 0;
    WriteV4BtreeHeader(block, InobtV4Magic, hasChunk ? (ushort)1 : (ushort)0);
    if (!hasChunk)
      return;
    var freeMask = usedSlots >= InodesPerChunk ? 0UL : ulong.MaxValue << usedSlots;
    BinaryPrimitives.WriteUInt32BigEndian(block[BtreeRecordOffset..], (uint)RootIno);
    BinaryPrimitives.WriteUInt32BigEndian(block[(BtreeRecordOffset + 4)..], (uint)BitOperations.PopCount(freeMask));
    BinaryPrimitives.WriteUInt64BigEndian(block[(BtreeRecordOffset + 8)..], freeMask);
  }

  private static void WriteV4BtreeHeader(Span<byte> block, uint magic, ushort records) {
    BinaryPrimitives.WriteUInt32BigEndian(block[0..], magic);
    BinaryPrimitives.WriteUInt16BigEndian(block[4..], 0);
    BinaryPrimitives.WriteUInt16BigEndian(block[6..], records);
    BinaryPrimitives.WriteUInt32BigEndian(block[8..], NullAgBlock);
    BinaryPrimitives.WriteUInt32BigEndian(block[12..], NullAgBlock);
  }

  private static void WriteInodeCoreV2(Span<byte> inode, ushort mode, byte format, uint nlink) {
    inode.Clear();
    BinaryPrimitives.WriteUInt16BigEndian(inode[0..], InodeMagic);
    BinaryPrimitives.WriteUInt16BigEndian(inode[2..], mode);
    inode[4] = 2;
    inode[5] = format;
    BinaryPrimitives.WriteUInt32BigEndian(inode[16..], nlink);
    inode[83] = 2;
    BinaryPrimitives.WriteUInt32BigEndian(inode[96..], NullAgIno);
  }

  /// <summary>
  /// Writes one dir2 short-form directory inode. <paramref name="parentInode"/> goes into the
  /// short-form header (the on-disk "..") and the entry list carries the direct children only.
  /// A directory's link count is 2 ("." plus its own name in the parent) plus one per subdirectory,
  /// because each subdirectory's ".." links back here.
  /// </summary>
  private static void WriteDirectoryInode(Span<byte> inode, ulong parentInode,
      IReadOnlyList<DirectoryChild> entries) {
    var subdirectories = 0;
    foreach (var entry in entries)
      if (entry.IsDirectory)
        ++subdirectories;

    WriteInodeCoreV2(inode, mode: 0x41ED, format: 1, nlink: (uint)(2 + subdirectories));
    var fork = inode[ForkOffset..];
    fork[0] = (byte)entries.Count;
    fork[1] = 0;
    BinaryPrimitives.WriteUInt32BigEndian(fork[2..], (uint)parentInode);

    var pos = 6;
    ushort dataOffset = 0x30;
    for (var i = 0; i < entries.Count; ++i) {
      var name = Encoding.UTF8.GetBytes(entries[i].Name);
      fork[pos] = (byte)name.Length;
      BinaryPrimitives.WriteUInt16BigEndian(fork[(pos + 1)..], dataOffset);
      name.CopyTo(fork[(pos + 3)..]);
      BinaryPrimitives.WriteUInt32BigEndian(fork[(pos + 3 + name.Length)..], (uint)entries[i].Inode);
      pos += 7 + name.Length;
      dataOffset = checked((ushort)(dataOffset + ((11 + name.Length + 7) & ~7)));
    }
    BinaryPrimitives.WriteUInt64BigEndian(inode[56..], (ulong)pos);
  }

  private static void WriteExtent(Span<byte> record, ulong startBlock, ulong blockCount) {
    var hi = (startBlock >> 43) & 0x1FF;
    var lo = (startBlock << 21) | (blockCount & 0x1FFFFF);
    BinaryPrimitives.WriteUInt64BigEndian(record[0..], hi);
    BinaryPrimitives.WriteUInt64BigEndian(record[8..], lo);
  }

  private static void FormatLog(byte[] image, int logOffsetBytes, int logSizeBytes, uint cycle) {
    const uint XlogMagic = 0xFEEDBABE;
    const uint XlogUnmountType = 0x556E;
    const byte XlogUnmountTransFlag = 0x20;
    const byte XfsLog = 0xAA;
    const int XlogBigRecordBsize = 32 * 1024;
    var log = image.AsSpan(logOffsetBytes, logSizeBytes);
    log.Clear();

    var lsn = (ulong)cycle << 32;
    BinaryPrimitives.WriteUInt32BigEndian(log[0..], XlogMagic);
    BinaryPrimitives.WriteUInt32BigEndian(log[4..], cycle);
    BinaryPrimitives.WriteUInt32BigEndian(log[8..], 2);
    BinaryPrimitives.WriteUInt32BigEndian(log[12..], 512);
    BinaryPrimitives.WriteUInt64BigEndian(log[16..], lsn);
    BinaryPrimitives.WriteUInt64BigEndian(log[24..], ((ulong)cycle << 32) | 2);
    BinaryPrimitives.WriteUInt32BigEndian(log[36..], uint.MaxValue);
    BinaryPrimitives.WriteUInt32BigEndian(log[40..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(log[300..], 1);
    UuidBytes.CopyTo(log[304..]);
    BinaryPrimitives.WriteUInt32BigEndian(log[320..], XlogBigRecordBsize);

    var unmountSector = log[SectorSize..];
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[0..], 0xB0C0D0D0u);
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[4..], 8);
    unmountSector[8] = XfsLog;
    unmountSector[9] = XlogUnmountTransFlag;
    BinaryPrimitives.WriteUInt16LittleEndian(unmountSector[12..], (ushort)XlogUnmountType);
    var savedFirst4 = BinaryPrimitives.ReadUInt32BigEndian(unmountSector);
    BinaryPrimitives.WriteUInt32BigEndian(log[44..], savedFirst4);
    BinaryPrimitives.WriteUInt32BigEndian(unmountSector[0..], cycle);
  }
}
