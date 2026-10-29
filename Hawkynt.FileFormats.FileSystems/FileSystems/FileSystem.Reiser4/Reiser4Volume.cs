#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>Item plugin identifiers (plugin type 1).</summary>
internal static class Reiser4ItemPlugin {
  internal const ushort StatData = 0;
  internal const ushort DirEntries = 2;
  internal const ushort NodePointer = 3;
  internal const ushort Extent = 5;
  internal const ushort Tail = 6;
  internal const ushort CompressedTail = 7;
}

/// <summary>Plugin-set member slots (the <c>sdext_plugin_set</c> member numbers).</summary>
internal static class Reiser4PsetMember {
  internal const ushort File = 0;
  internal const ushort Directory = 1;
  internal const ushort Formatting = 3;
  internal const ushort Hash = 4;
  internal const ushort Fibration = 5;
  internal const ushort Cipher = 8;
  internal const ushort Compression = 10;
  internal const ushort CompressionMode = 11;
  internal const ushort Cluster = 12;
  internal const ushort Create = 13;
}

/// <summary>One item as it sits in a node: its key, plugin and where its body lives.</summary>
internal sealed record Reiser4ItemRef(Reiser4Key Key, ushort Plugin, ushort Flags, ulong Block, int BodyOffset, int BodyLength);

/// <summary>One directory entry: the three key words its name produced, the name and its target.</summary>
internal sealed record Reiser4DirEntry(ulong Ordering, ulong ObjectId, ulong Offset, byte[] Name, Reiser4Key Target) {
  internal bool Hashed => (this.Ordering & Reiser4NameKeys.LongNameBit) != 0;
  internal bool IsDot => this.Name is [(byte)'.'];
  internal bool IsDotDot => this.Name is [(byte)'.', (byte)'.'];
}

/// <summary>
/// The stat data of an object, parsed only as far as the writer needs it; the raw body is always
/// kept so a rebuild can carry every extension byte for byte.
/// </summary>
internal sealed class Reiser4Stat {

