#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Hpfs;

/// <summary>
/// Read-only reader for OS/2 HPFS (High Performance File System) volumes.
/// </summary>
/// <remarks>
/// <para>Scope (intentionally narrow — enough for typical test images):</para>
/// <list type="bullet">
///   <item>Root directory only (no subdirectory descent).</item>
///   <item>Small files using the fnode's direct allocation list (no AllocSec B-tree traversal).</item>
/// </list>
/// <para>Larger files (those whose fnode height field is non-zero, indicating an
/// AllocSec B-tree) are listed but return empty byte arrays on extract; this is
/// documented as deferred.</para>
/// <para>Layout references:</para>
/// <list type="bullet">
///   <item>LBA size: 512 bytes.</item>
///   <item>Boot sector at LBA 0.</item>
///   <item>Superblock at LBA 16, 8-byte magic <c>F9 95 E8 F9 FA 53 E9 F9</c> at offset 0.</item>
///   <item>Superblock offset 12 (uint32 LE): root-fnode LBA.</item>
///   <item>Fnode (512 bytes): magic <c>F7 E4 0A AE</c> at offset 0.</item>
///   <item>Directory block (2 KiB = 4 LBAs): magic <c>77 E4 0A AE</c> at offset 0.</item>
///   <item>Dirent: uint16 record-length (off 0), uint16 flags (off 2), uint32 fnode-LBA
///   (off 4), uint32 file-size (off 12), byte name-length (off 30), name bytes at offset 31.</item>
/// </list>
/// </remarks>
public sealed class HpfsReader : IDisposable {

  public const int LbaSize = 512;
  public const int SuperblockLba = 16;
  public const int DirBlockSize = 2048;

  /// <summary>Superblock magic <c>F9 95 E8 F9 FA 53 E9 F9</c>.</summary>
  public static readonly byte[] SuperblockMagic =
    [0xF9, 0x95, 0xE8, 0xF9, 0xFA, 0x53, 0xE9, 0xF9];

  /// <summary>Fnode magic <c>F7 E4 0A AE</c>.</summary>
  public static readonly byte[] FnodeMagic = [0xF7, 0xE4, 0x0A, 0xAE];

  /// <summary>Dirent-block magic <c>77 E4 0A AE</c>.</summary>
  public static readonly byte[] DirBlockMagic = [0x77, 0xE4, 0x0A, 0xAE];

  private readonly byte[] _data;
  private readonly List<HpfsEntry> _entries = [];

  /// <summary>Root-fnode LBA from the superblock.</summary>
  public uint RootFnodeLba { get; }

  public IReadOnlyList<HpfsEntry> Entries => _entries;

  public HpfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();

    var sbOff = SuperblockLba * LbaSize;
    if (_data.Length < sbOff + LbaSize)
      throw new InvalidDataException("HPFS: image too small for superblock.");

    for (var i = 0; i < SuperblockMagic.Length; i++)
      if (_data[sbOff + i] != SuperblockMagic[i])
        throw new InvalidDataException("HPFS: missing superblock magic at LBA 16.");

    RootFnodeLba = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(sbOff + 12));

    ParseRootDirectory();
  }

  public HpfsReader(byte[] data) : this(new MemoryStream(data)) { }

  private int LbaOffset(uint lba) => (int)lba * LbaSize;

  private void ParseRootDirectory() {
    // Step 1: open the root fnode. Its first direct-allocation entry points to the
    // dirent block for the root. Simplification: assume direct allocations only.
    var fnodeOff = LbaOffset(RootFnodeLba);
    if (fnodeOff + LbaSize > _data.Length) return;

    // Verify fnode magic (lenient — some test images may elide it).
    var hasFnodeMagic = FnodeMagic.AsSpan()
      .SequenceEqual(_data.AsSpan(fnodeOff, FnodeMagic.Length));

    // Direct allocation list: offset 0xC4 (196) in the fnode. 8 entries * 12 bytes each.
    // Each entry: [4:logical-sector-offset][4:length-in-sectors][4:physical-LBA].
    // For the root fnode the first entry's physical LBA points at the dirent block.
    uint rootDirLba;
    if (hasFnodeMagic) {
      rootDirLba = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(fnodeOff + 0xC4 + 8));
    } else {
      // Fallback: scan the first 512 bytes for a plausible dirent-block magic pointer.
      rootDirLba = ScanForDirBlockLba(fnodeOff);
    }

    if (rootDirLba == 0) return;
    ParseDirectoryBlock(rootDirLba);
  }

  private uint ScanForDirBlockLba(int fnodeOff) {
    for (var i = 0; i < LbaSize - 4; i += 4) {
      var candidate = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(fnodeOff + i));
      var target = LbaOffset(candidate);
      if (target < 0 || target + DirBlockMagic.Length > _data.Length) continue;
      if (DirBlockMagic.AsSpan().SequenceEqual(_data.AsSpan(target, DirBlockMagic.Length)))
        return candidate;
    }
    return 0;
  }

  private void ParseDirectoryBlock(uint dirLba) {
    var off = LbaOffset(dirLba);
    if (off + DirBlockSize > _data.Length) return;

    // Verify directory-block magic.
    for (var i = 0; i < DirBlockMagic.Length; i++)
      if (_data[off + i] != DirBlockMagic[i])
        return;

    // Dirent records start at offset 0x14 (20) into the 2 KiB block, per HPFS spec.
    var cursor = off + 0x14;
    var blockEnd = off + DirBlockSize;
    var safety = 0;

    while (cursor < blockEnd && safety++ < 512) {
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(cursor));
      if (recLen < 32 || cursor + recLen > blockEnd) break;

      var flags = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(cursor + 2));

      // Bit 0 (0x0001): "special" entry — either ".." or end-of-block sentinel.
      // Bit 3 (0x0008): directory.
      var isSpecial = (flags & 0x0001) != 0;
      var isDirectory = (flags & 0x0008) != 0;

      var fnodeLba = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(cursor + 4));
      var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(cursor + 12));
      var nameLen = _data[cursor + 30];

      if (!isSpecial && nameLen > 0 && cursor + 31 + nameLen <= blockEnd) {
        var name = Encoding.Latin1.GetString(_data, cursor + 31, nameLen);

        // Detect files using the allocation B-tree (unsupported scope).
        var btree = IsBtreeFnode(fnodeLba);

        _entries.Add(new HpfsEntry {
          Name = name,
          Size = fileSize,
          IsDirectory = isDirectory,
          FnodeLba = fnodeLba,
          DataLba = btree || isDirectory ? 0u : GetFirstDataLbaFromFnode(fnodeLba),
          IsBtreeFile = btree && !isDirectory,
        });
      }

      cursor += recLen;
    }
  }

  private bool IsBtreeFnode(uint fnodeLba) {
    var off = LbaOffset(fnodeLba);
    if (off + 0xC4 + 12 > _data.Length) return false;
    // AllocSec header at offset 0xC0 (192) in the fnode. Offset 0xC0+7 = height.
    // Height 0 means direct allocation list follows; >0 means B-tree.
    var height = _data[off + 0xC0 + 7];
    return height != 0;
  }

  private uint GetFirstDataLbaFromFnode(uint fnodeLba) {
    var off = LbaOffset(fnodeLba);
    if (off + 0xC4 + 12 > _data.Length) return 0;
    return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(off + 0xC4 + 8));
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
    Buffer.BlockCopy(_data, off, result, 0, len);
    return result;
  }

  public void Dispose() { }
}
