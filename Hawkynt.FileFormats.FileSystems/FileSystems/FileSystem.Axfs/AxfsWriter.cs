using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;
using FileFormat.Zlib;

namespace FileSystem.Axfs;

/// <summary>
/// A file (<see cref="Data"/>), symbolic link (<see cref="LinkTarget"/>) or directory
/// (<see cref="Children"/>) to write into an AXFS image.
/// </summary>
/// <param name="Name">Entry name (UTF-8, no '/' or NUL); ignored for the root.</param>
/// <param name="Data">File contents.</param>
/// <param name="Children">Directory contents.</param>
/// <param name="LinkTarget">Symbolic-link target.</param>
public sealed record AxfsWriteNode(string Name, byte[]? Data = null, IReadOnlyList<AxfsWriteNode>? Children = null, string? LinkTarget = null) {
  /// <summary>Permission bits (<c>07777</c>); defaults to 0644 for files, 0755 for directories, 0777 for links.</summary>
  public int? Permissions { get; init; }

  /// <summary>Owner.</summary>
  public uint Uid { get; init; }

  /// <summary>Group.</summary>
  public uint Gid { get; init; }

  /// <summary>True for a directory.</summary>
  public bool IsDirectory => this.Children != null;
}

/// <summary>
/// Writes AXFS 2.2 images the AXFS kernel driver mounts, laid out as <c>mkfs.axfs</c> lays them
/// out (see <c>docs/AXFS-ON-DISK.md</c>, "Writing"):
/// <list type="bullet">
///   <item><description>the superblock, then the 18 region descriptors from offset 264, then the
///   byte tables packed back to back;</description></item>
///   <item><description>an empty XIP region at the next page boundary, then the byte-aligned
///   region, the compressed region and the strings, and the magic once more as the last four
///   bytes the superblock's size covers; the file is padded to whole pages.</description></item>
/// </list>
/// Inodes are numbered breadth-first from the root (inode 0), each directory's children
/// consecutive and sorted by name, and every file's pages take consecutive nodes in inode order.
/// A page that zlib shrinks is its own cblock; any other is stored byte-aligned. Each byte table
/// uses the fewest bytes per entry that hold its largest value.
/// </summary>
public static class AxfsWriter {
  private const int PageShift = 12;
  private const int PageSize = 1 << PageShift;
  private const int SuperblockLength = 264;
  private const int DescriptorLength = 40;
  private const int Regions = 18;
  private const ulong Compressed = 1, ByteAligned = 2;

  /// <summary>Writes <paramref name="root"/> as a complete image.</summary>
  /// <param name="output">Receives the image.</param>
  /// <param name="root">The root directory; the AXFS driver refuses to mount one with no entries.</param>
  /// <param name="timestamp">Build time in UNIX seconds (<c>mkfs.axfs</c> writes 0).</param>
  public static void Write(Stream output, AxfsWriteNode root, ulong timestamp = 0) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(root);
    if (!root.IsDirectory) throw new ArgumentException("The root must be a directory.", nameof(root));

    // Breadth-first inode order: each directory's children take consecutive numbers.
    var inodes = new List<AxfsWriteNode> { root };
    var names = new List<byte[]> { "./"u8.ToArray() };
    var firstChild = new Dictionary<int, int>();
    for (var i = 0; i < inodes.Count; ++i) {
      if (!inodes[i].IsDirectory) continue;
      var children = Sorted(inodes[i]);
      firstChild[i] = inodes.Count;
      foreach (var (name, child) in children) {
        inodes.Add(child);
        names.Add(name);
      }
    }

    var count = inodes.Count;
    var fileSize = new ulong[count];
    var numEntries = new ulong[count];
    var arrayIndex = new ulong[count];
    var nameOffset = new ulong[count];
    var modeIndex = new ulong[count];
    var modeTriples = new List<(ulong Mode, ulong Uid, ulong Gid)>();
    var strings = new MemoryStream();

