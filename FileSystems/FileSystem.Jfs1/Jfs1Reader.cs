#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jfs1;

/// <summary>
/// Read-side companion to <see cref="Jfs1Writer"/>. Walks the JFS1 superblock,
/// inode array, and writer-emitted directory blocks to surface every file at
/// its full nested path.
/// </summary>
public sealed class Jfs1Reader {
  private readonly byte[] _image;
  private readonly int _blockSize;
  private readonly int _inodesPerBlock;
  private readonly List<Jfs1Entry> _entries = [];

  public Jfs1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _image = ms.ToArray();
    var sb = Jfs1Superblock.TryParse(_image);
    if (!sb.Valid) throw new InvalidDataException("Not a JFS1 image: superblock magic mismatch.");
    _blockSize = (int)sb.BlockSize;
    if (_blockSize <= 0) _blockSize = Jfs1Writer.DefaultBlockSize;
    _inodesPerBlock = _blockSize / Jfs1Writer.InodeSize;
    Recurse(2, "");
  }

  public IReadOnlyList<Jfs1Entry> Entries => _entries;

  public byte[] Extract(Jfs1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size == 0 || entry.FirstBlock == 0) return [];
    var start = entry.FirstBlock * _blockSize;
    if (start + entry.Size > _image.Length)
      throw new InvalidDataException("JFS1 extract: extent reaches past image end.");
    return _image.AsSpan(start, (int)entry.Size).ToArray();
  }

  private void Recurse(int inode, string prefix) {
    var info = ReadInode(inode);
    if (!info.IsDirectory || info.FirstBlock == 0) return;
    var off = info.FirstBlock * _blockSize;
    if (off >= _image.Length) return;
    var blk = _image.AsSpan(off, Math.Min(_blockSize, _image.Length - off));
    if (blk.Length < 4) return;
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(blk[..2]);
    if (magic != Jfs1Writer.DirBlockMagicConst) return;
    int slots = BinaryPrimitives.ReadUInt16LittleEndian(blk[2..4]);
    var cur = 4;
    for (var i = 0; i < slots && cur + 5 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt32LittleEndian(blk[cur..]);
      int nlen = blk[cur + 4];
      if (cur + 5 + nlen > blk.Length) break;
      var name = Encoding.UTF8.GetString(blk.Slice(cur + 5, nlen));
      cur += 5 + nlen;
      if (name is "." or "..") continue;
      var childInfo = ReadInode((int)childInode);
      var full = prefix.Length == 0 ? name : $"{prefix}/{name}";
      _entries.Add(new Jfs1Entry(full, (int)childInode, childInfo.IsDirectory, childInfo.Size, childInfo.FirstBlock));
      if (childInfo.IsDirectory) Recurse((int)childInode, full);
    }
  }

  private InodeInfo ReadInode(int inode) {
    var inodeStart = 1;
    var blockOff = (inode - 2) / _inodesPerBlock;
    var slotOff = (inode - 2) % _inodesPerBlock;
    var ip = _image.AsSpan((inodeStart + blockOff) * _blockSize + slotOff * Jfs1Writer.InodeSize, Jfs1Writer.InodeSize);
    var firstBlock = (int)BinaryPrimitives.ReadUInt32LittleEndian(ip[16..]);
    var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(ip[24..]);
    var mode = BinaryPrimitives.ReadUInt32LittleEndian(ip[52..]);
    return new InodeInfo((mode & 0xF000) == 0x4000, size, firstBlock);
  }

  private readonly record struct InodeInfo(bool IsDirectory, long Size, int FirstBlock);
}

/// <summary>One entry surfaced by <see cref="Jfs1Reader"/>.</summary>
public sealed record Jfs1Entry(string Name, int Inode, bool IsDirectory, long Size, int FirstBlock);
