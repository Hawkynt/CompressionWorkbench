#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

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

  private readonly record struct Node(long Off, int TotLen, ushort Type, long Pino, long Ino, uint Version, string Name);

  /// <summary>Bytes read per node probe: the dirent header plus the longest name it can carry.</summary>
  private const int MaxNodeProbe = 40 + 128;

  /// <summary>Zeros obsolete dirent/inode nodes in <paramref name="image"/>. Returns bytes zeroed.</summary>
  public static long WipeObsolete(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    using var accessor = new ImageAccessor(image);
    var nodes = Walk(accessor);

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
    var scratch = new byte[MaxNodeProbe];
    var zeros = new byte[64 * 1024];
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
      var end = Math.Min(accessor.Length, n.Off + n.TotLen);
      var span = end - n.Off;
      if (span <= 0) continue;

      // Count what actually changes so the caller's byte tally stays honest,
      // then overwrite the node in one pass.
      var remaining = span;
      var probe = n.Off;
      while (remaining > 0) {
        var chunk = (int)Math.Min(scratch.Length, remaining);
        var read = accessor.Read(probe, scratch.AsSpan(0, chunk));
        for (var i = 0; i < read; ++i)
          if (scratch[i] != 0) ++wiped;
        probe += read;
        remaining -= read;
        if (read <= 0) break;
      }

      image.Position = n.Off;
      remaining = span;
      while (remaining > 0) {
        var chunk = (int)Math.Min(zeros.Length, remaining);
        image.Write(zeros, 0, chunk);
        remaining -= chunk;
      }
    }
    image.Flush();
    return wiped;
  }

  private static List<Node> Walk(ImageAccessor image) {
    var nodes = new List<Node>();
    var buffer = new byte[MaxNodeProbe];
    long off = 0;
    while (off + 12 <= image.Length) {
      var want = (int)Math.Min(MaxNodeProbe, image.Length - off);
      var read = image.Read(off, buffer.AsSpan(0, want));
      if (read < 12) break;
      var node = buffer.AsSpan(0, read);

      if (BinaryPrimitives.ReadUInt16LittleEndian(node[..2]) != Magic) { off += 4; continue; }
      var type = BinaryPrimitives.ReadUInt16LittleEndian(node.Slice(2, 2));
      var totLen = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(4, 4));
      if (totLen < 12 || off + totLen > image.Length) { off += 4; continue; }

      if (type == NodeTypeDirent && node.Length >= 40) {
        var pino = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(12, 4));
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(16, 4));
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(20, 4));
        var nsize = node[28];
        var name = "";
        if (nsize is > 0 and <= 128 && 40 + nsize <= node.Length && 40 + nsize <= totLen)
          name = Encoding.UTF8.GetString(node.Slice(40, nsize));
        nodes.Add(new Node(off, (int)totLen, type, pino, ino, ver, name));
      } else if (type == NodeTypeInode && node.Length >= 20) {
        var ino = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(12, 4));
        var ver = BinaryPrimitives.ReadUInt32LittleEndian(node.Slice(16, 4));
        nodes.Add(new Node(off, (int)totLen, type, 0, ino, ver, ""));
      }
      off += (totLen + 3) & ~3u;
    }
    return nodes;
  }
}
