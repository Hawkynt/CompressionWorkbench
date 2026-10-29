using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jffs1;

/// <summary>One inode of a JFFS (version 1) image, after replaying its nodes.</summary>
/// <param name="Path">Slash-separated path from the root (inode 1).</param>
/// <param name="Ino">Inode number.</param>
/// <param name="Mode">Unix mode word.</param>
/// <param name="Uid">Owner.</param>
/// <param name="Gid">Group.</param>
/// <param name="MTime">Modification time (UNIX seconds).</param>
/// <param name="Data">File contents, symlink target, or (device nodes) the raw device number.</param>
public sealed record Jffs1Entry(string Path, uint Ino, uint Mode, ushort Uid, ushort Gid, uint MTime, byte[] Data) {
  public bool IsDirectory => (this.Mode & 0xF000) == 0x4000;
  public bool IsFile => (this.Mode & 0xF000) == 0x8000;
  public bool IsSymlink => (this.Mode & 0xF000) == 0xA000;
}

/// <summary>
/// Reads JFFS version 1 (Axis Communications' Journalling Flash File System, Linux 2.4)
/// images: a log of 60-byte raw inodes, each followed by its name and data, every part
/// padded to four bytes. Either byte order is accepted ("1984" magic read either way).
/// See <c>docs/JFFS1-ON-DISK.md</c>.
/// </summary>
/// <remarks>
/// Proven on images <c>mkfs.jffs</c> writes. Logs a running file system produced — deleted
/// or renamed nodes (the flag bits), or nodes that replace bytes of an earlier version — are
/// refused rather than replayed, because no reference implementation is left to check such a
/// replay against.
/// </remarks>
public sealed class Jffs1Reader {
  public const uint Magic = 0x34383931;   // "1984" in the image's byte order
  public const int HeaderSize = 60;
  public const uint RootIno = 1;

  private readonly record struct Node(uint Ino, uint Pino, uint Version, uint Mode, ushort Uid, ushort Gid, uint MTime,
    uint Offset, uint DSize, uint RSize, string Name, byte[] Data);

  /// <summary>True when the image is big-endian.</summary>
  public bool BigEndian { get; }

  /// <summary>Every inode below the root, parents before children.</summary>
  public IReadOnlyList<Jffs1Entry> Entries { get; }

  /// <summary>Raw nodes skipped because their header checksum failed or they were marked obsolete.</summary>
  public int SkippedNodes { get; }

