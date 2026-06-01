#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Efs;

/// <summary>
/// Read-side companion to <see cref="EfsWriter"/>. Walks the on-disk
/// superblock + inode table + directory blocks emitted by our writer and
/// yields the file tree as a flat list of <see cref="EfsEntry"/>.
/// </summary>
public sealed class EfsReader {
  private readonly byte[] _image;
  private readonly List<EfsEntry> _entries = [];

  /// <summary>
  /// Parses <paramref name="stream"/> as an EFS image and surfaces every
  /// file / directory at its full path. Throws on malformed superblock.
  /// </summary>
  public EfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _image = ms.ToArray();

    var sb = EfsSuperblock.TryParse(_image);
    if (!sb.Valid) throw new InvalidDataException("Not an EFS image: superblock magic mismatch.");

    // Walk from inode 2 (root). For each directory inode we read its first
    // extent block and decode the variable-length dirent stream.
    Recurse(2, "");
  }

  /// <summary>All non-root entries (files + intermediate directories).</summary>
  public IReadOnlyList<EfsEntry> Entries => _entries;

  /// <summary>Extracts a file entry's bytes by reading its first extent.</summary>
  public byte[] Extract(EfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var len = entry.Size;
    if (len == 0 || entry.FirstBlock == 0) return [];
    var start = entry.FirstBlock * EfsWriter.BasicBlock;
    if (start + len > _image.Length)
      throw new InvalidDataException("EFS extract: extent reaches past image end.");
    return _image.AsSpan(start, len).ToArray();
  }

  private void Recurse(int inode, string prefix) {
    var info = ReadInode(inode);
    if (!info.IsDirectory) return;
    if (info.NumExtents == 0 || info.FirstBlock == 0) return;
    var off = info.FirstBlock * EfsWriter.BasicBlock;
    if (off >= _image.Length) return;
    var blk = _image.AsSpan(off, Math.Min(EfsWriter.BasicBlock, _image.Length - off));
    if (blk.Length < 3) return;
    var magic = BinaryPrimitives.ReadUInt16BigEndian(blk[..2]);
    if (magic != 0xBEEF) return; // not our writer's directory shape
    int slots = blk[2];
    var cur = 3;
    for (var i = 0; i < slots && cur + 3 <= blk.Length; i++) {
      var childInode = BinaryPrimitives.ReadUInt16BigEndian(blk[cur..]);
      int nlen = blk[cur + 2];
      if (cur + 3 + nlen > blk.Length) break;
      var name = Encoding.UTF8.GetString(blk.Slice(cur + 3, nlen));
      cur += 3 + nlen;
      if (name is "." or "..") continue;
      var childInfo = ReadInode(childInode);
      var full = prefix.Length == 0 ? name : $"{prefix}/{name}";
      _entries.Add(new EfsEntry(full, childInode, childInfo.IsDirectory, childInfo.Size, childInfo.FirstBlock));
      if (childInfo.IsDirectory) Recurse(childInode, full);
    }
  }

  private InodeInfo ReadInode(int inode) {
    var blockOff = (inode - 2) / EfsWriter.InodesPerBlock;
    var slotOff = (inode - 2) % EfsWriter.InodesPerBlock;
    var ip = _image.AsSpan((EfsWriter.InodeTableOffset + blockOff) * EfsWriter.BasicBlock + slotOff * EfsWriter.InodeSize, EfsWriter.InodeSize);
    var mode = BinaryPrimitives.ReadUInt16BigEndian(ip[..2]);
    var size = BinaryPrimitives.ReadInt32BigEndian(ip[8..]);
    var numExtents = BinaryPrimitives.ReadInt16BigEndian(ip[28..]);
    var firstBlock = 0;
    if (numExtents > 0) {
      // ex_bn at offset 33..35 (3 bytes BE inside the 8-byte extent).
      var ex = ip[32..];
      firstBlock = (ex[1] << 16) | (ex[2] << 8) | ex[3];
    }
    return new InodeInfo((mode & 0xF000) == 0x4000, size, numExtents, firstBlock);
  }

  private readonly record struct InodeInfo(bool IsDirectory, int Size, int NumExtents, int FirstBlock);
}

/// <summary>One entry surfaced by <see cref="EfsReader"/>.</summary>
public sealed record EfsEntry(string Name, int Inode, bool IsDirectory, int Size, int FirstBlock);
