#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Htfs;

/// <summary>
/// Read-side companion to <see cref="HtfsWriter"/>. Walks the SB at sector 1,
/// the inode array immediately after, and every directory body to surface
/// the file tree at full nested paths.
/// </summary>
public sealed class HtfsReader {
  /// <summary>
  /// Random-access view over the image. Copying the volume into a byte[] capped
  /// the reader at the array limit, which the on-disk block addresses do not.
  /// </summary>
  private readonly ImageAccessor _image;
  private readonly int _blockSize;
  private readonly int _inodeStart;
  private readonly int _inodesPerBlock;
  private readonly List<HtfsEntry> _entries = [];

  public HtfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    _image = new ImageAccessor(stream, leaveOpen: true);
    var sb = HtfsSuperblock.TryParse(_image.Read(0, (int)Math.Min(_image.Length, 1024 * 1024)));
    if (!sb.Valid) throw new InvalidDataException("Not an HTFS image: superblock magic mismatch.");

    // Block size auto-detect: try 512/1024/2048 and pick the one whose
    // computed fsize×blocksize ≈ image length.
    _blockSize = DetectBlockSize(sb.Fsize);
    _inodesPerBlock = _blockSize / HtfsWriter.InodeSize;
    _inodeStart = (HtfsWriter.SuperblockOffset / _blockSize) + 1;
    Recurse(2, "");
  }

  /// <summary>All non-root entries (files + intermediate directories).</summary>
  public IReadOnlyList<HtfsEntry> Entries => _entries;

  /// <summary>Extracts the file's bytes via its single contiguous extent.</summary>
  public byte[] Extract(HtfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size == 0 || entry.FirstBlock == 0) return [];
    // Block index times block size in ints wraps a couple of gigabytes in,
    // and the file was then read from a negative offset — or from another
    // file's bytes once the wrap landed back inside the volume.
    var start = (long)entry.FirstBlock * _blockSize;
    if (start + entry.Size > _image.Length)
      throw new InvalidDataException("HTFS extract: extent reaches past image end.");
    return _image.Read(start, entry.Size);
  }

  private int DetectBlockSize(uint sbFsize) {
    // Common HTFS block sizes; pick the one whose fsize×bs lands closest to image length.
    foreach (var bs in new[] { 512, 1024, 2048 }) {
      var implied = (long)sbFsize * bs;
      if (implied >= _image.Length - bs && implied <= _image.Length + bs) return bs;
    }
    return HtfsWriter.DefaultBlockSize;
  }

  private void Recurse(int inode, string prefix) {
    var info = ReadInode(inode);
    if (!info.IsDirectory || info.FirstBlock == 0) return;
    var off = (long)info.FirstBlock * _blockSize;
    if (off >= _image.Length) return;
    var blk = _image.Read(off, (int)Math.Min(_blockSize, _image.Length - off));
    for (var cur = 0; cur + 16 <= blk.Length; cur += 16) {
      var childInode = BinaryPrimitives.ReadUInt16LittleEndian(blk[cur..]);
      if (childInode == 0) continue;
      var nameSpan = blk.AsSpan(cur + 2, HtfsWriter.MaxNameLen);
      var nul = nameSpan.IndexOf((byte)0);
      var len = nul < 0 ? nameSpan.Length : nul;
      if (len == 0) continue;
      var name = Encoding.ASCII.GetString(nameSpan[..len]);
      if (name is "." or "..") continue;
      var childInfo = ReadInode(childInode);
      var full = prefix.Length == 0 ? name : $"{prefix}/{name}";
      _entries.Add(new HtfsEntry(full, childInode, childInfo.IsDirectory, childInfo.Size, childInfo.FirstBlock));
      if (childInfo.IsDirectory) Recurse(childInode, full);
    }
  }

  private InodeInfo ReadInode(int inode) {
    var blockOff = (inode - 2) / _inodesPerBlock;
    var slotOff = (inode - 2) % _inodesPerBlock;
    var ip = _image.Read(((long)_inodeStart + blockOff) * _blockSize + slotOff * HtfsWriter.InodeSize, HtfsWriter.InodeSize);
    var mode = BinaryPrimitives.ReadUInt16LittleEndian(ip[..2]);
    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(ip[8..]);
    var first = (int)BinaryPrimitives.ReadUInt32LittleEndian(ip[24..]);
    return new InodeInfo((mode & 0xF000) == 0x4000, size, first);
  }

  private readonly record struct InodeInfo(bool IsDirectory, int Size, int FirstBlock);
}

/// <summary>One entry surfaced by <see cref="HtfsReader"/>.</summary>
public sealed record HtfsEntry(string Name, int Inode, bool IsDirectory, int Size, int FirstBlock);