  /// <summary>Parses the whole log; throws <see cref="InvalidDataException"/> for anything it will not replay.</summary>
  public Jffs1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.Length > int.MaxValue) throw new InvalidDataException("JFFS image too large to buffer.");
    var img = new byte[stream.Length];
    stream.Position = 0;
    stream.ReadExactly(img);
    if (img.Length < HeaderSize) throw new InvalidDataException("Image too short for a JFFS node.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(img) == Magic) this.BigEndian = false;
    else if (BinaryPrimitives.ReadUInt32BigEndian(img) == Magic) this.BigEndian = true;
    else throw new InvalidDataException("Not a JFFS (v1) image: no \"1984\" magic at offset 0.");

    var nodes = new List<Node>();
    var skipped = 0;
    var pos = 0;
    while (pos + HeaderSize <= img.Length) {
      var h = img.AsSpan(pos, HeaderSize);
      if (this.U32(h, 0) != Magic) { pos += 4; continue; }      // erased (0xFF) or dirty space
      if (!HeaderChecksumOk(h, this)) { ++skipped; pos += 4; continue; }
      int nsize = h[48];
      var flags = h[50];
      var accurate = h[51];
      var dsize = this.U32(h, 40);
      var total = HeaderSize + Pad(nsize) + Pad(dsize);
      if (dsize > int.MaxValue || pos + (long)total > img.Length)
        throw new InvalidDataException($"JFFS node at {pos} runs past the end of the image.");
      var name = img.AsSpan(pos + HeaderSize, nsize);
      var data = img.AsSpan(pos + HeaderSize + Pad(nsize), (int)dsize);
      if (accurate == 0) { ++skipped; pos += total; continue; }    // obsolete node
      if (flags != 0)
        throw new InvalidDataException($"JFFS node at {pos} is marked deleted or renamed; replaying a running file system's log is not supported.");
      if (nsize > 0 && Sum16(name) != this.U16(h, 56)) throw new InvalidDataException($"JFFS node at {pos}: name checksum mismatch.");
      if (Sum32(data) != this.U32(h, 52)) throw new InvalidDataException($"JFFS node at {pos}: data checksum mismatch.");
      nodes.Add(new Node(this.U32(h, 4), this.U32(h, 8), this.U32(h, 12), this.U32(h, 16), this.U16(h, 20), this.U16(h, 22),
        this.U32(h, 28), this.U32(h, 36), dsize, this.U32(h, 44), Encoding.Latin1.GetString(name), data.ToArray()));
      pos += total;
    }
    this.SkippedNodes = skipped;
    this.Entries = Build(nodes);
  }

  private static List<Jffs1Entry> Build(List<Node> nodes) {
    var inodes = new Dictionary<uint, (Node Meta, string Name, uint Pino, byte[] Data)>();
    foreach (var group in nodes.GroupBy(n => n.Ino)) {
      var ordered = group.OrderBy(n => n.Version).ToList();
      if (ordered.Select(n => n.Version).Distinct().Count() != ordered.Count)
        throw new InvalidDataException($"JFFS inode {group.Key} has two nodes with one version.");
      var data = new MemoryStream();
      foreach (var n in ordered) {
        if (n.RSize != 0 || n.Offset != data.Length)
          throw new InvalidDataException($"JFFS inode {n.Ino}: node version {n.Version} rewrites bytes at {n.Offset} (rsize {n.RSize}); only appending logs are replayed.");
        data.Write(n.Data);
      }
      var meta = ordered[^1];
      var name = ordered.LastOrDefault(n => n.Name.Length > 0).Name ?? "";
      inodes[group.Key] = (meta, name, meta.Pino, data.ToArray());
    }
    if (!inodes.TryGetValue(RootIno, out var root) || (root.Meta.Mode & 0xF000) != 0x4000)
      throw new InvalidDataException("JFFS root inode 1 is missing or not a directory.");

    var children = inodes.Where(kv => kv.Key != RootIno).GroupBy(kv => kv.Value.Pino).ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Value.Name, StringComparer.Ordinal).ToList());
    var result = new List<Jffs1Entry>();
    var visited = new HashSet<uint> { RootIno };
    void Walk(uint dir, string prefix, int depth) {
      if (depth > 256) throw new InvalidDataException("JFFS directory nesting beyond 256 levels.");
      if (!children.TryGetValue(dir, out var list)) return;
      foreach (var (ino, v) in list) {
        if (!visited.Add(ino)) throw new InvalidDataException($"JFFS inode {ino} appears twice in the tree.");
        if (v.Name.Length == 0 || v.Name.Contains('/') || v.Name is "." or "..") throw new InvalidDataException($"JFFS inode {ino} has an unusable name '{v.Name}'.");
        var path = prefix.Length == 0 ? v.Name : prefix + "/" + v.Name;
        var e = new Jffs1Entry(path, ino, v.Meta.Mode, v.Meta.Uid, v.Meta.Gid, v.Meta.MTime, v.Data);
        result.Add(e);
        if (e.IsDirectory) Walk(ino, path, depth + 1);
      }
    }
    Walk(RootIno, "", 0);
    if (visited.Count != inodes.Count)
      throw new InvalidDataException($"JFFS image holds {inodes.Count - visited.Count} inode(s) not reachable from the root.");
    return result;
  }

  /// <summary>16-bit byte sum of the header with <c>accurate</c> and <c>chksum</c> taken as zero, as mkfs.jffs computes it.</summary>
  private static bool HeaderChecksumOk(ReadOnlySpan<byte> h, Jffs1Reader r) {
    Span<byte> copy = stackalloc byte[HeaderSize];
    h.CopyTo(copy);
    copy[51] = 0;
    copy[58] = 0; copy[59] = 0;
    return Sum16(copy) == r.U16(h, 58);
  }

  private static ushort Sum16(ReadOnlySpan<byte> d) {
    uint s = 0;
    foreach (var b in d) s += b;
    return (ushort)s;
  }

  private static uint Sum32(ReadOnlySpan<byte> d) {
    uint s = 0;
    foreach (var b in d) s += b;
    return s;
  }

  private static int Pad(long n) => (int)((n + 3) & ~3L);
  private uint U32(ReadOnlySpan<byte> b, int at) => this.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[at..]);
  private ushort U16(ReadOnlySpan<byte> b, int at) => this.BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(b[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[at..]);
}
