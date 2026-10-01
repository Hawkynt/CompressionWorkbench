#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Reiser4;

/// <summary>
/// Builds the two-level reiser4 tree: a leaf with the root directory's stat data
/// and entries plus every file's stat data, and above it the twig that points at
/// that leaf and holds each file's extents.
/// </summary>
/// <remarks>
/// <para>A node is a 28-byte header, item bodies growing up from there, and an
/// array of 38-byte item headers growing down from the node's end — each a
/// four-word key, the body's offset, flags, and which plugin reads it. Items sit
/// in key order.</para>
///
/// <para>Extents are twig items, not leaf items: reiser4 keeps the pointers to a
/// file body's unformatted blocks one level above the leaves, next to the pointers
/// to those leaves. <c>fsck.reiser4</c> rejects a level-1 node holding an extent
/// as broken ("node level does not match the item type"), so the body keys, which
/// sort after every stat data and entry, follow the leaf pointer in the twig.</para>
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
  private const int PluginNodePtr40 = 0x3;

  private const byte MinorFileName = 0;
  private const byte MinorStatData = 1;
  private const byte MinorFileBody = 4;

  /// <summary>Characters of a name the ordering holds, its top byte being the fibre.</summary>
  private const int OrderingChars = 7;

  /// <summary>And the object id after it.</summary>
  private const int ObjectIdChars = 8;

  /// <summary>Past this many a name no longer fits its key and is hashed into it.</summary>
  internal const int MaxInlineNameLength = OrderingChars + ObjectIdChars + 8;

  /// <summary>The bit in the ordering that says the name was hashed, not held.</summary>
  private const ulong HashedNameBit = 0x0100000000000000;

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

  /// <summary>
  /// The r5 hash, which a name too long to be held in its key is reduced to.
  /// </summary>
  private static ulong R5Hash(string name, int from) {
    ulong a = 0;
    for (var i = from; i < name.Length; ++i) {
      var b = (byte)name[i];
      a += (ulong)(b << 4);
      a += (ulong)(b >> 4);
      a *= 11;
    }

    return a;
  }

  /// <summary>The three words a directory entry's key carries a name in.</summary>
  /// <remarks>
  /// A name of twenty-three characters or fewer fits whole; past that only its
  /// first fifteen are held and the rest becomes a hash, with a bit set in the
  /// ordering to say so — and the name itself is then stored beside the entry.
  /// </remarks>
  private static (ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed) NameKey(string name) {
    if (name == ".") return (0, 0, 0, false);

    var ordering = PackName(name, 0, 1);
    var objectId = name.Length > OrderingChars ? PackName(name, OrderingChars, 0) : 0;

    ulong offset;
    var hashed = name.Length > MaxInlineNameLength;
    if (hashed) {
      ordering |= HashedNameBit;
      offset = R5Hash(name, OrderingChars + ObjectIdChars);
    } else {
      offset = name.Length > OrderingChars + ObjectIdChars
        ? PackName(name, OrderingChars + ObjectIdChars, 0)
        : 0;
    }

    return (ordering | (ulong)Fibre(name) << 57, objectId, offset, hashed);
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

    var keyed = new List<(ulong Ordering, ulong ObjectId, ulong Offset, bool Hashed,
                          string Name, ulong Locality, ulong Target)>();
    foreach (var (name, locality, target) in names) {
      var (ordering, objectId, offset, hashed) = NameKey(name);
      keyed.Add((ordering, objectId, offset, hashed, name, locality, target));
    }

    keyed.Sort((a, b) => a.Ordering != b.Ordering ? a.Ordering.CompareTo(b.Ordering)
      : a.ObjectId != b.ObjectId ? a.ObjectId.CompareTo(b.ObjectId)
      : a.Offset.CompareTo(b.Offset));

    const int unitHeaderBytes = 26;
    const int targetKeyBytes = 24;

    // A hashed name is kept beside the key it did not fit into, NUL-terminated.
    var unitBytes = 0;
    foreach (var entry in keyed)
      unitBytes += targetKeyBytes + (entry.Hashed ? Encoding.ASCII.GetByteCount(entry.Name) + 1 : 0);

    var body = new byte[2 + keyed.Count * unitHeaderBytes + unitBytes];
    BinaryPrimitives.WriteUInt16LittleEndian(body, (ushort)keyed.Count);

    var unit = 2 + keyed.Count * unitHeaderBytes;
    for (var i = 0; i < keyed.Count; ++i) {
      var (ordering, objectId, offset, hashed, name, locality, target) = keyed[i];
      var header = 2 + i * unitHeaderBytes;
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header), ordering);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 8), objectId);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 16), offset);
      BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(header + 24), (ushort)unit);

      // What the entry points at: the first three words of its target's stat-data key.
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit), locality << 4 | MinorStatData);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit + 8), 0);
      BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit + 16), target);
      unit += targetKeyBytes;

      if (!hashed) continue;

      var nameBytes = Encoding.ASCII.GetBytes(name);
      nameBytes.CopyTo(body.AsSpan(unit));
      unit += nameBytes.Length + 1;
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
    var directory = Directory(rootLocality, rootObjectId, files, blockSize);

    // A directory's size is its entry count and its bytes the space its entries
    // take (every unit, without the item's two-byte count); fsck.reiser4 checks
    // both against the entries it finds. mkfs wrote them for "." and ".." alone.
    if ((BinaryPrimitives.ReadUInt16LittleEndian(rootStat) & 0x3) != 0x3 || rootStat.Length < 44)
      throw new InvalidOperationException("Reiser4: the root stat-data template lacks the light-weight and unix extensions.");
    BinaryPrimitives.WriteUInt64LittleEndian(rootStat.AsSpan(8), (ulong)(2 + files.Count));
    BinaryPrimitives.WriteUInt64LittleEndian(rootStat.AsSpan(36), (ulong)(directory.Length - 2));

    var bodies = new List<(ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong Offset,
                           int Plugin, byte[] Body)> {
      (rootLocality, MinorStatData, 0, rootObjectId, 0, PluginStat40, rootStat),
      (rootObjectId, MinorFileName, 0, 0, 0, PluginCde40, directory),
    };

    foreach (var file in files) {
      var held = 0UL;
      foreach (var run in file.Runs) held += run.Width;
      bodies.Add((rootObjectId, MinorStatData, 0, file.ObjectId, 0, PluginStat40,
        StatData(0x81A4, 1, (ulong)file.Size, held * (ulong)blockSize, time)));
    }

    WriteNode(leaf, blockSize, mkfsId, level: 1, bodies);
  }

  /// <summary>
  /// Rewrites <paramref name="twig" /> so it points at the leaf and carries every
  /// file's extents, in key order after that pointer.
  /// </summary>
  /// <param name="twig">The twig as mkfs left it, whose single leaf pointer is kept.</param>
  internal static void BuildTwig(Span<byte> twig, int blockSize, uint mkfsId, ulong rootObjectId, IReadOnlyList<Entry> files) {
    var pointerHeader = blockSize - ItemHeaderBytes;
    if (BinaryPrimitives.ReadUInt16LittleEndian(twig[2..]) != 1
        || BinaryPrimitives.ReadUInt16LittleEndian(twig[(pointerHeader + 36)..]) != PluginNodePtr40)
      throw new InvalidOperationException("Reiser4: the twig template does not hold exactly one leaf pointer.");
    var pointerOffset = BinaryPrimitives.ReadUInt16LittleEndian(twig[(pointerHeader + 32)..]);
    var key = twig.Slice(pointerHeader, 32);
    var first = BinaryPrimitives.ReadUInt64LittleEndian(key);

    var bodies = new List<(ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong Offset,
                           int Plugin, byte[] Body)> {
      (first >> 4, (byte)(first & 0xF), BinaryPrimitives.ReadUInt64LittleEndian(key[8..]),
       BinaryPrimitives.ReadUInt64LittleEndian(key[16..]), BinaryPrimitives.ReadUInt64LittleEndian(key[24..]),
       PluginNodePtr40, twig.Slice(pointerOffset, 8).ToArray()),
    };

    // Body keys share the root's object id as locality and differ only in the
    // file's object id, so object-id order is key order.
    foreach (var file in files.OrderBy(static f => f.ObjectId)) {
      if (file.Runs.Count == 0) continue;

      var extents = new byte[file.Runs.Count * 16];
      for (var i = 0; i < file.Runs.Count; ++i) {
        BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16), file.Runs[i].Start);
        BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16 + 8), file.Runs[i].Width);
      }

      bodies.Add((rootObjectId, MinorFileBody, 0, file.ObjectId, 0, PluginExtent40, extents));
    }

    WriteNode(twig, blockSize, mkfsId, level: 2, bodies);
  }

  /// <summary>Lays <paramref name="bodies" /> out as one node40 at <paramref name="level" />.</summary>
  private static void WriteNode(Span<byte> leaf, int blockSize, uint mkfsId, byte level,
      List<(ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong Offset, int Plugin, byte[] Body)> bodies) {
    leaf[..blockSize].Clear();
    var at = NodeHeaderBytes;
    for (var i = 0; i < bodies.Count; ++i) {
      var (locality, minor, ordering, objectId, offset, plugin, body) = bodies[i];
      if (at + body.Length > blockSize - (i + 1) * ItemHeaderBytes)
        throw new InvalidOperationException(
          $"Reiser4: {bodies.Count} items do not fit one {blockSize}-byte node; this writer builds a single leaf under a single twig.");

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
    leaf[26] = level;                                                        // 1 = leaf, 2 = twig
    leaf[27] = 0;
  }
}
