#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Os9Rbf;

/// <summary>
/// Writer for Microware OS-9 RBF (Random-Block-File) disk images. Emits a
/// 35-track DSDD CoCo geometry (322 560 bytes / ~315 KB, 1260 sectors of 256
/// bytes, cluster size 1). Files whose names contain '/' separators are placed
/// into real OS-9 subdirectories: each path component becomes a directory file
/// descriptor (directory attribute set) whose data holds "." / ".." links plus
/// one entry per child. Files and directories each occupy one file-descriptor
/// sector and a single contiguous segment.
/// </summary>
public sealed class Os9RbfWriter {

  /// <summary>A node in the directory tree assembled before layout.</summary>
  private sealed class Node {
    public required string Name;
    public bool IsDirectory;
    public byte[] Data = [];                       // file payload (files only)
    public readonly Dictionary<string, Node> Children = new(StringComparer.Ordinal);
    public readonly List<Node> OrderedChildren = [];

    // Assigned during layout.
    public int FdLsn;
    public int DataLsn;
    public int DataSectors;
    public int ParentFdLsn;

    public Node AddChild(string name, bool isDirectory) {
      if (this.Children.TryGetValue(name, out var existing)) return existing;
      var child = new Node { Name = name, IsDirectory = isDirectory };
      this.Children[name] = child;
      this.OrderedChildren.Add(child);
      return child;
    }
  }

