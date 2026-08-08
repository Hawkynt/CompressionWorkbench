#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Reiser4;

/// <summary>
/// Builds the leaf of a reiser4 tree: the root directory's stat data and entries,
/// and for each file a stat data and the extents holding its bytes.
/// </summary>
/// <remarks>
/// <para>A node is a 28-byte header, item bodies growing up from there, and an
/// array of 38-byte item headers growing down from the node's end — each a
/// four-word key, the body's offset, flags, and which plugin reads it. Items sit
/// in key order.</para>
///
/// <para>A key is four little-endian words: the locality in the top sixty bits of
/// the first with the item's type in its low four, an ordering, an object id, and
/// an offset. A file's stat data and its body share the first three and differ in
/// the type and the offset, so every stat data sorts ahead of every body.</para>
///
/// <para>A directory entry carries its name in its own key rather than a hash of
/// it, for any name of twenty-three characters or fewer. The bytes pack
/// big-endian into the ordering starting one byte in, then the object id, then the
/// offset; the ordering's top seven bits hold a fibre, which is the last character
/// when the one before it is a dot. A name held that way is stored nowhere else.</para>
/// </remarks>
internal static class Reiser4Tree {

  internal const int NodeHeaderBytes = 28;
  internal const int ItemHeaderBytes = 38;
  internal const int NodeMagic = 0x52344653;

  private const int PluginStat40 = 0x0;
  private const int PluginCde40 = 0x2;
  private const int PluginExtent40 = 0x5;

  private const byte MinorFileName = 0;
  private const byte MinorStatData = 1;
  private const byte MinorFileBody = 4;

  /// <summary>Characters of a name the ordering holds, its top byte being the fibre.</summary>
  private const int OrderingChars = 7;

  /// <summary>And the object id after it.</summary>
  private const int ObjectIdChars = 8;

  /// <summary>Past this many a name is hashed instead, which this does not write.</summary>
  internal const int MaxInlineNameLength = OrderingChars + ObjectIdChars + 8;

  /// <summary>One run of blocks holding part of a file.</summary>
  internal readonly record struct Run(ulong Start, ulong Width);

  /// <summary>A file to put in the tree.</summary>
  internal sealed class Entry {
    internal required string Name { get; init; }
    internal required ulong ObjectId { get; init; }
    internal required long Size { get; init; }
    internal required IReadOnlyList<Run> Runs { get; init; }
  }

  /// <summary>The name packed into a word, big-endian, from <paramref name="from" />.</summary>
  private static ulong PackName(string name, int from, int skip) {
    ulong value = 0;
    var taken = 0;
    for (var i = from; i < name.Length && taken < 8 - skip; ++i, ++taken)
      value = value << 8 | (byte)name[i];

    return value << (8 - taken - skip) * 8;
  }

  /// <summary>The fibre a name sorts under: its last character after a one-letter suffix.</summary>
  private static byte Fibre(string name)
    => name.Length > 2 && name[^2] == '.' ? (byte)name[^1] : (byte)0;

  private static void WriteKey(Span<byte> at, ulong locality, byte minor,
                               ulong ordering, ulong objectId, ulong offset) {
    BinaryPrimitives.WriteUInt64LittleEndian(at, locality << 4 | minor);
    BinaryPrimitives.WriteUInt64LittleEndian(at[8..], ordering);
    BinaryPrimitives.WriteUInt64LittleEndian(at[16..], objectId);
    BinaryPrimitives.WriteUInt64LittleEndian(at[24..], offset);
  }

  /// <summary>The three words a directory entry's key carries a name in.</summary>
  private static (ulong Ordering, ulong ObjectId, ulong Offset) NameKey(string name) {
    if (name == ".") return (0, 0, 0);

    var ordering = PackName(name, 0, 1) | (ulong)Fibre(name) << 57;
    var objectId = name.Length > OrderingChars ? PackName(name, OrderingChars, 0) : 0;
    var offset = name.Length > OrderingChars + ObjectIdChars
      ? PackName(name, OrderingChars + ObjectIdChars, 0)
      : 0;
    return (ordering, objectId, offset);
  }

