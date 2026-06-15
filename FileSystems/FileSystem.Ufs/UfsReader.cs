#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ufs;

/// <summary>
/// Reads UFS1 (FreeBSD/BSD FFS) filesystem images. Decodes the superblock at
/// <c>SBLOCK_UFS1 = 8192</c>, locates CG 0's inode table, walks the root
/// directory (inode 2) and extracts file contents via <c>di_db[]</c> direct
/// block pointers (indirect blocks are not followed — our writer never uses them).
/// <para>
/// All field offsets mirror FreeBSD's <c>struct fs</c> (<c>sys/ufs/ffs/fs.h</c>)
/// and <c>struct ufs1_dinode</c> (<c>sys/ufs/ufs/dinode.h</c>). <c>fs_magic</c>
/// sits at the last 4 bytes of the 1376-byte superblock (offset 1372).
/// </para>
/// </summary>
public sealed class UfsReader : IDisposable {
  private const int SuperblockOffset = 8192;
  private const int SuperblockSize = 1376;
  private const int FsMagicOffset = SuperblockSize - 4;
  private const uint Ufs1Magic = 0x00011954;
  private const int InodeSize = 128;
  private const int RootInode = 2;
  private const int MaxDirectBlocks = 12;

  private readonly byte[] _data;
  private readonly List<UfsEntry> _entries = [];

  private int _blockSize;
  private int _fragSize;
  private int _inodesPerGroup;
  private int _iblkno;         // inode-block offset within CG 0 (in frags)
  private int _fpg;            // frags per group
  private int _fsbtodb;        // log2(fs_fsize/DEV_BSIZE)
  private int _inodesPerBlock; // fs_inopb

  public IReadOnlyList<UfsEntry> Entries => _entries;

  public UfsReader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + SuperblockSize)
      throw new InvalidDataException("UFS: image too small to contain a UFS1 superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(sb[FsMagicOffset..]);
    if (magic != Ufs1Magic)
      throw new InvalidDataException($"UFS: invalid superblock magic 0x{magic:X8} (expected 0x{Ufs1Magic:X8}).");

    // Real spec offsets:
    _iblkno = BinaryPrimitives.ReadInt32LittleEndian(sb[16..]);          // fs_iblkno
    _blockSize = BinaryPrimitives.ReadInt32LittleEndian(sb[48..]);       // fs_bsize
    _fragSize = BinaryPrimitives.ReadInt32LittleEndian(sb[52..]);        // fs_fsize
    _fsbtodb = BinaryPrimitives.ReadInt32LittleEndian(sb[100..]);        // fs_fsbtodb
    _inodesPerBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[120..]); // fs_inopb
    _inodesPerGroup = (int)BinaryPrimitives.ReadUInt32LittleEndian(sb[184..]); // fs_ipg
    _fpg = BinaryPrimitives.ReadInt32LittleEndian(sb[188..]);            // fs_fpg

    if (_fragSize <= 0) _fragSize = 1024;
    if (_blockSize <= 0) _blockSize = 8192;
    if (_fpg <= 0) _fpg = 16384;
    if (_inodesPerGroup <= 0) _inodesPerGroup = 2048;
    if (_inodesPerBlock <= 0) _inodesPerBlock = _blockSize / InodeSize;

    ReadDirectory(RootInode, "");
  }

  private long InodeOffset(int ino) {
    // In a single-CG image, cgstart(0) = 0. Inode i → CG (i / fs_ipg), index (i % fs_ipg).
    // For our simple layout, inode table starts at (iblkno * fragSize) within CG 0.
    var cg = ino / _inodesPerGroup;
    var idx = ino % _inodesPerGroup;
    var cgStart = (long)cg * _fpg * _fragSize;
    return cgStart + (long)_iblkno * _fragSize + (long)idx * InodeSize;
  }

  private void ReadDirectory(int ino, string basePath) {
    var inodeOff = InodeOffset(ino);
    if (inodeOff + InodeSize > _data.Length) return;

    var dirData = ReadInodeData(inodeOff);
    if (dirData == null || dirData.Length == 0) return;

    var pos = 0;
    while (pos + 8 <= dirData.Length) {
      var dino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos));
      var reclen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 4));
      if (reclen < 8 || pos + reclen > dirData.Length) break;
      var namlen = dirData[pos + 7];

      if (dino != 0 && namlen > 0 && pos + 8 + namlen <= dirData.Length) {
        var name = Encoding.ASCII.GetString(dirData, pos + 8, namlen);
        // Skip "." / ".." and the synthetic newfs snapshot directory.
        if (name != "." && name != ".." && !(basePath.Length == 0 && name == ".snap")) {
          var childInodeOff = InodeOffset((int)dino);
          var isDir = false;
          long size = 0;
          DateTime? mtime = null;

          if (childInodeOff + InodeSize <= _data.Length) {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)childInodeOff));
            isDir = (mode & 0xF000) == 0x4000;
            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan((int)(childInodeOff + 8)));
            var mt = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)(childInodeOff + 24)));
            if (mt > 0) mtime = DateTimeOffset.FromUnixTimeSeconds(mt).UtcDateTime;
          }

          var fullPath = string.IsNullOrEmpty(basePath) ? name : $"{basePath}/{name}";
          _entries.Add(new UfsEntry {
            Name = fullPath,
            Size = isDir ? 0 : size,
            IsDirectory = isDir,
            LastModified = mtime,
            Inode = (int)dino,
          });
          if (isDir) ReadDirectory((int)dino, fullPath);
        }
      }
      pos += reclen;
    }
  }

  private byte[]? ReadInodeData(long inodeOff) {
    if (inodeOff + InodeSize > _data.Length) return null;
    var ioff = (int)inodeOff;
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(ioff + 8));
    if (size <= 0 || size > 256L * 1024 * 1024) return null;

    using var ms = new MemoryStream();

    // Direct blocks (di_db[0..11]).
    for (var i = 0; i < MaxDirectBlocks && ms.Length < size; i++) {
      var blk = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(ioff + 40 + i * 4));
      AppendBlock(ms, blk, size);
    }

    // Single-indirect block (di_ib[0]): a table of block pointers immediately
    // following the 12 direct pointers.
    if (ms.Length < size) {
      var indirect = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(ioff + 40 + MaxDirectBlocks * 4));
      if (indirect != 0) {
        var tableOff = (long)indirect * _fragSize;
        var ptrs = _blockSize / 4;
        for (var i = 0; i < ptrs && ms.Length < size; i++) {
          var ptrOff = tableOff + (long)i * 4;
          if (ptrOff + 4 > _data.Length) break;
          var blk = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan((int)ptrOff));
          if (blk == 0) break;
          AppendBlock(ms, blk, size);
        }
      }
    }
    return ms.ToArray();
  }

  // Appends up to one block of payload from frag-block `blk`, stopping at `size`.
  private void AppendBlock(MemoryStream ms, int blk, long size) {
    if (blk == 0) return;
    var off = (long)blk * _fragSize;
    var remaining = size - ms.Length;
    if (remaining <= 0) return;
    var chunk = (int)Math.Min(_blockSize, remaining);
    if (off + chunk <= _data.Length)
      ms.Write(_data, (int)off, chunk);
  }

  public byte[] Extract(UfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inodeOff = InodeOffset(entry.Inode);
    var data = ReadInodeData(inodeOff);
    if (data == null) return [];
    if (data.Length > entry.Size) return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
