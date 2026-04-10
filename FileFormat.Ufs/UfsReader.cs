#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Ufs;

public sealed class UfsReader : IDisposable {
  private const int SuperblockOffset = 8192;
  private const uint Ufs1Magic = 0x00011954;
  private const int RootInode = 2;

  private readonly byte[] _data;
  private readonly List<UfsEntry> _entries = [];

  private int _blockSize;
  private int _fragSize;
  private int _inodesPerGroup;
  private int _iblkno; // inode block offset within CG (in fragments)
  private int _fpg; // fragments per group
  private int _inodeSize = 128; // UFS1 default

  public IReadOnlyList<UfsEntry> Entries => _entries;

  public UfsReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 1384)
      throw new InvalidDataException("UFS: image too small.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 1372));
    if (magic != Ufs1Magic)
      throw new InvalidDataException("UFS: invalid superblock magic.");

    _fragSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 84));
    _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 88));
    _inodesPerGroup = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 1268));
    _iblkno = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 16));
    _fpg = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(SuperblockOffset + 1380));

    if (_fragSize == 0) _fragSize = 1024;
    if (_blockSize == 0) _blockSize = 8192;
    if (_fpg == 0) _fpg = _blockSize * 8;
    if (_inodesPerGroup == 0) _inodesPerGroup = 1;

    ReadDirectory(RootInode, "");
  }

  private int InodeOffset(int ino) {
    var cg = ino / _inodesPerGroup;
    var idx = ino % _inodesPerGroup;
    var cgStart = cg * _fpg * _fragSize;
    return cgStart + _iblkno * _fragSize + idx * _inodeSize;
  }

  private void ReadDirectory(int ino, string basePath) {
    var inodeOff = InodeOffset(ino);
    if (inodeOff + _inodeSize > _data.Length) return;

    var dirData = ReadInodeData(inodeOff);
    if (dirData == null || dirData.Length == 0) return;

    var pos = 0;
    while (pos + 8 <= dirData.Length) {
      var dino = BinaryPrimitives.ReadUInt32LittleEndian(dirData.AsSpan(pos));
      var reclen = BinaryPrimitives.ReadUInt16LittleEndian(dirData.AsSpan(pos + 4));
      if (reclen < 8) break;
      var namlen = dirData[pos + 7];

      if (dino != 0 && namlen > 0 && pos + 8 + namlen <= dirData.Length) {
        var name = Encoding.ASCII.GetString(dirData, pos + 8, namlen);
        if (name != "." && name != "..") {
          var childInodeOff = InodeOffset((int)dino);
          var isDir = false;
          long size = 0;
          DateTime? mtime = null;

          if (childInodeOff + _inodeSize <= _data.Length) {
            var mode = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(childInodeOff));
            isDir = (mode & 0xF000) == 0x4000;
            size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(childInodeOff + 8));
            var mt = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(childInodeOff + 24));
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

          if (isDir)
            ReadDirectory((int)dino, fullPath);
        }
      }
      pos += reclen;
    }
  }

  private byte[]? ReadInodeData(int inodeOff) {
    if (inodeOff + 92 > _data.Length) return null;
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(inodeOff + 8));
    if (size <= 0 || size > 10 * 1024 * 1024) return null;

    using var ms = new MemoryStream();
    // Read direct blocks (12 x uint32 at offset 40)
    for (int i = 0; i < 12 && ms.Length < size; i++) {
      var blk = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(inodeOff + 40 + i * 4));
      if (blk == 0) continue;
      var off = (long)blk * _fragSize;
      var len = (int)Math.Min(_blockSize, size - ms.Length);
      if (off + len <= _data.Length)
        ms.Write(_data, (int)off, len);
    }
    return ms.ToArray();
  }

  public byte[] Extract(UfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var inodeOff = InodeOffset(entry.Inode);
    var data = ReadInodeData(inodeOff);
    if (data == null) return [];
    if (data.Length > entry.Size)
      return data.AsSpan(0, (int)entry.Size).ToArray();
    return data;
  }

  public void Dispose() { }
}