  internal byte[] Raw { get; }
  internal ushort Mask => BinaryPrimitives.ReadUInt16LittleEndian(this.Raw);
  internal ushort Mode => BinaryPrimitives.ReadUInt16LittleEndian(this.Raw.AsSpan(2));
  internal uint Links => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(4));
  internal ulong Size => BinaryPrimitives.ReadUInt64LittleEndian(this.Raw.AsSpan(8));
  internal uint UserId => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(16));
  internal uint GroupId => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(20));
  internal uint AccessTime => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(24));
  internal uint ModifyTime => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(28));
  internal uint ChangeTime => BinaryPrimitives.ReadUInt32LittleEndian(this.Raw.AsSpan(32));
  /// <summary>Allocated bytes for files and directories; the device number for special files.</summary>
  internal ulong BytesOrDevice => BinaryPrimitives.ReadUInt64LittleEndian(this.Raw.AsSpan(36));

  /// <summary>The object's own plugin-set members, member → plugin id.</summary>
  internal IReadOnlyDictionary<ushort, ushort> PluginSet { get; }

  /// <summary>Byte range of the plugin-set extension, or (-1, 0) when there is none.</summary>
  internal (int Start, int Length) PluginSetRange { get; } = (-1, 0);

  /// <summary>The symlink target the symlink extension holds.</summary>
  internal byte[]? SymlinkTarget { get; }

  internal const int FileType = 0xF000;
  internal const int Directory = 0x4000;
  internal const int Regular = 0x8000;
  internal const int Symlink = 0xA000;
  internal const int CharDevice = 0x2000;
  internal const int BlockDevice = 0x6000;
  internal const int Fifo = 0x1000;
  internal const int Socket = 0xC000;

  private Reiser4Stat(byte[] raw, Dictionary<ushort, ushort> pset, (int, int) psetRange, byte[]? symlink) {
    this.Raw = raw;
    this.PluginSet = pset;
    this.PluginSetRange = psetRange;
    this.SymlinkTarget = symlink;
  }

  /// <summary>
  /// Parses a stat-data body, or returns why it is outside what this profile carries: an
  /// extension nobody verified (crypto, a chained mask) or bytes the mask does not declare.
  /// </summary>
  internal static Reiser4Stat? Parse(ReadOnlySpan<byte> body, out string? problem) {
    problem = null;
    if (body.Length < 2) { problem = "the stat data has no extension mask"; return null; }
    var mask = BinaryPrimitives.ReadUInt16LittleEndian(body);
    if ((mask & 3) != 3) { problem = "the stat data lacks the light-weight or Unix extension"; return null; }
    var at = 2;
    var pset = new Dictionary<ushort, ushort>();
    (int, int) psetRange = (-1, 0);
    byte[]? symlink = null;
    for (var bit = 0; bit < 16; ++bit) {
      if ((mask & 1 << bit) == 0) continue;
      int length;
      switch (bit) {
        case 0: length = 14; break;
        case 1: length = 28; break;
        case 2: length = 12; break;
        case 3: {
          // Symlink: the target, as long as the light-weight extension's size says, then a NUL.
          var size = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);
          if (size > 4096) { problem = "a symlink target is longer than a page"; return null; }
          length = (int)size + 1;
          if (at + length > body.Length) { problem = "the symlink extension is truncated"; return null; }
          symlink = body.Slice(at, (int)size).ToArray();
          break;
        }
        case 5: length = 4; break;
        case 4 or 8: {
          if (at + 2 > body.Length) { problem = "a plugin-set extension is truncated"; return null; }
          var count = BinaryPrimitives.ReadUInt16LittleEndian(body[at..]);
          length = 2 + 4 * count;
          if (at + length > body.Length) { problem = $"stat-data extension {bit} is truncated"; return null; }
          if (bit == 4) {
            psetRange = (at, length);
            for (var i = 0; i < count; ++i)
              pset[BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 2 + 4 * i)..])] =
                BinaryPrimitives.ReadUInt16LittleEndian(body[(at + 4 + 4 * i)..]);
          }
          break;
        }
        default: problem = $"stat-data extension {bit} is not supported"; return null;
      }
      if (at + length > body.Length) { problem = $"stat-data extension {bit} is truncated"; return null; }
      at += length;
    }
    if (at != body.Length) { problem = "the stat data holds bytes its extension mask does not declare"; return null; }
    if (pset.TryGetValue(Reiser4PsetMember.Cipher, out var cipher) && cipher != 0) {
      problem = "the object is encrypted";
      return null;
    }
    return new Reiser4Stat(body.ToArray(), pset, psetRange, symlink);
  }

  internal int Kind => this.Mode & FileType;
}

/// <summary>One object of the volume: its stat data, its body items and, for a directory, its entries.</summary>
internal sealed class Reiser4Object {
  internal required Reiser4Key StatKey { get; init; }
  internal required Reiser4Stat Stat { get; init; }
  internal List<Reiser4ItemRef> Body { get; } = [];
  internal List<Reiser4DirEntry> Entries { get; } = [];
  internal ulong ObjectId => this.StatKey.ObjectId;
  internal bool IsDirectory => this.Stat.Kind == Reiser4Stat.Directory;
}

/// <summary>
/// A Reiser4 volume read whole: the fixed blocks after journal replay, the root's plugin profile
/// and every object the node40/node41 tree holds, resolved into a namespace.
/// </summary>
/// <remarks>
/// The layout this reads — and the reasons it refuses what it does not — is written down in
/// <c>docs/REISER4-NATIVE-PROFILE.md</c>.
/// </remarks>
internal sealed class Reiser4Volume : IDisposable {

  internal const int BlockSize = 4096;
  internal const long MasterOffset = 65536;
  internal const ulong RootLocality = 0x29;
  internal const ulong RootObjectId = 0x2a;
  internal static readonly Reiser4Key RootStatKey = Reiser4Key.Of(RootLocality, Reiser4Key.MinorStatData, 0, RootObjectId, 0);

