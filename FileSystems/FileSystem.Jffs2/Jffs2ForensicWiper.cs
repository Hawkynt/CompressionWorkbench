#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Jffs2;

/// <summary>
/// Forensic wipe for the log-structured JFFS2: a delete only appends an unlink
/// dirent (ino = 0); the deleted file's inode data nodes and its now-superseded
/// dirents physically persist in the log until the kernel garbage-collects them,
/// so the deleted content stays recoverable. This zeros those obsolete nodes —
/// the data of every inode the live directory tree no longer references, plus the
/// dirents naming deleted/superseded entries — while leaving every live node
/// untouched (the scanner skips a zeroed, magic-less region as free space, so the
/// live files still read back).
/// </summary>
internal static class Jffs2ForensicWiper {
  private const ushort Magic = 0x1985;
  private const ushort NodeTypeDirent = 0xE001;
  private const ushort NodeTypeInode = 0xE002;

  private readonly record struct Node(int Off, int TotLen, ushort Type, long Pino, long Ino, uint Version, string Name);

  /// <summary>Zeros obsolete dirent/inode nodes in <paramref name="image"/>. Returns bytes zeroed.</summary>
  public static long WipeObsolete(byte[] image) {
    var nodes = Walk(image);

    // Live inode set: the latest dirent per (parent, name) wins; a non-zero target
    // ino there is live (plus root ino 1). Everything else is a deleted/orphaned file.
    var latestDirent = new Dictionary<(long, string), Node>();
    foreach (var n in nodes) {
      if (n.Type != NodeTypeDirent) continue;
      var key = (n.Pino, n.Name);
      if (!latestDirent.TryGetValue(key, out var cur) || n.Version > cur.Version)
        latestDirent[key] = n;
    }
    var liveInos = new HashSet<long> { 1 };
    foreach (var d in latestDirent.Values)
      if (d.Ino != 0) liveInos.Add(d.Ino);

    long wiped = 0;
    foreach (var n in nodes) {
      var obsolete = n.Type switch {
        // Dirent: obsolete unless it's the live (latest, non-unlink) entry for its name.
        NodeTypeDirent => !(latestDirent.TryGetValue((n.Pino, n.Name), out var latest)
                            && latest.Off == n.Off && latest.Ino != 0),
        // Inode: obsolete when its file is no longer reachable from the live tree.
        NodeTypeInode => !liveInos.Contains(n.Ino),
        _ => false,
      };
      if (!obsolete) continue;
      var end = Math.Min(image.Length, n.Off + n.TotLen);
      for (var i = n.Off; i < end; i++)
        if (image[i] != 0) { image[i] = 0; wiped++; }
    }
    return wiped;
  }

  private static List<Node> Walk(byte[] image) {
    var nodes = new List<Node>();
    var off = 0;
    while (off + 12 <= image.Length) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off, 2)) != Magic) { off += 4; continue; }
      var type = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(off + 2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 4, 4));
      if (totLen < 12 || totLen > (uint)image.Length || off + (long)totLen > image.Length) { off += 4; continue; }

      if (type == NodeTypeDirent && off + 40 <= image.Length) {
        var pino = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 12, 4));
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 16, 4));
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 20, 4));
        var nsize = image[off + 28];
        var name = "";
        if (nsize is > 0 and <= 128 && off + 40 + nsize <= image.Length && 40 + nsize <= (int)totLen)
          name = Encoding.UTF8.GetString(image.AsSpan(off + 40, nsize));
        nodes.Add(new Node(off, (int)totLen, type, pino, ino, ver, name));
      } else if (type == NodeTypeInode && off + 20 <= image.Length) {
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 12, 4));
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(off + 16, 4));
        nodes.Add(new Node(off, (int)totLen, type, 0, ino, ver, ""));
      }
      off += ((int)totLen + 3) & ~3;
    }
    return nodes;
  }
}