    var nodeType = new List<ulong>();
    var nodeIndex = new List<ulong>();
    var cnodeOffset = new List<ulong>();
    var cnodeIndex = new List<ulong>();
    var cblockOffset = new List<ulong>();
    var banodeOffset = new List<ulong>();
    var byteAligned = new MemoryStream();
    var compressed = new MemoryStream();

    for (var i = 0; i < count; ++i) {
      var node = inodes[i];
      nameOffset[i] = (ulong)strings.Length;
      strings.Write(names[i]);
      strings.WriteByte(0);

      ulong type;
      if (node.IsDirectory) {
        type = 0x4000;
        numEntries[i] = (ulong)node.Children!.Count;
        arrayIndex[i] = node.Children.Count == 0 ? 0 : (ulong)firstChild[i];
        fileSize[i] = node.Children.Aggregate(0UL, (sum, c) => sum + (ulong)Encoding.UTF8.GetByteCount(c.Name) + 1);
      } else {
        var data = node.LinkTarget != null ? Encoding.UTF8.GetBytes(node.LinkTarget) : node.Data!;
        type = node.LinkTarget != null ? 0xA000UL : 0x8000UL;
        fileSize[i] = (ulong)data.Length;
        var pages = (data.Length + PageSize - 1) / PageSize;
        numEntries[i] = (ulong)pages;
        arrayIndex[i] = pages == 0 ? 0 : (ulong)nodeType.Count;
        for (var p = 0; p < pages; ++p) {
          var page = data.AsSpan(p * PageSize, Math.Min(PageSize, data.Length - p * PageSize));
          var zlib = ZlibRawHelper.Wrap(DeflateCompressor.Compress(page, DeflateCompressionLevel.Best), Adler32.Compute(page));
          if (zlib.Length < page.Length) {
            nodeType.Add(Compressed);
            nodeIndex.Add((ulong)cnodeIndex.Count);
            cnodeOffset.Add(0);
            cnodeIndex.Add((ulong)cblockOffset.Count);
            cblockOffset.Add((ulong)compressed.Length);
            compressed.Write(zlib);
          } else {
            nodeType.Add(ByteAligned);
            nodeIndex.Add((ulong)banodeOffset.Count);
            banodeOffset.Add((ulong)byteAligned.Length);
            byteAligned.Write(page);
          }
        }
      }
      var perms = (ulong)((node.Permissions ?? (node.IsDirectory ? 0x1ED : node.LinkTarget != null ? 0x1FF : 0x1A4)) & 0xFFF);
      var triple = (type | perms, (ulong)node.Uid, (ulong)node.Gid);
      var at = modeTriples.IndexOf(triple);
      if (at < 0) { at = modeTriples.Count; modeTriples.Add(triple); }
      modeIndex[i] = (ulong)at;
    }
    cblockOffset.Add((ulong)compressed.Length); // the end of the last cblock

    // Byte tables, in descriptor order from node_type on.
    ulong[][] tables = [
      [.. nodeType], [.. nodeIndex], [.. cnodeOffset], [.. cnodeIndex], [.. banodeOffset], [.. cblockOffset],
      fileSize, nameOffset, numEntries, modeIndex, arrayIndex,
      [.. modeTriples.Select(t => t.Mode)], [.. modeTriples.Select(t => t.Uid)], [.. modeTriples.Select(t => t.Gid)],
    ];

    var image = new MemoryStream();
    image.Write(new byte[SuperblockLength + Regions * DescriptorLength]);
    var descriptors = new (ulong Offset, ulong Size, ulong MaxIndex, byte Depth)[Regions];
    for (var t = 0; t < tables.Length; ++t) {
      var depth = Depth(tables[t]);
      descriptors[4 + t] = ((ulong)image.Length, (ulong)tables[t].Length * depth, (ulong)tables[t].Length, depth);
      var entry = new byte[8];
      foreach (var value in tables[t]) {
        BinaryPrimitives.WriteUInt64BigEndian(entry, value);
        image.Write(entry, 8 - depth, depth);
      }
    }
    Pad(image);
    descriptors[1] = ((ulong)image.Length, 0, 0, 0); // XIP: nothing is placed for execute-in-place
    descriptors[2] = ((ulong)image.Length, (ulong)byteAligned.Length, 0, 0);
    byteAligned.WriteTo(image);
    descriptors[3] = ((ulong)image.Length, (ulong)compressed.Length, 0, 0);
    compressed.WriteTo(image);
    descriptors[0] = ((ulong)image.Length, (ulong)strings.Length, 0, 0);
    strings.WriteTo(image);
    var magic = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(magic, AxfsReader.Magic);
    image.Write(magic); // checked by the driver as the volume's last four bytes
    var size = (ulong)image.Length;
    Pad(image);