  private static readonly byte[] MasterMagic = "ReIsEr4"u8.ToArray();
  private static readonly byte[] FormatMagic = "ReIsEr40FoRmAt"u8.ToArray();
  private const uint NodeMagic = 0x52344653;
  /// <summary>The magic a node41 (checksummed) node carries instead.</summary>
  internal const uint Node41Magic = 0x19051966;
  private const int ItemHeaderBytes = 38;

  private readonly ImageAccessor _image;
  private Dictionary<ulong, byte[]> _replayed = [];

  internal bool Valid { get; }
  internal string Label { get; } = "";
  internal string UuidHex { get; } = "";
  internal int VolumeBlockSize { get; } = BlockSize;
  internal long Length => this._image.Length;
  internal ulong BlockCount => (ulong)(this._image.Length / BlockSize);

  /// <summary>True when the whole tree was read and every object is one this profile carries.</summary>
  internal bool TreeValid { get; private set; }

  /// <summary>Why the tree was not read, when it was not.</summary>
  internal string? Problem { get; private set; }

  /// <summary>Number of journal transactions replayed before the tree was read.</summary>
  internal int ReplayedTransactions { get; private set; }

  /// <summary>Node plugin the format superblock names: 0 node40, 1 node41 (checksummed).</summary>
  internal ushort NodePlugin { get; private set; }
  internal int NodeHeaderBytes => this.NodePlugin == 1 ? 32 : 28;

  internal ushort TreeHeight { get; private set; }

  internal Dictionary<Reiser4Key, Reiser4Object> Objects { get; } = [];
  internal Reiser4Object? Root { get; private set; }
  internal List<ulong> NodeBlocks { get; } = [];

  /// <summary>Every path the namespace resolves, in walk order, with the object each one names.</summary>
  internal List<(string Path, byte[] Name, Reiser4Object Object, Reiser4Object Parent)> Paths { get; } = [];

