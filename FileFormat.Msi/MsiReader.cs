#pragma warning disable CS1591
namespace FileFormat.Msi;

/// <summary>
/// Reads MSI (Windows Installer) and OLE Compound File Binary Format files.
/// Exposes all streams and storages as entries. Streams can be extracted directly.
/// </summary>
public sealed class MsiReader : IDisposable {
  private readonly CfbReader _cfb;
  private readonly List<MsiEntry> _entries = [];
  private bool _disposed;

  public IReadOnlyList<MsiEntry> Entries => _entries;

  public MsiReader(Stream stream, bool leaveOpen = false) {
    _cfb = new CfbReader(stream);
    BuildEntries();
  }

  private void BuildEntries() {
    // Walk directory entries, building full paths via parent tracking
    var cfbEntries = _cfb.Entries;
    if (cfbEntries.Count == 0) return;

    // Build parent map from tree structure
    var parentPath = new Dictionary<int, string>();
    parentPath[0] = ""; // root

    // Collect children for each storage
    var childrenOf = new Dictionary<int, List<int>>();
    foreach (var e in cfbEntries)
      childrenOf[e.Index] = [];

    // Walk the tree: root's children come from ChildDid, siblings from left/right
    foreach (var e in cfbEntries) {
      if (e.EntryType is CfbEntryType.Storage or CfbEntryType.RootStorage) {
        if (e.ChildDid != 0xFFFFFFFF) {
          var children = new List<int>();
          CollectSiblings(cfbEntries, (int)e.ChildDid, children);
          childrenOf[e.Index] = children;
        }
      }
    }

    // BFS to assign paths
    var queue = new Queue<(int entryIndex, string parentDir)>();
    // Start with root's children
    foreach (var childIdx in childrenOf[0])
      queue.Enqueue((childIdx, ""));

    while (queue.Count > 0) {
      var (idx, parent) = queue.Dequeue();
      var cfbEntry = FindEntry(cfbEntries, idx);
      if (cfbEntry == null) continue;

      var path = string.IsNullOrEmpty(parent) ? cfbEntry.Name : parent + "/" + cfbEntry.Name;

      _entries.Add(new MsiEntry {
        Name = cfbEntry.Name,
        FullPath = path,
        IsDirectory = cfbEntry.EntryType == CfbEntryType.Storage,
        Size = cfbEntry.EntryType == CfbEntryType.Stream ? cfbEntry.StreamSize : 0,
        DirectoryIndex = cfbEntry.Index,
      });

      if (cfbEntry.EntryType == CfbEntryType.Storage && childrenOf.TryGetValue(idx, out var kids)) {
        foreach (var kid in kids)
          queue.Enqueue((kid, path));
      }
    }
  }

  private static void CollectSiblings(IReadOnlyList<CfbDirectoryEntry> entries, int startIdx, List<int> result) {
    // In-order traversal of red-black tree
    if (startIdx < 0 || startIdx >= entries.Count) return;
    var entry = FindEntry(entries, startIdx);
    if (entry == null) return;

    if (entry.LeftSiblingDid != 0xFFFFFFFF)
      CollectSiblings(entries, (int)entry.LeftSiblingDid, result);

    result.Add(startIdx);

    if (entry.RightSiblingDid != 0xFFFFFFFF)
      CollectSiblings(entries, (int)entry.RightSiblingDid, result);
  }

  private static CfbDirectoryEntry? FindEntry(IReadOnlyList<CfbDirectoryEntry> entries, int index) {
    foreach (var e in entries)
      if (e.Index == index) return e;
    return null;
  }

  public byte[] Extract(MsiEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(_disposed, this);

    if (entry.IsDirectory) return [];

    var cfbEntry = FindEntry(_cfb.Entries, entry.DirectoryIndex);
    if (cfbEntry == null)
      throw new InvalidDataException($"MSI: directory entry {entry.DirectoryIndex} not found.");

    return _cfb.ExtractStream(cfbEntry);
  }

  public void Dispose() {
    _disposed = true;
  }
}
