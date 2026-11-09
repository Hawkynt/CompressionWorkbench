using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Chfs;

/// <summary>One vnode of a CHFS image, after replaying its nodes.</summary>
/// <param name="Path">Slash-separated path from the root (vnode 2).</param>
/// <param name="Vno">Vnode number.</param>
/// <param name="Mode">Unix mode word.</param>
/// <param name="Uid">The node's uid field.</param>
/// <param name="Gid">The node's gid field.</param>
/// <param name="MTime">Modification time (UNIX seconds).</param>
/// <param name="Data">Contents (files), target (symlinks) or device number (device nodes).</param>
public sealed record ChfsEntry(string Path, ulong Vno, uint Mode, uint Uid, uint Gid, uint MTime, byte[] Data) {
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  public bool IsFile => (this.Mode & 0xF000) == 0x8000;
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
}

/// <summary>
/// Reads CHFS (NetBSD's Chip File System for raw NOR/NAND flash) images as NetBSD's
/// <c>makefs -t chfs</c> writes them: eraseblocks that start with an erase-counter header and
/// a NOR or NAND eraseblock header, then a log of vnode, data and directory-entry nodes,
/// every part CRC-32 protected. See <c>docs/CHFS-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// Converted from NetBSD's BSD-licensed CHFS sources (<c>sys/ufs/chfs/media.h</c>,
/// <c>ebh_media.h</c>); see <c>THIRD-PARTY-NOTICE.Chfs.txt</c>.
/// </remarks>
public sealed class ChfsReader {
  public const uint EraseBlockMagic = 0x53454452;   // "RDES"
  public const ushort NodeMagic = 0x4AF1;
  public const ushort TypeVnode = 1, TypeData = 2, TypeDirent = 3, TypePadding = 4;
  public const ulong RootVno = 2;
  private const int VnodeSize = 64, DataHeaderSize = 48, DirentHeaderSize = 52;

  /// <summary>Eraseblock size, from the distance between erase-counter headers.</summary>
  public int EraseBlockSize { get; }

  /// <summary>Page size: where each eraseblock's second header starts.</summary>
  public int PageSize { get; }

  /// <summary>True when the eraseblock headers are the 16-byte NAND form (with a serial).</summary>
  public bool IsNand { get; }

  /// <summary>Every vnode below the root, parents before children.</summary>
  public IReadOnlyList<ChfsEntry> Entries { get; }

  private readonly record struct Vnode(ulong Version, uint Uid, uint Gid, uint Mode, uint Size, uint MTime);
  private readonly record struct DataNode(ulong Version, ulong Offset, byte[] Data, int Order);
  private readonly record struct Dirent(ulong Vno, ulong Pvno, ulong Version, string Name);

