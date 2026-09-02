#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Gfs1;

/// <summary>
/// Read-side companion to <see cref="Gfs1Writer"/>. Walks the superblock at
/// byte offset 65536, the inode table immediately following it, and every
/// directory body, surfacing files at full nested paths.
/// </summary>
public sealed class Gfs1Reader {
  /// <summary>
  /// Random-access view over the image. Copying the volume into a byte[] capped
  /// the reader at the array limit, which the on-disk block addresses do not.
  /// </summary>
  private readonly ImageAccessor _image;
  private readonly int _inodeStart;
  private readonly int _inodesPerBlock;
  private readonly List<Gfs1Entry> _entries = [];

    /// <summary>
  /// Initializes a new instance of <see cref="Gfs1Reader"/>.
  /// </summary>
public Gfs1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    _image = new ImageAccessor(stream, leaveOpen: true);

    var sb = Gfs1Superblock.TryParse(_image.Read(0, (int)Math.Min(_image.Length, 1024 * 1024)));
    if (!sb.Valid) throw new InvalidDataException("Not a GFS1 image: superblock magic mismatch.");

    _inodesPerBlock = Gfs1Writer.BlockSize / Gfs1Writer.InodeSize;
    _inodeStart = (Gfs1Writer.SuperblockOffset / Gfs1Writer.BlockSize) + 1;
    Recurse(2, "");
  }

    /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<Gfs1Entry> Entries => _entries;

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(Gfs1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (entry.Size == 0 || entry.FirstBlock == 0) return [];
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"GFS1: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    // Block index times block size in ints wraps a couple of gigabytes in, and
    // the file was then read from the wrong offset — the bytes came back the
    // right length holding something else.
    var start = (long)entry.FirstBlock * Gfs1Writer.BlockSize;
    if (start + (long)entry.Size > _image.Length)
      throw new InvalidDataException("GFS1 extract: extent reaches past image end.");
    return _image.Read(start, (int)entry.Size);
  }

  /// <summary>Writes <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(Gfs1Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory || entry.Size == 0 || entry.FirstBlock == 0) return 0;
    var start = (long)entry.FirstBlock * Gfs1Writer.BlockSize;
    if (start + (long)entry.Size > _image.Length)
      throw new InvalidDataException("GFS1 extract: extent reaches past image end.");
    this._image.CopyTo(start, destination, entry.Size);
    return entry.Size;
  }

  private void Recurse(int inode, string prefix) {
    var info = ReadInode(inode);
    if (!info.IsDirectory || info.FirstBlock == 0) return;
    var off = (long)info.FirstBlock * Gfs1Writer.BlockSize;
    if (off >= _image.Length) return;
    var blk = _image.Read(off, (int)Math.Min(Gfs1Writer.BlockSize, _image.Length - off)).AsSpan();
    if (blk.Length < 4) return;
    var magic = BinaryPrimitives.ReadUInt16BigEndian(blk[..2]);
    if (magic != Gfs1Writer.DirBlockMagicConst) return;
    int slots = BinaryPrimitives.ReadUInt16BigEndian(blk[2..4]);
    var cur = 4;
    for (var i = 0; i < slots && cur + 5 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt32BigEndian(blk[cur..]);
      int nlen = blk[cur + 4];
      if (cur + 5 + nlen > blk.Length) break;
      var name = Encoding.UTF8.GetString(blk.Slice(cur + 5, nlen));
      cur += 5 + nlen;
      if (name is "." or "..") continue;
      var childInfo = ReadInode((int)childInode);
      var full = prefix.Length == 0 ? name : $"{prefix}/{name}";
      _entries.Add(new Gfs1Entry(full, (int)childInode, childInfo.IsDirectory, (long)childInfo.Size, childInfo.FirstBlock));
      if (childInfo.IsDirectory) Recurse((int)childInode, full);
    }
  }

  private InodeInfo ReadInode(int inode) {
    var blockOff = (inode - 2) / _inodesPerBlock;
    var slotOff = (inode - 2) % _inodesPerBlock;
    var ip = _image.Read(((long)_inodeStart + blockOff) * Gfs1Writer.BlockSize + slotOff * Gfs1Writer.InodeSize, Gfs1Writer.InodeSize).AsSpan();
    var mode = BinaryPrimitives.ReadUInt32BigEndian(ip[40..]);
    var size = BinaryPrimitives.ReadUInt64BigEndian(ip[56..]);
    var firstBlock = (int)BinaryPrimitives.ReadUInt64BigEndian(ip[24..]);
    return new InodeInfo((mode & 0xF000) == 0x4000, size, firstBlock);
  }

  private readonly record struct InodeInfo(bool IsDirectory, ulong Size, int FirstBlock);
}

/// <summary>One entry surfaced by <see cref="Gfs1Reader"/>.</summary>
public sealed record Gfs1Entry(string Name, int Inode, bool IsDirectory, long Size, int FirstBlock);