    var buffer = image.GetBuffer().AsSpan(0, (int)image.Length);
    BinaryPrimitives.WriteUInt32BigEndian(buffer, AxfsReader.Magic);
    AxfsReader.Signature.CopyTo(buffer[4..]);
    BinaryPrimitives.WriteUInt32BigEndian(buffer[60..], PageSize);           // cblock_size: one page per cblock
    BinaryPrimitives.WriteUInt64BigEndian(buffer[64..], (ulong)count);       // files
    BinaryPrimitives.WriteUInt64BigEndian(buffer[72..], size);               // size
    BinaryPrimitives.WriteUInt64BigEndian(buffer[80..], (ulong)nodeType.Count); // blocks
    BinaryPrimitives.WriteUInt64BigEndian(buffer[88..], size);               // mmap_size
    for (var r = 0; r < Regions; ++r) {
      var at = SuperblockLength + r * DescriptorLength;
      BinaryPrimitives.WriteUInt64BigEndian(buffer[(96 + r * 8)..], (ulong)at);
      var d = buffer.Slice(at, DescriptorLength);
      BinaryPrimitives.WriteUInt64BigEndian(d, descriptors[r].Offset);
      BinaryPrimitives.WriteUInt64BigEndian(d[8..], descriptors[r].Size);
      BinaryPrimitives.WriteUInt64BigEndian(d[24..], descriptors[r].MaxIndex);
      d[32] = descriptors[r].Depth;
    }
    buffer[240] = 2; // version 2.2.0
    buffer[241] = 2;
    buffer[242] = 0;
    buffer[243] = 0; // zlib
    BinaryPrimitives.WriteUInt64BigEndian(buffer[248..], timestamp);
    buffer[256] = PageShift;
    output.Write(buffer);
  }

  private static List<(byte[] Name, AxfsWriteNode Node)> Sorted(AxfsWriteNode dir) {
    var list = new List<(byte[] Name, AxfsWriteNode Node)>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var child in dir.Children!) {
      ArgumentNullException.ThrowIfNull(child);
      if (child.Name.Length == 0 || child.Name is "." or ".." || child.Name.Contains('/') || child.Name.Contains('\0'))
        throw new ArgumentException($"AXFS: '{child.Name}' is not a usable name.", nameof(dir));
      if (!seen.Add(child.Name)) throw new ArgumentException($"AXFS: '{child.Name}' appears twice in one directory.", nameof(dir));
      var kinds = (child.IsDirectory ? 1 : 0) + (child.Data != null ? 1 : 0) + (child.LinkTarget != null ? 1 : 0);
      if (kinds != 1) throw new ArgumentException($"AXFS: '{child.Name}' must be exactly one of file, directory or link.", nameof(dir));
      list.Add((Encoding.UTF8.GetBytes(child.Name), child));
    }
    list.Sort((a, b) => a.Name.AsSpan().SequenceCompareTo(b.Name));
    return list;
  }

  private static byte Depth(ulong[] table) {
    var max = table.Length == 0 ? 0 : table.Max();
    byte depth = 1;
    while (depth < 8 && max >> (depth * 8) != 0) ++depth;
    return depth;
  }

  private static void Pad(MemoryStream image) {
    var rest = (int)(image.Length % PageSize);
    if (rest != 0) image.Write(new byte[PageSize - rest]);
  }
}