  /// <summary>A stat data saying what a file is and how long.</summary>
  private static byte[] StatData(ushort mode, uint links, ulong size, ulong bytes, uint time) {
    var body = new byte[2 + 14 + 28];
    BinaryPrimitives.WriteUInt16LittleEndian(body, 0x3);              // light-weight and unix
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), mode);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), links);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), 0);     // uid
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), 0);     // gid
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), time);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), time);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), time);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(36), bytes);
    return body;
  }

  /// <summary>The root's entries, its own two and one for each file.</summary>
  private static byte[] Directory(ulong rootLocality, ulong rootObjectId,
                                  IReadOnlyList<Entry> files, int blockSize) {
    var names = new List<(string Name, ulong Locality, ulong ObjectId)> {
      (".", rootLocality, rootObjectId),
      ("..", rootLocality, rootObjectId),
    };
    foreach (var file in files) names.Add((file.Name, rootObjectId, file.ObjectId));

    var keyed = new List<(ulong Ordering, ulong ObjectId, ulong Offset, ulong Locality, ulong Target)>();
    foreach (var (name, locality, target) in names) {
      var (ordering, objectId, offset) = NameKey(name);
      keyed.Add((ordering, objectId, offset, locality, target));
    }

    keyed.Sort((a, b) => a.Ordering != b.Ordering ? a.Ordering.CompareTo(b.Ordering)
      : a.ObjectId != b.ObjectId ? a.ObjectId.CompareTo(b.ObjectId)
      : a.Offset.CompareTo(b.Offset));

    const int unitHeaderBytes = 26;
    const int targetKeyBytes = 24;
    var body = new byte[2 + keyed.Count * unitHeaderBytes + keyed.Count * targetKeyBytes];
    BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)keyed.Count);

    var unitsAt = 2 + keyed.Count * unitHeaderBytes;
    for (var i = 0; i < keyed.Count; ++i) {
      var (ordering, objectId, offset, locality, target) = keyed[i];
      var header = 2 + i * unitHeaderBytes;
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header), ordering);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 8), objectId);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 16), offset);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(header + 24),
        (ushort)(unitsAt + i * targetKeyBytes));

      // What the entry points at: the first three words of its target's stat-data key.
      var unit = unitsAt + i * targetKeyBytes;
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit), locality << 4 | MinorStatData);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit + 8), 0);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit + 16), target);
    }

    return body;
  }

  /// <summary>
  /// Rewrites <paramref name="leaf" /> so it holds the root and every file given.
  /// </summary>
  /// <param name="leaf">The leaf as mkfs left it, whose root stat data is kept.</param>
  internal static void Build(Span<byte> leaf, int blockSize, uint mkfsId, uint time,
                             ulong rootLocality, ulong rootObjectId, IReadOnlyList<Entry> files) {
    // The root's own stat data, kept as mkfs wrote it — it carries the plugin set
    // every file below it inherits, which this does not attempt to rebuild.
    var rootStatOffset = BinaryPrimitives.ReadUInt16LittleEndian(leaf[(blockSize - ItemHeaderBytes + 32)..]);
    var rootStatLength = BinaryPrimitives.ReadUInt16LittleEndian(leaf[(blockSize - 2 * ItemHeaderBytes + 32)..])
      - rootStatOffset;
    var rootStat = leaf.Slice(rootStatOffset, rootStatLength).ToArray();

    var bodies = new List<(ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong Offset,
                           int Plugin, byte[] Body)> {
      (rootLocality, MinorStatData, 0, rootObjectId, 0, PluginStat40, rootStat),
      (rootObjectId, MinorFileName, 0, 0, 0, PluginCde40, Directory(rootLocality, rootObjectId, files, blockSize)),
    };

    foreach (var file in files) {
      var held = 0UL;
      foreach (var run in file.Runs) held += run.Width;
      bodies.Add((rootObjectId, MinorStatData, 0, file.ObjectId, 0, PluginStat40,
        StatData(0x81A4, 1, (ulong)file.Size, held * (ulong)blockSize, time)));
    }

    foreach (var file in files) {
      if (file.Runs.Count == 0) continue;

      var extents = new byte[file.Runs.Count * 16];
      for (var i = 0; i < file.Runs.Count; ++i) {
        BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16), file.Runs[i].Start);
        BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16 + 8), file.Runs[i].Width);
      }

      bodies.Add((rootObjectId, MinorFileBody, 0, file.ObjectId, 0, PluginExtent40, extents));
    }

    leaf[..blockSize].Clear();
    var at = NodeHeaderBytes;
    for (var i = 0; i < bodies.Count; ++i) {
      var (locality, minor, ordering, objectId, offset, plugin, body) = bodies[i];
      if (at + body.Length > blockSize - (i + 1) * ItemHeaderBytes)
        throw new InvalidOperationException(
          $"Reiser4: {bodies.Count} items do not fit one {blockSize}-byte leaf; this writer builds one.");

      body.CopyTo(leaf[at..]);
      var header = blockSize - (i + 1) * ItemHeaderBytes;
      WriteKey(leaf[header..], locality, minor, ordering, objectId, offset);
      BinaryPrimitives.WriteUInt16LittleEndian(leaf[(header + 32)..], (ushort)at);
      BinaryPrimitives.WriteUInt16LittleEndian(leaf[(header + 34)..], 0);
      BinaryPrimitives.WriteUInt16LittleEndian(leaf[(header + 36)..], (ushort)plugin);
      at += body.Length;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(leaf, 0);                       // node40
    BinaryPrimitives.WriteUInt16LittleEndian(leaf[2..], (ushort)bodies.Count);
    BinaryPrimitives.WriteUInt16LittleEndian(leaf[4..],
      (ushort)(blockSize - bodies.Count * ItemHeaderBytes - at));
    BinaryPrimitives.WriteUInt16LittleEndian(leaf[6..], (ushort)at);
    BinaryPrimitives.WriteUInt32LittleEndian(leaf[8..], unchecked((uint)NodeMagic));
    BinaryPrimitives.WriteUInt32LittleEndian(leaf[12..], mkfsId);
    BinaryPrimitives.WriteUInt64LittleEndian(leaf[16..], 0);                 // flush id
    BinaryPrimitives.WriteUInt16LittleEndian(leaf[24..], 0);                 // flags
    leaf[26] = 1;                                                            // level: a leaf
    leaf[27] = 0;
  }
}