  /// <summary>
  /// Builds an OS-9 RBF image. Each path component may be at most 28 ASCII
  /// characters. Forward slashes in <c>name</c> introduce subdirectories.
  /// </summary>
  /// <param name="files">Files to embed (names may contain '/' separators).</param>
  /// <param name="volumeName">Volume label (max 31 ASCII chars).</param>
  public static byte[] Build(
    IReadOnlyList<(string Name, byte[] Data)> files,
    string volumeName = "CWBOS9") {

    var root = BuildTree(files);

    var image = new byte[Os9Layout.TotalBytes];

    // ── Plan layout ──────────────────────────────────────────────────────
    //   LSN 0           : identification sector
    //   LSN 1..1+B-1    : allocation bitmap (B sectors)
    //   LSN dirFdLsn    : root directory descriptor (1 sector)
    //   then, depth-first: each node's FD sector, its data sectors.
    var rootFdLsn = Os9Layout.BitmapLsn + Os9Layout.BitmapSectors;
    var nextLsn = rootFdLsn;
    AssignLayout(root, parentFdLsn: rootFdLsn, ref nextLsn);

    if (nextLsn > Os9Layout.TotalSectors)
      throw new ArgumentException(
        $"OS-9 RBF: layout requires {nextLsn} sectors, exceeds {Os9Layout.TotalSectors} sector capacity.", nameof(files));

    var now = DateTime.Now;

    // ── Identification sector ────────────────────────────────────────────
    var id = image.AsSpan(0, Os9Layout.SectorSize);
    WriteU24Be(id, Os9Layout.Pd_DD_TOT, Os9Layout.TotalSectors);
    id[Os9Layout.Pd_DD_TKS] = (byte)Os9Layout.SectorsPerTrack;
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_MAP..], (ushort)Os9Layout.BitmapBytes);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_BIT..], (ushort)Os9Layout.ClusterSizeSectors);
    WriteU24Be(id, Os9Layout.Pd_DD_DIR, rootFdLsn);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_OWN..], 0);
    id[Os9Layout.Pd_DD_ATT] = 0xFF; // permissions
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_DSK..], (ushort)Random.Shared.Next(0, ushort.MaxValue));
    id[Os9Layout.Pd_DD_FMT] = 0x03; // double-sided, double-density
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_SPT..], (ushort)Os9Layout.SectorsPerTrack);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_RES..], 0);
    WriteU24Be(id, Os9Layout.Pd_DD_BT, 0);
    BinaryPrimitives.WriteUInt16BigEndian(id[Os9Layout.Pd_DD_BSZ..], 0);
    id[Os9Layout.Pd_DD_DAT + 0] = (byte)(now.Year % 100);
    id[Os9Layout.Pd_DD_DAT + 1] = (byte)now.Month;
    id[Os9Layout.Pd_DD_DAT + 2] = (byte)now.Day;
    id[Os9Layout.Pd_DD_DAT + 3] = (byte)now.Hour;
    id[Os9Layout.Pd_DD_DAT + 4] = (byte)now.Minute;
    WriteHighBitTerminatedAscii(id[Os9Layout.Pd_DD_NAM..], volumeName, Os9Layout.SectorSize - Os9Layout.Pd_DD_NAM);

    // ── Allocation bitmap ───────────────────────────────────────────────
    var bitmap = image.AsSpan(Os9Layout.BitmapLsn * Os9Layout.SectorSize, Os9Layout.BitmapSectors * Os9Layout.SectorSize);
    for (var lsn = 0; lsn < nextLsn; lsn++) MarkAllocated(bitmap, lsn);
    // Bits past the real capacity read as 1 so allocators never hand them out.
    for (var bit = Os9Layout.TotalSectors; bit < Os9Layout.BitmapBytes * 8; bit++) MarkAllocated(bitmap, bit);

    // ── Emit every node depth-first ─────────────────────────────────────
    EmitNode(image, root, now);

    return image;
  }

  /// <summary>
  /// Folds a flat list of (possibly slash-separated) file names into a tree of
  /// directory and file nodes rooted at a synthetic root directory.
  /// </summary>
  private static Node BuildTree(IReadOnlyList<(string Name, byte[] Data)> files) {
    var root = new Node { Name = string.Empty, IsDirectory = true };

    foreach (var (rawName, data) in files) {
      var parts = SplitPath(rawName);
      if (parts.Count == 0)
        throw new InvalidOperationException($"OS-9 RBF: empty filename \"{rawName}\".");

      foreach (var part in parts) {
        if (part.Length > Os9Layout.DirEntryNameMaxBytes - 1)
          throw new InvalidOperationException(
            $"OS-9 RBF: path component \"{part}\" exceeds {Os9Layout.DirEntryNameMaxBytes - 1} characters.");
        if (!IsAsciiPrintable(part))
          throw new InvalidOperationException(
            $"OS-9 RBF: path component \"{part}\" contains non-printable ASCII characters.");
      }

      var cursor = root;
      for (var i = 0; i < parts.Count - 1; i++) {
        var dir = cursor.AddChild(parts[i], isDirectory: true);
        if (!dir.IsDirectory)
          throw new InvalidOperationException(
            $"OS-9 RBF: \"{parts[i]}\" is used both as a file and a directory.");
        cursor = dir;
      }

      var leafName = parts[^1];
      if (cursor.Children.ContainsKey(leafName))
        throw new InvalidOperationException($"OS-9 RBF: duplicate entry \"{rawName}\".");
      var leaf = cursor.AddChild(leafName, isDirectory: false);
      leaf.Data = data;
    }

    return root;
  }

  private static List<string> SplitPath(string name) {
    var parts = new List<string>();
    foreach (var raw in name.Replace('\\', '/').Split('/')) {
      if (raw.Length == 0 || raw == ".") continue;
      parts.Add(raw);
    }
    return parts;
  }

  /// <summary>
  /// Assigns the FD sector, data start sector and data sector count for a node
  /// and all its descendants, advancing <paramref name="nextLsn"/>. The node's
  /// own FD sector is expected to already be reserved by the caller; directory
  /// data is laid out before recursing into children so a directory's data
  /// sectors stay contiguous.
  /// </summary>
  private static void AssignLayout(Node node, int parentFdLsn, ref int nextLsn) {
    node.ParentFdLsn = parentFdLsn;
    node.FdLsn = nextLsn++;

    if (node.IsDirectory) {
      // "." + ".." + one entry per child.
      var entryCount = node.OrderedChildren.Count + 2;
      var byteLen = entryCount * Os9Layout.DirEntryBytes;
      var sectors = (byteLen + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
      if (sectors == 0) sectors = 1;
      node.DataLsn = nextLsn;
      node.DataSectors = sectors;
      nextLsn += sectors;

      foreach (var child in node.OrderedChildren)
        AssignLayout(child, parentFdLsn: node.FdLsn, ref nextLsn);
    } else {
      var dataSec = (node.Data.Length + Os9Layout.SectorSize - 1) / Os9Layout.SectorSize;
      if (dataSec == 0) {
        node.DataLsn = 0;
        node.DataSectors = 0;
      } else {
        node.DataLsn = nextLsn;
        node.DataSectors = dataSec;
        nextLsn += dataSec;
      }
    }
  }

  /// <summary>Writes a node's FD, directory entries (if any) and payload, then recurses.</summary>
  private static void EmitNode(byte[] image, Node node, DateTime now) {
    var fd = image.AsSpan(node.FdLsn * Os9Layout.SectorSize, Os9Layout.SectorSize);
    fd[Os9Layout.FD_ATT] = node.IsDirectory ? Os9Layout.DefaultDirAttr : Os9Layout.DefaultFileAttr;
    BinaryPrimitives.WriteUInt16BigEndian(fd[Os9Layout.FD_OWN..], 0);
    fd[Os9Layout.FD_DAT + 0] = (byte)(now.Year % 100);
    fd[Os9Layout.FD_DAT + 1] = (byte)now.Month;
    fd[Os9Layout.FD_DAT + 2] = (byte)now.Day;
    fd[Os9Layout.FD_DAT + 3] = (byte)now.Hour;
    fd[Os9Layout.FD_DAT + 4] = (byte)now.Minute;
    fd[Os9Layout.FD_CRE + 0] = (byte)(now.Year % 100);
    fd[Os9Layout.FD_CRE + 1] = (byte)now.Month;
    fd[Os9Layout.FD_CRE + 2] = (byte)now.Day;

    if (node.IsDirectory) {
      // Link count for a directory = 2 (its own entry + the "." self link)
      // plus one per child subdirectory's ".." back-reference.
      var subdirCount = node.OrderedChildren.Count(c => c.IsDirectory);
      fd[Os9Layout.FD_LNK] = (byte)Math.Min(255, 2 + subdirCount);

      var entryCount = node.OrderedChildren.Count + 2;
      var byteLen = (uint)(entryCount * Os9Layout.DirEntryBytes);
      BinaryPrimitives.WriteUInt32BigEndian(fd[Os9Layout.FD_SIZ..], byteLen);
      WriteU24Be(fd, Os9Layout.FD_SEG + 0, node.DataLsn);
      BinaryPrimitives.WriteUInt16BigEndian(fd[(Os9Layout.FD_SEG + 3)..], (ushort)node.DataSectors);

      var dirData = image.AsSpan(node.DataLsn * Os9Layout.SectorSize, node.DataSectors * Os9Layout.SectorSize);
      var off = 0;
      // "." → this directory's own FD.
      WriteHighBitTerminatedAscii(dirData[off..], ".", Os9Layout.DirEntryNameMaxBytes);
      WriteU24Be(dirData, off + Os9Layout.DirEntryFdLsnOffset, node.FdLsn);
      off += Os9Layout.DirEntryBytes;
      // ".." → parent directory's FD (root points back to itself).
      WriteHighBitTerminatedAscii(dirData[off..], "..", Os9Layout.DirEntryNameMaxBytes);
      WriteU24Be(dirData, off + Os9Layout.DirEntryFdLsnOffset, node.ParentFdLsn);
      off += Os9Layout.DirEntryBytes;

      foreach (var child in node.OrderedChildren) {
        WriteHighBitTerminatedAscii(dirData[off..], child.Name, Os9Layout.DirEntryNameMaxBytes);
        WriteU24Be(dirData, off + Os9Layout.DirEntryFdLsnOffset, child.FdLsn);
        off += Os9Layout.DirEntryBytes;
      }

      foreach (var child in node.OrderedChildren)
        EmitNode(image, child, now);
    } else {
      fd[Os9Layout.FD_LNK] = 1;
      BinaryPrimitives.WriteUInt32BigEndian(fd[Os9Layout.FD_SIZ..], (uint)node.Data.Length);
      if (node.DataSectors > 0) {
        WriteU24Be(fd, Os9Layout.FD_SEG + 0, node.DataLsn);
        BinaryPrimitives.WriteUInt16BigEndian(fd[(Os9Layout.FD_SEG + 3)..], (ushort)node.DataSectors);
        node.Data.CopyTo(image.AsSpan(node.DataLsn * Os9Layout.SectorSize));
      }
    }
  }

  private static bool IsAsciiPrintable(string s) {
    foreach (var c in s) if (c is < (char)0x20 or > (char)0x7E) return false;
    return true;
  }

  private static void MarkAllocated(Span<byte> bitmap, int bit) {
    var byteIdx = bit / 8;
    if (byteIdx >= bitmap.Length) return;
    bitmap[byteIdx] |= (byte)(0x80 >> (bit % 8));
  }

  private static void WriteU24Be(Span<byte> span, int offset, int value) {
    span[offset + 0] = (byte)((value >> 16) & 0xFF);
    span[offset + 1] = (byte)((value >> 8) & 0xFF);
    span[offset + 2] = (byte)(value & 0xFF);
  }

  internal static void WriteHighBitTerminatedAscii(Span<byte> dest, string text, int maxBytes) {
    if (string.IsNullOrEmpty(text)) return;
    var bytes = Encoding.ASCII.GetBytes(text);
    var n = Math.Min(bytes.Length, maxBytes);
    if (n == 0) return;
    for (var i = 0; i < n; i++) dest[i] = (byte)(bytes[i] & 0x7F);
    dest[n - 1] |= 0x80; // last char carries MSB
  }
}