  internal Reiser4Volume(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream, leaveOpen);
    if (this._image.Length < MasterOffset + BlockSize) return;
    var master = this._image.Read(MasterOffset, BlockSize);
    if (!master.AsSpan(0, MasterMagic.Length).SequenceEqual(MasterMagic)) return;
    this.Valid = true;
    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(18, 2));
    if (blockSize is >= 512 and <= 8192) this.VolumeBlockSize = blockSize;
    this.UuidHex = Convert.ToHexString(master.AsSpan(20, 16));
    var label = master.AsSpan(36, 16);
    var nul = label.IndexOf((byte)0);
    this.Label = Encoding.UTF8.GetString(nul < 0 ? label : label[..nul]);
    try {
      this.Problem = this.Load();
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or OverflowException or ArgumentException or IndexOutOfRangeException) {
      this.Problem = ex.Message;
    }
    this.TreeValid = this.Problem is null;
  }

  /// <summary>A block as the volume holds it once the journal is replayed.</summary>
  internal byte[] ReadBlock(ulong block) {
    if (this._replayed.TryGetValue(block, out var replayed)) return replayed.ToArray();
    return this._image.Read(checked((long)block * BlockSize), BlockSize);
  }

  /// <summary>Bytes of an item body.</summary>
  internal byte[] ReadBody(Reiser4ItemRef item) => this.ReadBlock(item.Block).AsSpan(item.BodyOffset, item.BodyLength).ToArray();

  /// <summary>Copies whole blocks from the volume, journal replay included.</summary>
  internal void CopyBlocks(ulong start, ulong count, Stream destination, long byteLimit) {
    for (ulong i = 0; i < count && byteLimit > 0; ++i) {
      var block = start + i;
      var take = (int)Math.Min(BlockSize, byteLimit);
      if (this._replayed.TryGetValue(block, out var replayed)) destination.Write(replayed, 0, take);
      else this._image.CopyTo(checked((long)block * BlockSize), destination, take);
      byteLimit -= take;
    }
  }

  private string? Load() {
    if (this.VolumeBlockSize != BlockSize) return "only 4096-byte blocks are read natively";
    if (this.BlockCount <= 25) return "the image is too small for a format40 volume";
    var replay = Reiser4Journal.Read(block => this._image.Read(checked((long)block * BlockSize), BlockSize), this.BlockCount, out var journalProblem);
    if (replay is null) return journalProblem;
    this._replayed = replay.Blocks.ToDictionary(static pair => pair.Key, static pair => pair.Value);
    this.ReplayedTransactions = replay.Transactions;

    var format = this.ReadBlock(17);
    if (!format.AsSpan(52, FormatMagic.Length).SequenceEqual(FormatMagic)) return "the format40 superblock is missing";
    var flags = BinaryPrimitives.ReadUInt64LittleEndian(format.AsSpan(72));
    if ((flags & 1) == 0) return "the volume uses short keys";
    this.NodePlugin = (ushort)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(84));
    if (this.NodePlugin > 1) return $"node plugin {this.NodePlugin} is not supported";
    var root = BinaryPrimitives.ReadUInt64LittleEndian(format.AsSpan(16));
    this.TreeHeight = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(68));
    if (root == 0 || root >= this.BlockCount) return "the tree root lies outside the volume";

    var items = new List<Reiser4ItemRef>();
    var visited = new HashSet<ulong>();
    if (this.Walk(root, 0, visited, items) is { } walkProblem) return walkProblem;
    this.NodeBlocks.AddRange(visited);
    return this.Assemble(items);
  }

  private string? Walk(ulong block, int expectedLevel, HashSet<ulong> visited, List<Reiser4ItemRef> items) {
    if (block < 23 || block >= this.BlockCount) return $"a tree pointer names block {block}, outside the volume";
    if (!visited.Add(block)) return $"node {block} is reached twice";
    if (visited.Count > 16_000_000) return "the tree is larger than this reader walks";
    var node = this.ReadBlock(block);
    var plugin = BinaryPrimitives.ReadUInt16LittleEndian(node);
    if (plugin != this.NodePlugin || BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(8)) != (this.NodePlugin == 1 ? Node41Magic : NodeMagic))
      return $"block {block} is not a formatted node of the volume's node plugin";
    if (this.NodePlugin == 1 && NodeChecksum(node) != BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(28)))
      return $"node {block} fails its checksum";
    var level = node[26];
    if (level == 0 || expectedLevel != 0 && level != expectedLevel) return $"node {block} sits at the wrong tree level";
    var count = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2));
    var bodiesEnd = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(6));
    if (count == 0 || count > (BlockSize - this.NodeHeaderBytes) / ItemHeaderBytes ||
        bodiesEnd < this.NodeHeaderBytes || bodiesEnd > BlockSize - count * ItemHeaderBytes)
      return $"node {block} has a malformed header";
    var headers = new (Reiser4Key Key, ushort Offset, ushort Flags, ushort Plugin)[count];
    for (var i = 0; i < count; ++i) {
      var at = BlockSize - (i + 1) * ItemHeaderBytes;
      headers[i] = (Reiser4Key.Read(node.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 32)),
        BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 34)), BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(at + 36)));
      if (headers[i].Offset < this.NodeHeaderBytes || headers[i].Offset > bodiesEnd) return $"node {block} has an item outside its body area";
      if (i > 0 && headers[i].Key.CompareTo(headers[i - 1].Key) < 0) return $"node {block} holds items out of key order";
    }
    var ends = headers.Select(static h => (int)h.Offset).Append(bodiesEnd).Distinct().Order().ToArray();
    var children = new List<ulong>();
    for (var i = 0; i < count; ++i) {
      var (key, offset, itemFlags, itemPlugin) = headers[i];
      var end = ends[Array.IndexOf(ends, offset) + 1];
      var length = end - offset;
      if (level > 1 && itemPlugin == Reiser4ItemPlugin.NodePointer) {
        if (length != 8) return $"node {block} holds a malformed node pointer";
        children.Add(BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(offset)));
        continue;
      }
      // Extents live at the twig level (the earliest workbench images also put them in leaves);
      // every other item lives in a leaf.
      if (level > 2 || level == 2 && itemPlugin != Reiser4ItemPlugin.Extent)
        return $"node {block} holds item plugin {itemPlugin} at level {level}";
      items.Add(new Reiser4ItemRef(key, itemPlugin, itemFlags, block, offset, length));
    }
    if (level == 1 && children.Count > 0) return $"leaf {block} holds a node pointer";
    foreach (var child in children)
      if (this.Walk(child, level - 1, visited, items) is { } problem) return problem;
    return null;
  }

  /// <summary>The CRC-32C node41 keeps at byte 28: over the node40 header and everything after the checksum.</summary>
  internal static uint NodeChecksum(ReadOnlySpan<byte> node) {
    var crc = Crc32CUpdate(0xFFFF_FFFFu, node[..28]);
    return Crc32CUpdate(crc, node[32..]);
  }

  private static readonly uint[] Crc32CTable = BuildCrc32CTable();

  private static uint[] BuildCrc32CTable() {
    var table = new uint[256];
    for (var n = 0u; n < 256; ++n) {
      var c = n;
      for (var k = 0; k < 8; ++k) c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
      table[n] = c;
    }
    return table;
  }

  /// <summary>The CRC-32C register advanced over <paramref name="data" />, without the final inversion.</summary>
  private static uint Crc32CUpdate(uint crc, ReadOnlySpan<byte> data) {
    foreach (var b in data) crc = Crc32CTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return crc;
  }

  private string? Assemble(List<Reiser4ItemRef> items) {
    var directoryItems = new Dictionary<ulong, List<Reiser4ItemRef>>();
    foreach (var item in items) {
      var key = item.Key;
      switch (key.Minor) {
        case Reiser4Key.MinorStatData when item.Plugin == Reiser4ItemPlugin.StatData && key.Offset == 0: {
          var stat = Reiser4Stat.Parse(this.ReadBody(item), out var problem);
          if (stat is null) return problem;
          if (!this.Objects.TryAdd(key, new Reiser4Object { StatKey = key, Stat = stat })) return "an object has two stat data";
          break;
        }
        case Reiser4Key.MinorName when item.Plugin == Reiser4ItemPlugin.DirEntries:
          if (!directoryItems.TryGetValue(key.Locality, out var list)) directoryItems[key.Locality] = list = [];
          list.Add(item);
          break;
        case Reiser4Key.MinorBody when item.Plugin is Reiser4ItemPlugin.Extent or Reiser4ItemPlugin.Tail or Reiser4ItemPlugin.CompressedTail:
          break;
        default:
          return $"item plugin {item.Plugin} with key type {key.Minor} is not supported";
      }
    }
    foreach (var item in items.Where(static item => item.Key.Minor == Reiser4Key.MinorBody)) {
      if (!this.Objects.TryGetValue(item.Key.StatKey, out var owner)) return "a file body has no stat data";
      if (owner.Stat.Kind != Reiser4Stat.Regular) return "a body item belongs to an object that is not a regular file";
      owner.Body.Add(item);
    }
    foreach (var owner in this.Objects.Values) {
      owner.Body.Sort(static (a, b) => a.Key.Offset.CompareTo(b.Key.Offset));
      if (this.BodyProblem(owner) is { } bodyProblem) return bodyProblem;
    }

    var byObjectId = this.Objects.Values.Where(static o => o.IsDirectory).GroupBy(static o => o.ObjectId)
      .ToDictionary(static g => g.Key, static g => g.ToList());
    foreach (var (directoryId, list) in directoryItems) {
      if (!byObjectId.TryGetValue(directoryId, out var owners) || owners.Count != 1) return "directory entries have no directory";
      foreach (var item in list) {
        var body = this.ReadBody(item);
        if (DecodeEntries(body, item.Key, owners[0].Entries) is { } entryProblem) return entryProblem;
      }
    }

    if (!this.Objects.TryGetValue(RootStatKey, out var rootObject) || !rootObject.IsDirectory) return "the root directory is missing";
    this.Root = rootObject;
    var reached = new HashSet<Reiser4Key> { RootStatKey };
    var walked = new HashSet<Reiser4Key>();
    if (this.WalkNamespace(rootObject, "", 0, reached, walked) is { } nsProblem) return nsProblem;
    if (reached.Count != this.Objects.Count) return "the tree holds objects no directory names";
    return null;
  }

  private string? BodyProblem(Reiser4Object owner) {
    if (owner.Body.Count == 0) return null;
    var plugins = owner.Body.Select(static item => item.Plugin).Distinct().ToArray();
    if (plugins.Contains(Reiser4ItemPlugin.CompressedTail) && plugins.Length > 1)
      return "a file mixes compressed and plain body items";
    if (plugins.Contains(Reiser4ItemPlugin.CompressedTail)) {
      foreach (var item in owner.Body)
        if (item.BodyLength < 2) return "a compressed body item is empty";
      return null;
    }
    ulong expected = 0;
    foreach (var item in owner.Body) {
      if (item.Key.Offset != expected) return "a file body has a gap or an overlap between items";
      if (item.Plugin == Reiser4ItemPlugin.Tail) { expected += (ulong)item.BodyLength; continue; }
      if (item.BodyLength == 0 || item.BodyLength % 16 != 0) return "an extent item is malformed";
      var body = this.ReadBody(item);
      for (var at = 0; at < body.Length; at += 16) {
        var start = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
        var width = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at + 8));
        if (width == 0 || start == 1 || start != 0 && (start < 25 || start >= this.BlockCount || width > this.BlockCount - start))
          return "an extent unit lies outside the volume or is unallocated";
        expected = checked(expected + width * BlockSize);
      }
    }
    return null;
  }

  private static string? DecodeEntries(byte[] body, Reiser4Key itemKey, List<Reiser4DirEntry> into) {
    if (body.Length < 2) return "a directory item is truncated";
    var count = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var unitsStart = 2 + count * 26;
    if (count == 0 || unitsStart > body.Length) return "a directory item is truncated";
    for (var i = 0; i < count; ++i) {
      var header = 2 + i * 26;
      var ordering = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(header));
      var objectId = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(header + 8));
      var offset = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(header + 16));
      int unit = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(header + 24));
      int unitEnd = i + 1 < count ? BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(header + 26 + 24)) : body.Length;
      if (unit < unitsStart || unitEnd < unit + 24 || unitEnd > body.Length) return "a directory unit lies outside its item";
      if (i == 0 && (ordering != itemKey.Ordering || objectId != itemKey.ObjectId || offset != itemKey.Offset))
        return "a directory item's key is not its first entry's";
      var targetWord0 = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(unit));
      var target = Reiser4Key.Of(targetWord0 >> 4, Reiser4Key.MinorStatData,
        BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(unit + 8)), BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(unit + 16)), 0);
      byte[] name;
      if ((ordering & Reiser4NameKeys.LongNameBit) != 0) {
        var stored = body.AsSpan(unit + 24, unitEnd - unit - 24);
        var nul = stored.IndexOf((byte)0);
        if (nul <= 0) return "a long directory entry has no stored name";
        name = stored[..nul].ToArray();
      } else {
        name = Reiser4NameKeys.Unpack(ordering, objectId, offset) ?? [];
        if (unitEnd != unit + 24) return "a short directory entry carries a stored name";
      }
      if (name.Length == 0 || name.AsSpan().IndexOf((byte)'/') >= 0) return "a directory entry has an invalid name";
      into.Add(new Reiser4DirEntry(ordering, objectId, offset, name, target));
    }
    return null;
  }

  private string? WalkNamespace(Reiser4Object directory, string path, int depth, HashSet<Reiser4Key> reached, HashSet<Reiser4Key> walked) {
    if (depth > 128) return "the namespace is deeper than this reader walks";
    if (!walked.Add(directory.StatKey)) return "a directory is reached through two paths";
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var entry in directory.Entries) {
      if (entry.IsDot || entry.IsDotDot) continue;
      if (!this.Objects.TryGetValue(entry.Target, out var target)) return "a directory entry names a missing object";
      var name = DecodeName(entry.Name);
      if (!names.Add(name)) return "a directory holds the same name twice";
      var full = path.Length == 0 ? name : path + "/" + name;
      this.Paths.Add((full, entry.Name, target, directory));
      reached.Add(entry.Target);
      if (target.IsDirectory && this.WalkNamespace(target, full, depth + 1, reached, walked) is { } problem) return problem;
    }
    return null;
  }

  /// <summary>Names are bytes; they are shown as UTF-8 where they are valid UTF-8 and as Latin-1 otherwise.</summary>
  internal static string DecodeName(byte[] name) {
    try {
      return new UTF8Encoding(false, true).GetString(name);
    } catch (DecoderFallbackException) {
      return Encoding.Latin1.GetString(name);
    }
  }

  // ── Profile ────────────────────────────────────────────────────────────

  /// <summary>A plugin-set member as an object inherits it: its own, else the root's, else the kernel default.</summary>
  internal ushort Plugin(Reiser4Object? owner, ushort member) {
    if (owner is not null && owner.Stat.PluginSet.TryGetValue(member, out var own)) return own;
    if (this.Root is { } root && root.Stat.PluginSet.TryGetValue(member, out var inherited)) return inherited;
    return member switch {
      Reiser4PsetMember.Formatting => 2,
      Reiser4PsetMember.Hash => (ushort)Reiser4HashId.R5,
      Reiser4PsetMember.Fibration => (ushort)Reiser4FibrationId.Extension1,
      Reiser4PsetMember.CompressionMode => (ushort)Reiser4CompressionModeId.Convert,
      _ => 0,
    };
  }

  // ── File bodies ────────────────────────────────────────────────────────

  /// <summary>The extent units of a file, in logical order: (start, width), start 0 being a hole.</summary>
  internal IEnumerable<(ulong Start, ulong Width)> ExtentUnits(Reiser4Object file) {
    foreach (var item in file.Body.Where(static i => i.Plugin == Reiser4ItemPlugin.Extent)) {
      var body = this.ReadBody(item);
      for (var at = 0; at < body.Length; at += 16)
        yield return (BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at)), BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at + 8)));
    }
  }

  /// <summary>Writes a regular file's logical bytes, holes as zeros, into <paramref name="destination" />.</summary>
  internal long ExtractTo(Reiser4Object file, Stream destination) {
    var size = checked((long)file.Stat.Size);
    if (file.Stat.Kind != Reiser4Stat.Regular || size == 0) return 0;
    long written = 0;
    void Zeros(long count) {
      Span<byte> zero = stackalloc byte[4096];
      zero.Clear();
      while (count > 0) {
        var n = (int)Math.Min(zero.Length, count);
        destination.Write(zero[..n]);
        count -= n;
        written += n;
      }
    }
    if (file.Body.Count > 0 && file.Body[0].Plugin == Reiser4ItemPlugin.CompressedTail) {
      foreach (var (start, bytes) in this.Clusters(file)) {
        if (start >= size) break;
        if (start > written) Zeros(start - written);
        var take = (int)Math.Min(bytes.Length, size - written);
        destination.Write(bytes, 0, take);
        written += take;
      }
      if (written < size) Zeros(size - written);
      return written;
    }
    foreach (var item in file.Body) {
      if (written >= size) break;
      if (item.Plugin == Reiser4ItemPlugin.Tail) {
        var bytes = this.ReadBody(item);
        var take = (int)Math.Min(bytes.Length, size - written);
        destination.Write(bytes, 0, take);
        written += take;
        continue;
      }
      var body = this.ReadBody(item);
      for (var at = 0; at < body.Length && written < size; at += 16) {
        var start = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at));
        var width = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at + 8));
        var bytes = (long)Math.Min((ulong)(size - written), width * BlockSize);
        if (start == 0) { Zeros(bytes); continue; }
        this.CopyBlocks(start, width, destination, bytes);
        written += bytes;
      }
    }
    if (written < size) Zeros(size - written);
    return written;
  }

  /// <summary>The cluster shift of a cryptcompress file, from its body items.</summary>
  internal int ClusterShift(Reiser4Object file) => this.ReadBlock(file.Body[0].Block)[file.Body[0].BodyOffset];

  /// <summary>
  /// Each present logical cluster of a cryptcompress file: its start and its decoded bytes. Absent
  /// clusters are holes.
  /// </summary>
  internal IEnumerable<(long Start, byte[] Bytes)> Clusters(Reiser4Object file) {
    var size = (long)file.Stat.Size;
    var compression = (Reiser4CompressionId)this.Plugin(file, Reiser4PsetMember.Compression);
    foreach (var (index, shift, disk) in this.DiskClusters(file)) {
      var start = index << shift;
      if (start >= size) yield break;
      var logical = (int)Math.Min(1L << shift, size - start);
      yield return (start, Reiser4Clusters.Decode(disk, logical, compression));
    }
  }

  /// <summary>Each disk cluster of a cryptcompress file as stored: index, shift and bytes.</summary>
  internal IEnumerable<(long Index, int Shift, byte[] Disk)> DiskClusters(Reiser4Object file) {
    using var current = new MemoryStream();
    long index = -1;
    var shift = 0;
    ulong expected = 0;
    foreach (var item in file.Body) {
      var body = this.ReadBody(item);
      var itemShift = body[0];
      if (itemShift is < 12 or > 16) throw new InvalidDataException("Reiser4: a compressed body item names an unsupported cluster size.");
      var itemIndex = (long)(item.Key.Offset >> itemShift);
      if (itemIndex != index) {
        if (index >= 0) yield return (index, shift, current.ToArray());
        if (item.Key.Offset != (ulong)itemIndex << itemShift)
          throw new InvalidDataException("Reiser4: a disk cluster does not start at its cluster's offset.");
        current.SetLength(0);
        index = itemIndex;
        shift = itemShift;
        expected = item.Key.Offset;
      } else if (itemShift != shift || item.Key.Offset != expected)
        throw new InvalidDataException("Reiser4: a disk cluster's items are not contiguous.");
      current.Write(body, 1, body.Length - 1);
      expected += (ulong)(body.Length - 1);
    }
    if (index >= 0) yield return (index, shift, current.ToArray());
  }

  /// <summary>Physical byte runs that hold a regular file's extent data.</summary>
  internal IEnumerable<(long Offset, long Length)> DataRuns(Reiser4Object file) {
    var remaining = (long)file.Stat.Size;
    foreach (var (start, width) in this.ExtentUnits(file)) {
      if (remaining <= 0) yield break;
      var bytes = (long)Math.Min((ulong)remaining, width * BlockSize);
      if (start != 0) yield return ((long)start * BlockSize, bytes);
      remaining -= bytes;
    }
  }

  public void Dispose() => this._image.Dispose();

  /// <summary>Recomputes a node41 checksum in place.</summary>
  internal static void SealNode41(Span<byte> node) => BinaryPrimitives.WriteUInt32LittleEndian(node[28..], NodeChecksum(node));

  /// <summary>Recomputes a bitmap block's leading adler32.</summary>
  internal static void SealBitmap(Span<byte> bitmap) => BinaryPrimitives.WriteUInt32LittleEndian(bitmap, Adler32.Compute(bitmap[4..]));
}
