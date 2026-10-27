#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Reiser4;

/// <summary>
/// Builds a reiser4 node40 tree containing directory stat data and entries,
/// plus stat data and extents for regular files.
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

  /// <summary>Past this many a name no longer fits its key and is hashed into it.</summary>
  internal const int MaxInlineNameLength = OrderingChars + ObjectIdChars + 8;

  /// <summary>The bit in the ordering that says the name was hashed, not held.</summary>
  private const ulong HashedNameBit = 0x0100000000000000;

  /// <summary>One run of blocks holding part of a file.</summary>
  internal readonly record struct Run(ulong Start, ulong Width);

  /// <summary>A file to put in the tree.</summary>
  internal sealed record Entry {
    internal required string Name { get; init; }
    internal required ulong ParentObjectId { get; init; }
    internal required ulong ObjectId { get; init; }
    internal required long Size { get; init; }
    internal required IReadOnlyList<Run> Runs { get; init; }
    internal Reiser4Reader.FileMetadata? Metadata { get; init; }
  }

  private sealed record Item(ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong Offset,
      int Plugin, byte[] Body);

  internal sealed record Layout(ulong NextObjectId, ulong ObjectCount, byte Height, IReadOnlyDictionary<ulong, byte[]> Nodes);

  private sealed class DirectoryNode {
    internal required string Path { get; init; }
    internal required string Name { get; init; }
    internal required ulong ObjectId { get; init; }
    internal required ulong ParentObjectId { get; init; }
    internal required ulong StatLocality { get; init; }
    internal required ulong ParentStatLocality { get; init; }
    internal List<(string Name, ulong ObjectId)> Children { get; } = [];
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

  /// <summary>
  /// Why a stat-data body does not hold exactly the extensions its mask declares, or
  /// <see langword="null" /> when it does.
  /// </summary>
  /// <remarks>
  /// Extensions follow the 16-bit mask in bit order: light-weight (bit 0, 14 bytes),
  /// Unix (bit 1, 28), large times (bit 2, three 32-bit nanosecond fields), the plugin
  /// set (bit 4) and heir set (bit 8) — a 16-bit count of 4-byte member/plugin slots —
  /// and flags (bit 5, 32 bits). Anything else, including bit 15, which chains a
  /// further mask, is not carried: a body that does not parse is what
  /// <c>fsck.reiser4</c> calls a fatal corruption.
  /// </remarks>
  internal static string? StatDataProblem(ReadOnlySpan<byte> body) {
    if (body.Length < 2) return "the stat data has no extension mask";
    var mask = BinaryPrimitives.ReadUInt16LittleEndian(body);
    if ((mask & 3) != 3) return "the stat data lacks the light-weight or Unix extension";
    var at = 2;
    for (var bit = 0; bit < 16; ++bit) {
      if ((mask & 1 << bit) == 0) continue;
      int length;
      switch (bit) {
        case 0: length = 14; break;
        case 1: length = 28; break;
        case 2: length = 12; break;
        case 5: length = 4; break;
        case 4 or 8:
          if (at + 2 > body.Length) return "a plugin-set extension is truncated";
          length = 2 + 4 * BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
          break;
        default: return $"stat-data extension {bit} is not supported";
      }
      if (at + length > body.Length) return $"stat-data extension {bit} is truncated";
      at += length;
    }
    return at == body.Length ? null : "the stat data holds bytes its extension mask does not declare";
  }

  /// <summary>A stat data saying what a file is and how long.</summary>
  private static byte[] StatData(ushort mode, uint links, ulong size, ulong bytes, uint time,
                                 Reiser4Reader.FileMetadata? metadata = null) {
    if (metadata is { RawStatData.Length: > 0 } carried && StatDataProblem(carried.RawStatData) is { } problem)
      throw new NotSupportedException($"Reiser4: {problem}.");
    if (metadata is { } saved && saved.RawStatData.Length >= 44) {
      var preserved = saved.RawStatData.ToArray();
      BinaryPrimitives.WriteUInt16LittleEndian(preserved.AsSpan(2), saved.Mode);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(4), saved.LinkCount);
      BinaryPrimitives.WriteUInt64LittleEndian(preserved.AsSpan(8), size);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(16), saved.UserId);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(20), saved.GroupId);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(24), saved.ModifiedTime);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(28), saved.AccessedTime);
      BinaryPrimitives.WriteUInt32LittleEndian(preserved.AsSpan(32), saved.ChangedTime);
      BinaryPrimitives.WriteUInt64LittleEndian(preserved.AsSpan(36), bytes);
      return preserved;
    }
    var body = new byte[2 + 14 + 28];
    BinaryPrimitives.WriteUInt16LittleEndian(body, 0x3);              // light-weight and unix
    BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2), metadata?.Mode ?? mode);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), metadata?.LinkCount ?? links);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(8), size);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), metadata?.UserId ?? 0);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(20), metadata?.GroupId ?? 0);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(24), metadata?.ModifiedTime ?? time);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(28), metadata?.AccessedTime ?? time);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(32), metadata?.ChangedTime ?? time);
    BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(36), bytes);
    return body;
  }

  /// <summary>The root's entries, its own two and one for each file.</summary>
  private static IEnumerable<Item> Directory(DirectoryNode directory, int blockSize) {
    var names = new List<(string Name, ulong Locality, ulong ObjectId)> {
      (".", directory.StatLocality, directory.ObjectId),
      ("..", directory.ParentStatLocality, directory.ParentObjectId),
    };
    foreach (var child in directory.Children) names.Add((child.Name, directory.ObjectId, child.ObjectId));

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

    var first = 0;
    while (first < keyed.Count) {
      var last = first;
      var bodySize = 2;
      while (last < keyed.Count) {
        var entrySize = unitHeaderBytes + targetKeyBytes +
          (keyed[last].Hashed ? Encoding.ASCII.GetByteCount(keyed[last].Name) + 1 : 0);
        if (bodySize + entrySize > blockSize - NodeHeaderBytes - ItemHeaderBytes) break;
        bodySize += entrySize;
        ++last;
      }
      if (last == first) throw new InvalidDataException("Reiser4: a directory unit exceeds node capacity.");
      var body = new byte[bodySize];
      BinaryPrimitives.WriteUInt16LittleEndian(body, checked((ushort)(last - first)));
      var unit = 2 + (last - first) * unitHeaderBytes;
      for (var i = first; i < last; ++i) {
        var entry = keyed[i];
        var header = 2 + (i - first) * unitHeaderBytes;
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header), entry.Ordering);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 8), entry.ObjectId);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(header + 16), entry.Offset);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(header + 24), checked((ushort)unit));
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit), entry.Locality << 4 | MinorStatData);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(unit + 16), entry.Target);
        unit += targetKeyBytes;
        if (entry.Hashed) {
          var bytes = Encoding.ASCII.GetBytes(entry.Name);
          bytes.CopyTo(body.AsSpan(unit));
          unit += bytes.Length + 1;
        }
      }
      var firstKey = keyed[first];
      yield return new(directory.ObjectId, MinorFileName, firstKey.Ordering, firstKey.ObjectId,
        firstKey.Offset, PluginCde40, body);
      first = last;
    }
  }

  /// <summary>
  /// Builds leaves, twig extents and pointer levels from the captured root defaults.
  /// </summary>
  internal static Layout Build(byte[] leaf, int blockSize, uint mkfsId, uint time,
                             ulong rootLocality, ulong rootObjectId, IReadOnlyList<Entry> sourceFiles,
                             IReadOnlyDictionary<string, Reiser4Reader.FileMetadata?>? savedDirectories, Func<ulong> allocate) {
    // The root's own stat data, kept as mkfs wrote it — it carries the plugin set
    // every file below it inherits, which this does not attempt to rebuild.
    var rootStatOffset = BinaryPrimitives.ReadUInt16LittleEndian(leaf[(blockSize - ItemHeaderBytes + 32)..]);
    var rootStatLength = BinaryPrimitives.ReadUInt16LittleEndian(leaf[(blockSize - 2 * ItemHeaderBytes + 32)..])
      - rootStatOffset;
    var rootStat = leaf.AsSpan(rootStatOffset, rootStatLength).ToArray();
    if (savedDirectories?.GetValueOrDefault("") is { } rootMetadata)
      rootStat = StatData(rootMetadata.Mode, rootMetadata.LinkCount, (ulong)rootMetadata.Size,
        BinaryPrimitives.ReadUInt64LittleEndian(rootMetadata.RawStatData.AsSpan(36)), time, rootMetadata);

    var rootDirectory = new DirectoryNode {
      Path = "", Name = "", ObjectId = rootObjectId, ParentObjectId = rootObjectId,
      StatLocality = rootLocality, ParentStatLocality = rootLocality,
    };
    var directories = new List<DirectoryNode> { rootDirectory };
    var byPath = new Dictionary<string, DirectoryNode>(StringComparer.Ordinal) { [""] = rootDirectory };
    var knownIds = sourceFiles.Select(static entry => entry.Metadata?.ObjectId ?? 0)
      .Concat(savedDirectories?.Values.Select(static metadata => metadata?.ObjectId ?? 0) ?? []);
    var nextObjectId = checked(Math.Max(0xFFFFUL, Math.Max(rootObjectId, knownIds.DefaultIfEmpty().Max())) + 1);
    var assignedIds = new HashSet<ulong> { rootObjectId };
    ulong ObjectId(Reiser4Reader.FileMetadata? metadata) {
      var id = metadata?.ObjectId is > 0 ? metadata.ObjectId : nextObjectId;
      if (metadata?.ObjectId is not > 0) nextObjectId = checked(nextObjectId + 1);
      if (id <= rootObjectId || !assignedIds.Add(id))
        throw new InvalidDataException("Reiser4: duplicate or reserved object identity.");
      return id;
    }
    var fileParents = new List<(Entry Source, ulong ParentObjectId, ulong ObjectId)>();

    var allSources = (savedDirectories?.Keys.Where(static path => path.Length > 0) ?? [])
      .Select(static path => new Entry { Name = path + "/", ParentObjectId = 0, ObjectId = 0, Size = 0, Runs = [] })
      .Concat(sourceFiles);
    foreach (var source in allSources) {
      var isDirectory = source.Name.EndsWith('/');
      var components = source.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
      if (components.Length == 0 || components.Any(static component => component is "." or ".." ||
          component.Contains(':') || component.Contains('\0') || component.Any(static c => c > 127)))
        throw new InvalidDataException($"Reiser4: invalid archive path '{source.Name}'.");

      var parentPath = "";
      var parent = rootDirectory;
      for (var i = 0; i < components.Length - (isDirectory ? 0 : 1); ++i) {
        var path = parentPath.Length == 0 ? components[i] : $"{parentPath}/{components[i]}";
        if (!byPath.TryGetValue(path, out var directory)) {
          if (parent.Children.Any(child => child.Name == components[i]))
            throw new InvalidDataException($"Reiser4: file/directory collision at '{path}'.");
          directory = new DirectoryNode {
            Path = path, Name = components[i], ObjectId = ObjectId(savedDirectories?.GetValueOrDefault(path)), ParentObjectId = parent.ObjectId,
            StatLocality = parent.ObjectId, ParentStatLocality = parent.StatLocality,
          };
          byPath.Add(path, directory);
          directories.Add(directory);
          parent.Children.Add((directory.Name, directory.ObjectId));
        }
        parent = directory;
        parentPath = path;
      }

      if (isDirectory) continue;
      var leafName = components[^1];
      if (parent.Children.Any(child => string.Equals(child.Name, leafName, StringComparison.Ordinal)))
        throw new InvalidDataException($"Reiser4: duplicate path or file/directory collision at '{source.Name}'.");
      var objectId = ObjectId(source.Metadata);
      parent.Children.Add((leafName, objectId));
      fileParents.Add((source with { Name = leafName }, parent.ObjectId, objectId));
    }

    var files = fileParents.Select(static file => new Entry {
      Name = file.Source.Name, ParentObjectId = file.ParentObjectId, ObjectId = file.ObjectId,
      Size = file.Source.Size, Runs = file.Source.Runs, Metadata = file.Source.Metadata,
    }).ToArray();

    var rootItems = Directory(rootDirectory, blockSize).ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(rootStat.AsSpan(4), checked(3U + (uint)directories.Count(directory => directory != rootDirectory && directory.ParentObjectId == rootObjectId)));
    BinaryPrimitives.WriteUInt64LittleEndian(rootStat.AsSpan(8), (ulong)rootDirectory.Children.Count + 2);
    BinaryPrimitives.WriteUInt64LittleEndian(rootStat.AsSpan(36), (ulong)rootItems.Sum(static item => item.Body.Length - 2));
    var bodies = new List<Item> {
      new(rootLocality, MinorStatData, 0, rootObjectId, 0, PluginStat40, rootStat),
    };
    bodies.AddRange(rootItems);

    foreach (var directory in directories.Skip(1)) {
      var directoryItems = Directory(directory, blockSize).ToArray();
      bodies.Add(new(directory.StatLocality, MinorStatData, 0, directory.ObjectId, 0, PluginStat40,
        StatData(0x41ED, checked(2U + (uint)directories.Count(child => child.ParentObjectId == directory.ObjectId)), (ulong)directory.Children.Count + 2, (ulong)directoryItems.Sum(static item => item.Body.Length - 2), time,
          savedDirectories?.GetValueOrDefault(directory.Path))));
      bodies.AddRange(directoryItems);
    }

    foreach (var file in files) {
      var held = file.Runs.Aggregate(0UL, static (sum, run) => checked(sum + run.Width));
      bodies.Add(new(file.ParentObjectId, MinorStatData, 0, file.ObjectId, 0, PluginStat40,
        StatData(0x81A4, 1, (ulong)file.Size, held * (ulong)blockSize, time, file.Metadata)));
      if (file.Runs.Count == 0) continue;
      var runsPerItem = (blockSize - NodeHeaderBytes - ItemHeaderBytes) / 16;
      ulong logicalOffset = 0;
      for (var firstRun = 0; firstRun < file.Runs.Count; firstRun += runsPerItem) {
        var count = Math.Min(runsPerItem, file.Runs.Count - firstRun);
        var extents = new byte[count * 16];
        for (var i = 0; i < count; ++i) {
          var run = file.Runs[firstRun + i];
          BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16), run.Start);
          BinaryPrimitives.WriteUInt64LittleEndian(extents.AsSpan(i * 16 + 8), run.Width);
        }
        bodies.Add(new(file.ParentObjectId, MinorFileBody, 0, file.ObjectId, logicalOffset, PluginExtent40, extents));
        for (var i = 0; i < count; ++i)
          logicalOffset = checked(logicalOffset + file.Runs[firstRun + i].Width * (ulong)blockSize);
      }
    }

    bodies.Sort(static (a, b) => {
      var c = (a.Locality << 4 | a.Minor).CompareTo(b.Locality << 4 | b.Minor);
      if (c != 0) return c;
      c = a.Ordering.CompareTo(b.Ordering);
      if (c != 0) return c;
      c = a.ObjectId.CompareTo(b.ObjectId);
      return c != 0 ? c : a.Offset.CompareTo(b.Offset);
    });

    var nodes = new Dictionary<ulong, byte[]>();
    var twigItems = new List<Item>();
    var pending = new List<Item>();
    var firstLeaf = true;
    void FlushLeaves() {
      foreach (var group in Pack(pending, blockSize)) {
        var block = firstLeaf ? 24UL : allocate();
        firstLeaf = false;
        nodes.Add(block, EncodeNode(group, blockSize, mkfsId, 1));
        twigItems.Add(group[0] with { Plugin = 3, Body = PointerBody(block) });
      }
      pending.Clear();
    }
    // A leaf cannot straddle a key range occupied by a twig extent item.
    // End the leaf run before that extent, then resume after it.
    foreach (var item in bodies) {
      if (item.Plugin == PluginExtent40) {
        FlushLeaves();
        twigItems.Add(item);
      } else pending.Add(item);
    }
    FlushLeaves();
    byte level = 2;
    var parentItems = twigItems;
    while (true) {
      var groups = Pack(parentItems, blockSize).ToList();
      if (groups.Count == 1) {
        nodes.Add(23, EncodeNode(groups[0], blockSize, mkfsId, level));
        return new(nextObjectId, (ulong)assignedIds.Count, level, nodes);
      }
      parentItems = [];
      foreach (var group in groups) {
        var block = allocate();
        nodes.Add(block, EncodeNode(group, blockSize, mkfsId, level));
        parentItems.Add(group[0] with { Plugin = 3, Body = PointerBody(block) });
      }
      level = checked((byte)(level + 1));
    }
  }

  private static byte[] PointerBody(ulong block) {
    var body = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(body, block);
    return body;
  }

  private static IEnumerable<List<Item>> Pack(IReadOnlyList<Item> items, int blockSize) {
    var group = new List<Item>();
    var bytes = NodeHeaderBytes;
    foreach (var item in items) {
      var need = checked(item.Body.Length + ItemHeaderBytes);
      if (need > blockSize - NodeHeaderBytes)
        throw new InvalidDataException("Reiser4: an indivisible item exceeds node capacity.");
      if (bytes + need > blockSize) {
        yield return group;
        group = [];
        bytes = NodeHeaderBytes;
      }
      group.Add(item);
      bytes += need;
    }
    if (group.Count > 0) yield return group;
  }

  private static byte[] EncodeNode(IReadOnlyList<Item> items, int blockSize, uint mkfsId, byte level) {
    var node = new byte[blockSize];
    var at = NodeHeaderBytes;
    for (var i = 0; i < items.Count; ++i) {
      var item = items[i];
      item.Body.CopyTo(node.AsSpan(at));
      var header = blockSize - (i + 1) * ItemHeaderBytes;
      WriteKey(node.AsSpan(header), item.Locality, item.Minor, item.Ordering, item.ObjectId, item.Offset);
      BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(header + 32), checked((ushort)at));
      BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(header + 36), checked((ushort)item.Plugin));
      at += item.Body.Length;
    }
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2), checked((ushort)items.Count));
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(4), checked((ushort)(blockSize - items.Count * ItemHeaderBytes - at)));
    BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(6), checked((ushort)at));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8), unchecked((uint)NodeMagic));
    BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(12), mkfsId);
    node[26] = level;
    return node;
  }
}
