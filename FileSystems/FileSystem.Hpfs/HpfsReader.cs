#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Hpfs;

/// <summary>
/// Read-only reader for OS/2 HPFS (High Performance File System) volumes.
/// </summary>
/// <remarks>
/// <para>Scope (intentionally narrow — enough for typical test images):</para>
/// <list type="bullet">
///   <item>Hierarchical: subdirectories are descended and files are surfaced at
///   their full nested path (segments joined with '/').</item>
///   <item>Large directories: dirent blocks are read as a B-tree — each dirent's
///   down-pointer (and the end sentinel's rightmost down-pointer) is followed, so
///   a directory spanning many dirent blocks is read in full.</item>
///   <item>Small files using the fnode's direct allocation list (no AllocSec B-tree traversal).</item>
/// </list>
/// <para>Larger files (those whose fnode height field is non-zero, indicating an
/// AllocSec B-tree) are listed but return empty byte arrays on extract; this is
/// documented as deferred.</para>
/// <para>Layout references:</para>
/// <list type="bullet">
///   <item>LBA size: 512 bytes.</item>
///   <item>Boot sector at LBA 0.</item>
///   <item>Superblock at LBA 16, 8-byte magic <c>49 E8 95 F9 C5 E9 53 FA</c> at offset 0
///   (0xF995E849 / 0xFA53E9C5 stored little-endian).</item>
///   <item>Superblock offset 12 (uint32 LE): root-fnode LBA.</item>
///   <item>Fnode (512 bytes): magic <c>AE 0A E4 F7</c> at offset 0 (0xF7E40AAE LE).</item>
///   <item>Directory block (2 KiB = 4 LBAs): magic <c>AE 0A E4 77</c> at offset 0 (0x77E40AAE LE).</item>
///   <item>Dirent: uint16 record-length (off 0), uint16 flags (off 2), uint32 fnode-LBA
///   (off 4), uint32 file-size (off 12), byte name-length (off 30), name bytes at offset 31.</item>
/// </list>
/// </remarks>
public sealed class HpfsReader : IDisposable {

  public const int LbaSize = 512;
  public const int SuperblockLba = 16;
  public const int DirBlockSize = 2048;

  /// <summary>
  /// Superblock magic — the uint32 pair 0xF995E849 / 0xFA53E9C5 stored little-endian,
  /// i.e. the bytes <c>49 E8 95 F9 C5 E9 53 FA</c>.
  /// </summary>
  public static readonly byte[] SuperblockMagic =
    [0x49, 0xE8, 0x95, 0xF9, 0xC5, 0xE9, 0x53, 0xFA];

  /// <summary>Fnode magic 0xF7E40AAE little-endian: <c>AE 0A E4 F7</c>.</summary>
  public static readonly byte[] FnodeMagic = [0xAE, 0x0A, 0xE4, 0xF7];

  /// <summary>Dirent-block magic 0x77E40AAE little-endian: <c>AE 0A E4 77</c>.</summary>
  public static readonly byte[] DirBlockMagic = [0xAE, 0x0A, 0xE4, 0x77];

  /// <summary>Random-access view; the volume is never copied into an array.</summary>
  private readonly ImageAccessor _data;
  private readonly List<HpfsEntry> _entries = [];

  /// <summary>Root-fnode LBA from the superblock.</summary>
  public uint RootFnodeLba { get; }

  public IReadOnlyList<HpfsEntry> Entries => _entries;

  public HpfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    _data = new ImageAccessor(stream, leaveOpen: true);

    var sbOff = SuperblockLba * LbaSize;
    if (_data.Length < sbOff + LbaSize)
      throw new InvalidDataException("HPFS: image too small for superblock.");

    for (var i = 0; i < SuperblockMagic.Length; i++)
      if (_data.ReadByte(sbOff + i) != SuperblockMagic[i])
        throw new InvalidDataException("HPFS: missing superblock magic at LBA 16.");

    RootFnodeLba = _data.ReadUInt32(sbOff + 12);

