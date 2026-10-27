using System.Buffers.Binary;

namespace FileSystem.Vdfs;

/// <summary>
/// The directory tree of a VDF volume and its serialisation into catalog entries.
/// </summary>
/// <remarks>
/// <para>Laid out the way GothicVDFS and VDFSTool lay it out: a directory's entries are
/// contiguous — subdirectories first, then files, each group sorted by name — and the last
/// carries <see cref="VdfsLayout.TypeLast" />. Each subdirectory's own block follows, depth
/// first, and its entry points at that block's first index.</para>
///
/// <para>An empty directory cannot be written: its offset would name the first entry of
/// some other block, and a reader would walk into it looking for a last entry. Empty
/// directories are therefore left out, as the reference tools leave them out.</para>
/// </remarks>
internal sealed class VdfsCatalog {

  /// <summary>One directory or file.</summary>
  internal sealed class Node {
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public List<Node> Children { get; } = [];
    public long Offset { get; set; }
    public long Size { get; set; }
    public uint Attributes { get; set; } = VdfsLayout.FileAttributes;
    public object? Tag { get; set; }
  }

  public Node Root { get; } = new() { IsDirectory = true };

  private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

  /// <summary>Splits a path on either separator, dropping empty and <c>.</c> components.</summary>
  public static string[] Split(string path)
    => [.. path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(p => p != ".")];

  /// <summary>
  /// Returns the file node for <paramref name="path" />, creating it and its parent
  /// directories as needed. An existing file of that name is reused.
  /// </summary>
  /// <exception cref="ArgumentException">The path is empty, a component is too long, or it names a directory.</exception>
  public Node GetOrAddFile(string path) {
    var parts = Split(path);
    if (parts.Length == 0) throw new ArgumentException("VDFS: an entry needs a name.", nameof(path));
    foreach (var part in parts) CheckName(part);

    var directory = this.Root;
    for (var i = 0; i < parts.Length - 1; ++i)
      directory = Child(directory, parts[i], wantDirectory: true, path);

    return Child(directory, parts[^1], wantDirectory: false, path);
  }

  private static Node Child(Node parent, string name, bool wantDirectory, string path) {
    var existing = parent.Children.Find(c => NameComparer.Equals(c.Name, name));
    if (existing != null) {
      if (existing.IsDirectory != wantDirectory)
        throw new ArgumentException($"VDFS: '{path}' needs '{name}' to be a {(wantDirectory ? "directory" : "file")}.", nameof(path));
      return existing;
    }

    var node = new Node { Name = name, IsDirectory = wantDirectory, Attributes = wantDirectory ? 0 : VdfsLayout.FileAttributes };
    parent.Children.Add(node);
    return node;
  }

  /// <summary>Throws unless <paramref name="name" /> fits a 64-byte name field.</summary>
  public static void CheckName(string name) {
    if (VdfsLayout.NameEncoding.GetByteCount(name) > VdfsLayout.NameSize)
      throw new ArgumentException($"VDFS: '{name}' is longer than the {VdfsLayout.NameSize}-byte name field.", nameof(name));
  }

  /// <summary>Finds the node at <paramref name="path" />, or null.</summary>
  public Node? Find(string path) {
    var node = this.Root;
    foreach (var part in Split(path)) {
      node = node.Children.Find(c => NameComparer.Equals(c.Name, part));
      if (node == null) return null;
    }
    return ReferenceEquals(node, this.Root) ? null : node;
  }

  /// <summary>Removes the node at <paramref name="path" /> — a whole directory if it is one — and returns the files it held.</summary>
  public List<Node> Remove(string path) {
    var parts = Split(path);
    if (parts.Length == 0) return [];

    var trail = new List<Node> { this.Root };
    foreach (var part in parts) {
      var next = trail[^1].Children.Find(c => NameComparer.Equals(c.Name, part));
      if (next == null) return [];
      trail.Add(next);
    }

    var target = trail[^1];
    trail[^2].Children.Remove(target);
    var removed = new List<Node>();
    Collect(target, removed);
    return removed;
  }

  private static void Collect(Node node, List<Node> files) {
    if (!node.IsDirectory) { files.Add(node); return; }
    foreach (var child in node.Children) Collect(child, files);
  }

  /// <summary>Every file, in the order the reference tools place payloads: deepest directories first.</summary>
  public IEnumerable<Node> FilesInDataOrder() => DataOrder(this.Root);

  private static IEnumerable<Node> DataOrder(Node directory) {
    foreach (var child in Sorted(directory).Where(c => c.IsDirectory))
      foreach (var file in DataOrder(child))
        yield return file;
    foreach (var child in Sorted(directory).Where(c => !c.IsDirectory))
      yield return child;
  }

  /// <summary>Every file with its full path.</summary>
  public IEnumerable<(string Path, Node File)> Files() => Files(this.Root, "");

  private static IEnumerable<(string, Node)> Files(Node directory, string prefix) {
    foreach (var child in directory.Children) {
      var path = prefix + child.Name;
      if (child.IsDirectory) {
        foreach (var inner in Files(child, path + "/")) yield return inner;
      } else {
        yield return (path, child);
      }
    }
  }

  /// <summary>Children in catalog order, with empty directories left out.</summary>
  private static IEnumerable<Node> Sorted(Node directory)
    => directory.Children
      .Where(c => !c.IsDirectory || HasFiles(c))
      .OrderBy(c => c.IsDirectory ? 0 : 1)
      .ThenBy(c => c.Name.ToUpperInvariant(), StringComparer.Ordinal);

  private static bool HasFiles(Node directory)
    => directory.Children.Any(c => !c.IsDirectory || HasFiles(c));

  /// <summary>Number of catalog entries <see cref="Serialize" /> will produce.</summary>
  public int EntryCount => Count(this.Root);

  private static int Count(Node directory)
    => Sorted(directory).Sum(c => 1 + (c.IsDirectory ? Count(c) : 0));

  /// <summary>Number of files.</summary>
  public int FileCount => this.Files().Count();

  /// <summary>Serialises the tree, taking each file's offset and size from its node.</summary>
  public byte[] Serialize() {
    var entries = new List<byte[]>();
    Emit(this.Root, entries);
    var catalog = new byte[entries.Count * VdfsLayout.EntrySize];
    for (var i = 0; i < entries.Count; ++i)
      entries[i].CopyTo(catalog, i * VdfsLayout.EntrySize);
    return catalog;
  }

  private static void Emit(Node directory, List<byte[]> entries) {
    var children = Sorted(directory).ToList();
    var pending = new List<(byte[] Record, Node Directory)>();
    for (var i = 0; i < children.Count; ++i) {
      var child = children[i];
      var last = i == children.Count - 1 ? VdfsLayout.TypeLast : 0;
      var record = new byte[VdfsLayout.EntrySize];
      record.AsSpan(0, VdfsLayout.NameSize).Fill(0x20);
      VdfsLayout.NameEncoding.GetBytes(child.Name).CopyTo(record, 0);
      if (child.IsDirectory) {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryTypeField), VdfsLayout.TypeDirectory | last);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryAttributesField), child.Attributes);
        pending.Add((record, child));
      } else {
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryOffsetField), checked((uint)child.Offset));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntrySizeField), checked((uint)child.Size));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryTypeField), last);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryAttributesField), child.Attributes);
      }
      entries.Add(record);
    }

    foreach (var (record, child) in pending) {
      BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(VdfsLayout.EntryOffsetField), (uint)entries.Count);
      Emit(child, entries);
    }
  }
}
