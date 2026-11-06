namespace FileFormat.StuffIt;

/// <summary>
/// The folder tree both StuffIt writers lay out: '/'-separated paths become nested folders, in
/// the order the files and folders were added; folders named only by a file's path are created
/// before that file.
/// </summary>
internal sealed class StuffItTree<T> where T : class {
  public sealed class Node(string name, T? file) {
    public string Name { get; } = name;
    public T? File { get; } = file;
    public bool IsFolder => this.File == null;
    public DateTime Modified { get; set; }
    public List<Node> Children { get; } = [];
  }

  public Node Root { get; } = new("", null);

  private static string[] Split(string path) {
    var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0 || parts.Any(p => p is "." or ".."))
      throw new ArgumentException($"'{path}' is not a usable archive path.", nameof(path));
    return parts;
  }

  private Node Folder(IEnumerable<string> parts, DateTime modified) {
    var node = this.Root;
    foreach (var part in parts) {
      var next = node.Children.FirstOrDefault(c => c.Name == part);
      if (next == null) {
        next = new Node(part, null) { Modified = modified };
        node.Children.Add(next);
      } else if (!next.IsFolder) throw new ArgumentException($"'{part}' is both a file and a folder.");
      node = next;
    }
    return node;
  }

  /// <summary>Adds a (possibly empty) folder.</summary>
  public void AddFolder(string path, DateTime modified) => this.Folder(Split(path), modified).Modified = modified;

  /// <summary>Adds a file under its folders.</summary>
  public void AddFile(string path, T file, DateTime modified) {
    var parts = Split(path);
    var folder = this.Folder(parts[..^1], modified);
    if (folder.Children.Any(c => c.Name == parts[^1])) throw new ArgumentException($"'{path}' is added twice.");
    folder.Children.Add(new Node(parts[^1], file) { Modified = modified });
  }
}