    ParseRootDirectory();
  }

  public HpfsReader(byte[] data) : this(new MemoryStream(data)) { }

  // 64-bit: an HPFS volume past 2 GB overflows the int product.
  private long LbaOffset(uint lba) => (long)lba * LbaSize;

  private void ParseRootDirectory() {
    // Step 1: open the root fnode. Its first direct-allocation entry points to the
    // dirent block for the root. Simplification: assume direct allocations only.
    var fnodeOff = LbaOffset(RootFnodeLba);
    if (fnodeOff + LbaSize > _data.Length) return;

    // Verify fnode magic (lenient — some test images may elide it).
    var hasFnodeMagic = FnodeMagic.AsSpan()
      .SequenceEqual(_data.Read(fnodeOff, FnodeMagic.Length).AsSpan());

    // Direct allocation list: offset 0xC4 (196) in the fnode. 8 entries * 12 bytes each.
    // Each entry: [4:logical-sector-offset][4:length-in-sectors][4:physical-LBA].
    // For the root fnode the first entry's physical LBA points at the dirent block.
    uint rootDirLba;
    if (hasFnodeMagic) {
      rootDirLba = _data.ReadUInt32(fnodeOff + 0xC4 + 8);
    } else {
      // Fallback: scan the first 512 bytes for a plausible dirent-block magic pointer.
      rootDirLba = ScanForDirBlockLba(fnodeOff);
    }

    if (rootDirLba == 0) return;
    ParseDirectoryBlock(rootDirLba, pathPrefix: "", depth: 0);
  }

  /// <summary>Resolves the dirent block of a (sub)directory from its fnode's first
  /// direct-allocation entry, then parses it.</summary>
  private void ParseSubdirectory(uint dirFnodeLba, string pathPrefix, int depth) {
    var fnodeOff = LbaOffset(dirFnodeLba);
    if (fnodeOff < 0 || fnodeOff + LbaSize > _data.Length) return;

    // The directory's dirent block is the physical LBA of the fnode's first
    // direct-allocation entry (offset 0xC4 + 8). Direct-allocation only.
    if (IsBtreeFnode(dirFnodeLba)) return; // dirent-block B-tree spill not supported
    var dirBlockLba = _data.ReadUInt32(fnodeOff + 0xC4 + 8);
    if (dirBlockLba == 0) return;
    ParseDirectoryBlock(dirBlockLba, pathPrefix, depth);
  }

  private uint ScanForDirBlockLba(long fnodeOff) {
    for (var i = 0; i < LbaSize - 4; i += 4) {
      var candidate = _data.ReadUInt32(fnodeOff + i);
      var target = LbaOffset(candidate);
      if (target < 0 || target + DirBlockMagic.Length > _data.Length) continue;
      if (DirBlockMagic.AsSpan().SequenceEqual(_data.Read(target, DirBlockMagic.Length).AsSpan()))
        return candidate;
    }
    return 0;
  }

  /// <summary>Maximum directory nesting depth we will descend, as a guard against
  /// cyclic or corrupt parent/child fnode references.</summary>
  private const int MaxDepth = 64;

  private void ParseDirectoryBlock(uint dirLba, string pathPrefix, int depth) {
    ParseDirentBlock(dirLba, pathPrefix, depth, blockDepth: 0,
      visitedBlocks: []);
  }

  /// <summary>Maximum dirent-block B-tree depth we descend, guarding against
  /// cyclic or corrupt down-pointers.</summary>
  private const int MaxBlockDepth = 32;

  /// <summary>
  /// Parses a single dirent block, following B-tree down-pointers so a directory
  /// whose children span multiple dirent blocks is read in full. Each dirent may
  /// carry a down-pointer (flag bit 2) to a child dirent block holding the keys
  /// that sort before it; the end-of-block sentinel may carry the down-pointer to
  /// the rightmost child. Children are surfaced in B-tree order: a dirent's left
  /// subtree is read before the dirent itself.
  /// </summary>
  private void ParseDirentBlock(uint dirLba, string pathPrefix, int depth, int blockDepth, HashSet<uint> visitedBlocks) {
    if (depth > MaxDepth || blockDepth > MaxBlockDepth) return;
    if (!visitedBlocks.Add(dirLba)) return; // already visited this block — avoid cycles

    var off = LbaOffset(dirLba);
    if (off < 0 || off + DirBlockSize > _data.Length) return;

    // Verify directory-block magic.
    for (var i = 0; i < DirBlockMagic.Length; i++)
      if (_data.ReadByte(off + i) != DirBlockMagic[i])
        return;

    // Dirent records start at offset 0x14 (20) into the 2 KiB block, per HPFS spec.
    var cursor = off + 0x14;
    var blockEnd = off + DirBlockSize;
    var safety = 0;

    while (cursor < blockEnd && safety++ < 1024) {
      var recLen = _data.ReadUInt16(cursor);
      if (recLen < 32 || cursor + recLen > blockEnd) break;

      var flags = _data.ReadUInt16(cursor + 2);

      // Bit 0 (0x0001): "special" entry — either ".." or end-of-block sentinel.
      // Bit 2 (0x0004): B-tree down-pointer present (4-byte LBA at record tail).
      // Bit 3 (0x0008): directory.
      var isSpecial = (flags & 0x0001) != 0;
      var hasDownPointer = (flags & 0x0004) != 0;
      var isDirectory = (flags & 0x0008) != 0;

      // In-order traversal: read this dirent's left subtree before the dirent.
      if (hasDownPointer && cursor + recLen <= blockEnd) {
        var childLba = _data.ReadUInt32(cursor + recLen - 4);
        if (childLba != 0)
          ParseDirentBlock(childLba, pathPrefix, depth, blockDepth + 1, visitedBlocks);
      }

      // The end-of-block sentinel terminates the dirent list (its down-pointer,
      // the rightmost child, was already followed above).
      if (isSpecial && _data.ReadByte(cursor + 30) == 0)
        break;

      var fnodeLba = _data.ReadUInt32(cursor + 4);
      var fileSize = _data.ReadUInt32(cursor + 12);
      var nameLen = _data.ReadByte(cursor + 30);

      if (!isSpecial && nameLen > 0 && cursor + 31 + nameLen <= blockEnd) {
        var name = Encoding.Latin1.GetString(_data.Read(cursor + 31, nameLen));

        // Skip the "." / ".." self/parent links rather than recursing into them.
        if (name is not ("." or "..")) {
          var fullPath = pathPrefix.Length == 0 ? name : pathPrefix + "/" + name;

          // Detect files using the allocation B-tree (unsupported scope).
          var btree = IsBtreeFnode(fnodeLba);

          _entries.Add(new HpfsEntry {
            Name = fullPath,
            Size = isDirectory ? 0 : fileSize,
            IsDirectory = isDirectory,
            FnodeLba = fnodeLba,
            DataLba = btree || isDirectory ? 0u : GetFirstDataLbaFromFnode(fnodeLba),
            IsBtreeFile = btree && !isDirectory,
          });

          // Descend into subdirectories, surfacing their files at nested paths.
          if (isDirectory)
            ParseSubdirectory(fnodeLba, fullPath, depth + 1);
        }
      }

      cursor += recLen;
    }
  }

  private bool IsBtreeFnode(uint fnodeLba) {
    var off = LbaOffset(fnodeLba);
    if (off + 0xC4 + 12 > _data.Length) return false;
    // AllocSec header at offset 0xC0 (192) in the fnode. Offset 0xC0+7 = height.
    // Height 0 means direct allocation list follows; >0 means B-tree.
    var height = _data.ReadByte(off + 0xC0 + 7);
    return height != 0;
  }

  private uint GetFirstDataLbaFromFnode(uint fnodeLba) {
    var off = LbaOffset(fnodeLba);
    if (off + 0xC4 + 12 > _data.Length) return 0;
    return _data.ReadUInt32(off + 0xC4 + 8);
  }

  public byte[] Extract(HpfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.IsBtreeFile) return [];  // scope cut: B-tree allocation not yet supported
    if (entry.DataLba == 0 || entry.Size == 0) return [];

    var off = LbaOffset(entry.DataLba);
    var len = (int)Math.Min(entry.Size, int.MaxValue);
    if (off < 0 || off + len > _data.Length) return [];
    var result = new byte[len];
    _data.Read(off, len).CopyTo(result, 0);
    return result;
  }

  public void Dispose() { }
}
