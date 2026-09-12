using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Wim;

/// <summary>
/// Builds the metadata resource of a WIM image: the security-descriptor block
/// followed by the directory tree that gives the image its file names.
/// </summary>
/// <remarks>
/// <para>A WIM keeps file <em>contents</em> in the lookup table, addressed by the
/// SHA-1 of the uncompressed bytes, and keeps file <em>names</em> here. Neither
/// half is optional: without this resource an image has resources nobody can name
/// and no directory to list, which is what a reader means when it says a WIM
/// holds nothing.</para>
///
/// <para>Directory entries are laid out one level at a time. All the entries of a
/// level sit next to each other and are closed by an eight-byte zero length; the
/// children of each of them follow, in order, each level closed the same way. An
/// entry names its children by the absolute offset of their level within the
/// resource, which is why the offsets are patched in afterwards — a level's
/// position is not known until everything ahead of it has been written.</para>
/// </remarks>
internal static class WimImageMetadata {

  /// <summary>
  /// One node of the tree being written: a directory with children, or a file
  /// with the hash of its content.
  /// </summary>
  internal sealed class Node {
    /// <summary>The leaf name, as it appears in the image.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this node is a directory.</summary>
    public bool IsDirectory => this.Children is not null;

    /// <summary>The children of a directory, or null for a file.</summary>
    public List<Node>? Children { get; init; }

    /// <summary>
    /// SHA-1 of the file's uncompressed content. Left all-zero for directories
    /// and for empty files, which have no resource to point at.
    /// </summary>
    public byte[] Hash { get; init; } = new byte[WimConstants.HashLength];

    /// <summary>Where this entry's fixed part was written, for patching.</summary>
    public int WrittenAt { get; set; }

    /// <summary>Where this directory's level of children was written.</summary>
    public int ChildrenAt { get; set; }
  }

  /// <summary>
  /// Creates the root of a tree from paths, splitting on either separator and
  /// making the directories each path implies.
  /// </summary>
  /// <param name="files">Leaf paths with the hash of each file's content.</param>
  /// <returns>The root node, whose name is empty as the format requires.</returns>
  public static Node BuildTree(IEnumerable<(string Path, byte[] Hash)> files) {
    ArgumentNullException.ThrowIfNull(files);

    var root = new Node { Name = string.Empty, Children = [] };

    foreach (var (path, hash) in files) {
      var parts = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length == 0)
        continue;

      var directory = root;
      for (var i = 0; i < parts.Length - 1; ++i)
        directory = FindOrAddDirectory(directory, parts[i]);

      var leaf = parts[^1];
      if (directory.Children!.Any(c => string.Equals(c.Name, leaf, StringComparison.OrdinalIgnoreCase)))
        continue;                       // the same name twice is one file, not two

      directory.Children!.Add(new Node { Name = leaf, Hash = hash });
    }

    return root;
  }

  private static Node FindOrAddDirectory(Node parent, string name) {
    foreach (var child in parent.Children!)
      if (child.IsDirectory && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
        return child;

    var created = new Node { Name = name, Children = [] };
    parent.Children!.Add(created);
    return created;
  }

  /// <summary>Counts the directories below <paramref name="root"/>, root excluded.</summary>
  public static int CountDirectories(Node root) {
    ArgumentNullException.ThrowIfNull(root);
    var total = 0;
    foreach (var child in root.Children ?? [])
      if (child.IsDirectory)
        total += 1 + CountDirectories(child);
    return total;
  }

  /// <summary>Counts the files anywhere below <paramref name="root"/>.</summary>
  public static int CountFiles(Node root) {
    ArgumentNullException.ThrowIfNull(root);
    var total = 0;
    foreach (var child in root.Children ?? [])
      total += child.IsDirectory ? CountFiles(child) : 1;
    return total;
  }

  /// <summary>
  /// Serialises the metadata resource for the tree rooted at
  /// <paramref name="root"/>.
  /// </summary>
  public static byte[] Serialize(Node root) {
    ArgumentNullException.ThrowIfNull(root);

    var buffer = new List<byte>(1024);
    AppendSecurityData(buffer);
    WriteLevel(buffer, [root]);

    var bytes = buffer.ToArray();
    PatchSubdirectoryOffsets(bytes, [root]);
    return bytes;
  }

  /// <summary>
  /// Writes the security-descriptor block of an image that carries none: a total
  /// length covering the two fields themselves, and no entries.
  /// </summary>
  private static void AppendSecurityData(List<byte> buffer) {
    Span<byte> header = stackalloc byte[WimConstants.EmptySecurityDataSize];
    BinaryPrimitives.WriteUInt32LittleEndian(header, WimConstants.EmptySecurityDataSize);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0);
    buffer.AddRange(header);
  }

  /// <summary>
  /// Writes one level of the tree, its terminator, and then the levels its
  /// directories hold, recording where each entry and each level landed.
  /// </summary>
  /// <remarks>
  /// A directory gets a level of its own even when it is empty — the level is
  /// then just the terminator, and the directory points at it. Pointing at
  /// nothing instead would be a plainer way to say "no children", but it is not
  /// the way the format says it, and a reader looking for the terminator would
  /// find the security block at the front of the resource.
  /// </remarks>
  private static void WriteLevel(List<byte> buffer, List<Node> level) {
    foreach (var node in level) {
      node.WrittenAt = buffer.Count;
      AppendEntry(buffer, node);
    }

    buffer.AddRange(new byte[8]);                       // end of this level

    foreach (var node in level) {
      if (!node.IsDirectory)
        continue;

      node.ChildrenAt = buffer.Count;
      WriteLevel(buffer, node.Children!);
    }
  }

  /// <summary>
  /// Fills in each directory's pointer to its children, now that every level has
  /// a position.
  /// </summary>
  private static void PatchSubdirectoryOffsets(byte[] bytes, List<Node> level) {
    foreach (var node in level) {
      if (!node.IsDirectory)
        continue;

      BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(node.WrittenAt + 16), (ulong)node.ChildrenAt);
      PatchSubdirectoryOffsets(bytes, node.Children!);
    }
  }

  /// <summary>
  /// Appends one directory entry. The length covers the fixed part, the name and
  /// its terminator, rounded up so the entry after it starts eight-byte aligned;
  /// the padding is part of the entry rather than a gap between entries.
  /// </summary>
  private static void AppendEntry(List<byte> buffer, Node node) {
    var nameBytes = Encoding.Unicode.GetBytes(node.Name);
    var length = Align8(WimConstants.DirEntryFixedSize + nameBytes.Length + 2);

    var entry = new byte[length];
    var span = entry.AsSpan();

    BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)length);
    BinaryPrimitives.WriteUInt32LittleEndian(span[8..],
      node.IsDirectory ? WimConstants.AttributeDirectory : WimConstants.AttributeArchive);
    BinaryPrimitives.WriteInt32LittleEndian(span[12..], WimConstants.NoSecurityDescriptor);

    // +16 subdirectory offset — patched once the children have a position.
    // +24, +32 unused; +40, +48, +56 creation, access and write times. Nothing
    // upstream carries a timestamp, and inventing one would make the same input
    // produce a different image every time it is written.

    node.Hash.CopyTo(span[64..]);

    // +84 reparse tag, +88 hard-link group, +96 extra stream count, +98 short
    // name length: an image of plain files in plain directories has none of them.
    BinaryPrimitives.WriteUInt16LittleEndian(span[100..], (ushort)nameBytes.Length);
    nameBytes.CopyTo(span[WimConstants.DirEntryFixedSize..]);

    buffer.AddRange(entry);
  }

  private static int Align8(int value) => (value + 7) & ~7;
}