  /// <summary>Parses the image; throws <see cref="InvalidDataException"/> for anything it will not replay.</summary>
  public ChfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.Length > int.MaxValue) throw new InvalidDataException("CHFS image too large to buffer.");
    var img = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(img);
    if (img.Length < 1024 || BinaryPrimitives.ReadUInt32LittleEndian(img) != EraseBlockMagic)
      throw new InvalidDataException("Not a CHFS image: no erase-counter header (\"RDES\") at offset 0.");

    this.EraseBlockSize = FindEraseBlockSize(img);
    (this.PageSize, this.IsNand) = FindPageSize(img, this.EraseBlockSize);

    var vnodes = new Dictionary<ulong, Vnode>();
    var data = new Dictionary<ulong, List<DataNode>>();
    var dirents = new List<Dirent>();
    var order = 0;
    for (var eb = 0; eb < img.Length; eb += this.EraseBlockSize) {
      if (img.AsSpan(eb, this.EraseBlockSize).IndexOfAnyExcept((byte)0xFF) < 0) continue;   // erased block
      this.CheckEraseBlockHeaders(img.AsSpan(eb, this.EraseBlockSize), eb);
      var pos = eb + 2 * this.PageSize;
      var end = eb + this.EraseBlockSize;
      while (pos + 12 <= end) {
        var h = img.AsSpan(pos);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(h);
        if (magic == 0xFFFF && img.AsSpan(pos, end - pos).IndexOfAnyExcept((byte)0xFF) < 0) break;   // rest of the block is erased
        if (magic != NodeMagic) throw new InvalidDataException($"CHFS: no node at {pos} (magic {magic:X4}).");
        var type = BinaryPrimitives.ReadUInt16LittleEndian(h[2..]);
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        if (Crc32.Compute(h[..8]) != BinaryPrimitives.ReadUInt32LittleEndian(h[8..]))
          throw new InvalidDataException($"CHFS node at {pos}: header CRC mismatch.");
        if (length < 12 || pos + (long)length > end) throw new InvalidDataException($"CHFS node at {pos}: length {length} leaves the eraseblock.");
        var node = img.AsSpan(pos, length);
        switch (type) {
          case TypeVnode: {
            if (length < VnodeSize) throw new InvalidDataException($"CHFS vnode at {pos} is short.");
            CheckCrc(node[..(VnodeSize - 4)], node[(VnodeSize - 4)..], pos);
            var vno = U64(node, 12);
            var v = new Vnode(U64(node, 20), U32(node, 28), U32(node, 32), U32(node, 36), U32(node, 40), U32(node, 48));
            if (!vnodes.TryGetValue(vno, out var old) || old.Version <= v.Version) vnodes[vno] = v;
            break;
          }
          case TypeData: {
            if (length < DataHeaderSize) throw new InvalidDataException($"CHFS data node at {pos} is short.");
            CheckCrc(node[..(DataHeaderSize - 4)], node[(DataHeaderSize - 4)..], pos);
            var dlen = (int)U32(node, 36);
            if (DataHeaderSize + (long)dlen > length) throw new InvalidDataException($"CHFS data node at {pos}: data overruns the node.");
            var payload = node.Slice(DataHeaderSize, dlen);
            if (Crc32.Compute(payload) != U32(node, 40)) throw new InvalidDataException($"CHFS data node at {pos}: data CRC mismatch.");
            var vno = U64(node, 12);
            if (!data.TryGetValue(vno, out var list)) data[vno] = list = [];
            list.Add(new DataNode(U64(node, 20), U64(node, 28), payload.ToArray(), order++));
            break;
          }
          case TypeDirent: {
            if (length < DirentHeaderSize) throw new InvalidDataException($"CHFS dirent at {pos} is short.");
            CheckCrc(node[..(DirentHeaderSize - 4)], node[(DirentHeaderSize - 4)..], pos);
            int nsize = node[40];
            if (DirentHeaderSize + nsize > length) throw new InvalidDataException($"CHFS dirent at {pos}: name overruns the node.");
            var name = node.Slice(DirentHeaderSize, nsize);
            if (Crc32.Compute(name) != U32(node, 44)) throw new InvalidDataException($"CHFS dirent at {pos}: name CRC mismatch.");
            dirents.Add(new Dirent(U64(node, 12), U64(node, 20), U64(node, 28), Encoding.UTF8.GetString(name)));
            break;
          }
          case TypePadding:
            break;
          default:
            throw new InvalidDataException($"CHFS node at {pos} has unknown type {type}.");
        }
        pos += (length + 3) & ~3;
      }
    }
    this.Entries = Build(vnodes, data, dirents);
  }

  private static List<ChfsEntry> Build(Dictionary<ulong, Vnode> vnodes, Dictionary<ulong, List<DataNode>> data, List<Dirent> dirents) {
    // The newest directory entry for a (parent, name) pair wins; a vnode number of zero
    // there would mean the name was removed, which only a mounted file system writes.
    var names = new Dictionary<(ulong, string), Dirent>();
    foreach (var d in dirents) {
      var key = (d.Pvno, d.Name);
      if (!names.TryGetValue(key, out var old) || old.Version <= d.Version) names[key] = d;
    }
    if (names.Values.Any(d => d.Vno == 0))
      throw new InvalidDataException("CHFS log removes names (dirent vnode 0); replaying a mounted file system's history is not supported.");

    var children = names.Values.Where(d => d.Vno != d.Pvno).GroupBy(d => d.Pvno)
      .ToDictionary(g => g.Key, g => g.OrderBy(d => d.Name, StringComparer.Ordinal).ToList());
    var result = new List<ChfsEntry>();
    var seen = new HashSet<ulong> { RootVno };
    void Walk(ulong dir, string prefix, int depth) {
      if (depth > 256) throw new InvalidDataException("CHFS directory nesting beyond 256 levels.");
      if (!children.TryGetValue(dir, out var list)) return;
      foreach (var d in list) {
        if (d.Name.Length == 0 || d.Name.Contains('/') || d.Name is "." or "..") throw new InvalidDataException($"CHFS entry with unusable name '{d.Name}'.");
        if (!vnodes.TryGetValue(d.Vno, out var v)) throw new InvalidDataException($"CHFS name '{d.Name}' points at vnode {d.Vno}, which has no vnode node.");
        var path = prefix.Length == 0 ? d.Name : prefix + "/" + d.Name;
        var isDir = (v.Mode & 0xF000) == 0x4000;
        if (isDir && !seen.Add(d.Vno)) throw new InvalidDataException($"CHFS directory vnode {d.Vno} appears twice.");
        result.Add(new ChfsEntry(path, d.Vno, v.Mode, v.Uid, v.Gid, v.MTime, isDir ? [] : Content(v, data.GetValueOrDefault(d.Vno))));
        if (isDir) Walk(d.Vno, path, depth + 1);
      }
    }
    Walk(RootVno, "", 0);
    return result;
  }

  /// <summary>Data nodes laid over each other, oldest first; the vnode's size cuts or zero-extends the result.</summary>
  private static byte[] Content(Vnode v, List<DataNode>? nodes) {
    var bytes = new byte[v.Size];
    if (nodes is null) return bytes;
    foreach (var n in nodes.OrderBy(n => n.Version).ThenBy(n => n.Order)) {
      if (n.Offset >= v.Size) continue;
      var count = (int)Math.Min((ulong)n.Data.Length, v.Size - n.Offset);
      n.Data.AsSpan(0, count).CopyTo(bytes.AsSpan((int)n.Offset));
    }
    return bytes;
  }

  private void CheckEraseBlockHeaders(ReadOnlySpan<byte> eb, int at) {
    if (BinaryPrimitives.ReadUInt32LittleEndian(eb) != EraseBlockMagic)
      throw new InvalidDataException($"CHFS eraseblock at {at} has no erase-counter header.");
    if (Crc32.Compute(eb.Slice(8, 4)) != BinaryPrimitives.ReadUInt32LittleEndian(eb[4..]))
      throw new InvalidDataException($"CHFS eraseblock at {at}: erase-counter CRC mismatch.");
    var hdr = eb[this.PageSize..];
    var len = this.IsNand ? 16 : 8;
    if (Crc32.Compute(hdr.Slice(4, len - 4)) != BinaryPrimitives.ReadUInt32LittleEndian(hdr))
      throw new InvalidDataException($"CHFS eraseblock at {at}: eraseblock-header CRC mismatch.");
  }

  private static int FindEraseBlockSize(byte[] img) {
    for (var size = 512; size < img.Length; size *= 2)
      if (img.Length % size == 0 && BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(size)) == EraseBlockMagic)
        return size;
    return img.Length;
  }

  /// <summary>The second header sits at the first page boundary after the erase-counter header; its CRC tells NOR (8 bytes) from NAND (16 bytes).</summary>
  private static (int PageSize, bool Nand) FindPageSize(byte[] img, int ebSize) {
    for (var p = 16; p * 2 < ebSize; p *= 2) {
      if (img.AsSpan(12, p - 12).IndexOfAnyExcept((byte)0xFF) >= 0) break;
      var hdr = img.AsSpan(p);
      if (hdr[..8].IndexOfAnyExcept((byte)0xFF) < 0) continue;
      var crc = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
      if (Crc32.Compute(hdr.Slice(4, 12)) == crc) return (p, true);
      if (Crc32.Compute(hdr.Slice(4, 4)) == crc) return (p, false);
    }
    throw new InvalidDataException("CHFS: cannot find the eraseblock header after the erase-counter header.");
  }

  private static void CheckCrc(ReadOnlySpan<byte> covered, ReadOnlySpan<byte> stored, int pos) {
    if (Crc32.Compute(covered) != BinaryPrimitives.ReadUInt32LittleEndian(stored))
      throw new InvalidDataException($"CHFS node at {pos}: node CRC mismatch.");
  }

  private static uint U32(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
  private static ulong U64(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadUInt64LittleEndian(b[at..]);
}
