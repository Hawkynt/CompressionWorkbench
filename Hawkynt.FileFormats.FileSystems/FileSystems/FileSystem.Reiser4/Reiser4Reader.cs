#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Reiser4;

/// <summary>
/// Reads a Reiser4 image: the master superblock's label, UUID and block size,
/// plus regular files represented by the native stat40/cde40/extent40 items
/// emitted by <see cref="Reiser4Writer"/>. The older workbench payload directory
/// remains a compatibility fallback for images written before native tree items
/// became authoritative.
/// </summary>
public sealed class Reiser4Reader : IDisposable {

  /// <summary>Byte offset of the master superblock: block 16 at a 4 KB block size.</summary>
  public const long MasterOffset = 65536;

  private const int ItemHeaderBytes = 38;
  private const int DirectoryUnitHeaderBytes = 26;
  private const int TargetKeyBytes = 24;
  private const ushort PluginStat40 = 0;
  private const ushort PluginCde40 = 2;
  private const ushort PluginExtent40 = 5;
  private const byte MinorStatData = 1;
  private const byte MinorFileBody = 4;
  private const ulong HashedNameBit = 0x0100000000000000;

  private static readonly byte[] MasterMagic = "ReIsEr4"u8.ToArray();

  private readonly ImageAccessor _image;
  private readonly List<Entry> _entries = [];
  private readonly HashSet<string> _nativeNames = new(StringComparer.Ordinal);

  /// <summary>True when the image carries a valid Reiser4 master superblock.</summary>
  public bool Valid { get; }

  /// <summary>Filesystem block size from the master superblock.</summary>
  public int BlockSize { get; } = Reiser4Writer.BlockSize;

  /// <summary>Volume label from the master superblock.</summary>
  public string Label { get; } = "";

  /// <summary>Volume UUID from the master superblock, as hex.</summary>
  public string UuidHex { get; } = "";

  /// <summary>Files and directories found in the native tree, or files in the legacy payload directory.</summary>
  public IReadOnlyList<Entry> Entries => this._entries;

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._image.Length;

  /// <summary>
  /// Initializes a new instance of <see cref="Reiser4Reader"/>.
  /// </summary>
  public Reiser4Reader(Stream stream, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    this._image = new ImageAccessor(stream, leaveOpen);
    if (this._image.Length < MasterOffset + Reiser4Writer.BlockSize) return;

    var master = this._image.Read(MasterOffset, Reiser4Writer.BlockSize);
    if (!master.AsSpan(0, MasterMagic.Length).SequenceEqual(MasterMagic)) return;
    this.Valid = true;

    var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(master.AsSpan(18, 2));
    if (blockSize is >= 512 and <= 8192) this.BlockSize = blockSize;
    this.UuidHex = Convert.ToHexString(master.AsSpan(20, 16));
    this.Label = ReadCString(master.AsSpan(36, 16));

    // Native metadata is authoritative. The private payload directory is kept
    // only so old workbench images remain readable while they are migrated.
    if (this.TryReadNativeTree()) { this.NativeTreeValid = true; return; }
    this._entries.Clear();
    this._nativeNames.Clear();
    this.RootMetadata = null;

    if (!master.AsSpan(Reiser4Writer.MasterPayloadMarkerOff, Reiser4Writer.PayloadMarker.Length)
        .SequenceEqual(Reiser4Writer.PayloadMarker))
      return;

    var dirBlock = BinaryPrimitives.ReadUInt64LittleEndian(
      master.AsSpan(Reiser4Writer.MasterPayloadDirOff, 8));
    this.ReadLegacyDirectory(dirBlock);
  }

  /// <summary>True when the complete supported native namespace was decoded.</summary>
  public bool NativeTreeValid { get; private set; }

  /// <summary>Root stat-data, including inherited plugin settings.</summary>
  public FileMetadata? RootMetadata { get; private set; }

  internal IReadOnlyList<ulong> NativeNodeBlocks { get; private set; } = [];

  internal readonly record struct NativeRun(ulong Start, ulong Width);

  /// <summary>Stat-data fields which can be represented by the current writer.</summary>
  public sealed record FileMetadata(
      long Size, ushort Mode, uint LinkCount, uint UserId, uint GroupId,
      uint ModifiedTime, uint AccessedTime, uint ChangedTime,
      byte[] RawStatData) {
    /// <summary>Stable filesystem object identity.</summary>
    public ulong ObjectId { get; init; }
    /// <summary>Locality of the object stat key.</summary>
    public ulong StatLocality { get; init; }

    /// <summary>Last-write timestamp, when the seconds value is representable.</summary>
    public DateTime? LastModified => ModifiedTime == 0
      ? null
      : DateTimeOffset.FromUnixTimeSeconds(ModifiedTime).UtcDateTime;
  }

  /// <summary>One filesystem object and its physical extents.</summary>
  public sealed record Entry(string Name, ulong FirstBlock, long Size) {
    internal IReadOnlyList<NativeRun>? NativeRuns { get; init; }
    /// <summary>True when this entry is a directory.</summary>
    public bool IsDirectory { get; init; }
    /// <summary>On-disk stat-data fields retained for metadata-preserving rebuilds.</summary>
    public FileMetadata? Metadata { get; init; }
  }

  /// <summary>Reads a file's contents. Only valid below the array limit.</summary>
  public byte[] Extract(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Reiser4: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    using var buffer = new MemoryStream();
    this.ExtractTo(entry, buffer);
    return buffer.ToArray();
  }

  /// <summary>Writes <paramref name="entry"/>'s contents into <paramref name="destination"/>.</summary>
  public long ExtractTo(Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;
    if (entry.Size <= 0) return 0;

    if (entry.NativeRuns is { } runs)
      return this.ExtractNative(runs, entry.Size, destination);

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long written = 0;
    while (written < entry.Size) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset >= this._image.Length) break;
      var take = (int)Math.Min(Math.Min(this.BlockSize, entry.Size - written),
        this._image.Length - offset);
      if (take <= 0) break;
      this._image.CopyTo(offset, destination, take);
      written += take;
      ++block;
    }
    return written;
  }

  private long ExtractNative(IReadOnlyList<NativeRun> runs, long size, Stream destination) {
    long written = 0;
    foreach (var run in runs) {
      if (written >= size || run.Width == 0) break;
      var offset = checked((long)run.Start * this.BlockSize);
      if (offset < 0 || offset >= this._image.Length) break;
      var runBytes = checked((long)Math.Min(run.Width, (ulong)(long.MaxValue / this.BlockSize)) * this.BlockSize);
      var take = Math.Min(Math.Min(runBytes, size - written), this._image.Length - offset);
      if (take <= 0) break;
      this._image.CopyTo(offset, destination, take);
      written += take;
    }
    return written;
  }

  /// <summary>Where an entry's bytes live as physical byte runs.</summary>
  public IEnumerable<(long Offset, long Length)> EnumerateRuns(Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Size <= 0) yield break;

    if (entry.NativeRuns is { } nativeRuns) {
      long remaining = entry.Size;
      foreach (var run in nativeRuns) {
        if (remaining <= 0 || run.Width == 0) yield break;
        var offset = checked((long)run.Start * this.BlockSize);
        if (offset < 0 || offset >= this._image.Length) yield break;
        var capacity = checked((long)Math.Min(run.Width, (ulong)(long.MaxValue / this.BlockSize)) * this.BlockSize);
        var length = Math.Min(Math.Min(capacity, remaining), this._image.Length - offset);
        if (length <= 0) yield break;
        yield return (offset, length);
        remaining -= length;
      }
      yield break;
    }

    var blocksPerBitmap = Reiser4Writer.BlocksPerBitmap;
    var block = entry.FirstBlock;
    long remainingLegacy = entry.Size;
    while (remainingLegacy > 0) {
      while (IsBitmapBlock(block, blocksPerBitmap)) ++block;
      var start = (long)block * this.BlockSize;
      if (start < 0 || start >= this._image.Length) yield break;

      long run = 0;
      while (remainingLegacy - run > 0 && !IsBitmapBlock(block, blocksPerBitmap)) {
        var take = Math.Min((long)this.BlockSize, remainingLegacy - run);
        take = Math.Min(take, this._image.Length - (start + run));
        if (take <= 0) break;
        run += take;
        ++block;
      }
      if (run <= 0) yield break;
      yield return (start, run);
      remainingLegacy -= run;
    }
  }

  /// <summary>
  /// Walks formatted node40 internal pointers from the format40 root, collects
  /// standard leaf1 items, then resolves cde40 names against stat40 keys. Unknown
  /// node and item plugins fail closed so the legacy compatibility path remains
  /// available for images carrying it.
  /// </summary>
  private bool TryReadNativeTree() {
    if (this.BlockSize != Reiser4Writer.BlockSize) return false;
    var format = this._image.Read((long)17 * this.BlockSize, this.BlockSize);
    if (format.Length != this.BlockSize || !format.AsSpan(52, Reiser4MasterSb.Format40Magic.Length)
        .SequenceEqual(Reiser4MasterSb.Format40Magic)) return false;
    var root = BinaryPrimitives.ReadUInt64LittleEndian(format.AsSpan(16, 8));
    if (root == 0) return false;

    var leafBlocks = new List<(ulong Block, byte[] Data)>();
    var nodeBlocks = new HashSet<ulong>();
    if (!this.WalkNativeNodes(root, nodeBlocks, leafBlocks, 0) || leafBlocks.Count == 0)
      return false;

    var statData = new Dictionary<(ulong Locality, ulong ObjectId), FileMetadata>();
    var extents = new Dictionary<(ulong Locality, ulong ObjectId), List<(ulong Offset, List<NativeRun> Runs)>>();
    var directories = new Dictionary<ulong, List<byte[]>>();
    foreach (var (_, leaf) in leafBlocks) {
      if (!TryReadLeafItems(leaf, out var leafItems, out var bodiesEnd)) return false;
      var byBody = leafItems.OrderBy(static item => item.BodyOffset).ToArray();
      for (var i = 0; i < byBody.Length; ++i) {
        var item = byBody[i];
        var end = i + 1 < byBody.Length ? byBody[i + 1].BodyOffset : bodiesEnd;
        if (end < item.BodyOffset) return false;
        var body = leaf.AsSpan(item.BodyOffset, end - item.BodyOffset);
        var objectKey = (item.Locality, item.ObjectId);
        switch (item.Plugin) {
          case PluginStat40 when item.Minor == MinorStatData && item.Ordering == 0 && item.KeyOffset == 0:
            if (body.Length < 44 || (BinaryPrimitives.ReadUInt16LittleEndian(body) & 3) != 3) return false;
            var size = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(8, 8));
            if (size > long.MaxValue) return false;
            if (statData.ContainsKey(objectKey)) return false;
            statData[objectKey] = new FileMetadata((long)size,
              BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(16, 4)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(20, 4)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(24, 4)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(28, 4)),
              BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(32, 4)), body.ToArray()) {
                ObjectId = item.ObjectId, StatLocality = item.Locality,
              };
            break;
          case PluginExtent40 when item.Minor == MinorFileBody && item.Ordering == 0:
            if ((body.Length & 15) != 0) return false;
            var runs = new List<NativeRun>(body.Length / 16);
            for (var p = 0; p < body.Length; p += 16) {
              var start = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p, 8));
              var width = BinaryPrimitives.ReadUInt64LittleEndian(body.Slice(p + 8, 8));
              if (width != 0) runs.Add(new NativeRun(start, width));
            }
            if (!extents.TryGetValue(objectKey, out var pieces)) extents[objectKey] = pieces = [];
            pieces.Add((item.KeyOffset, runs));
            break;
          case PluginCde40 when item.Minor == 0:
            if (!directories.TryGetValue(item.Locality, out var items)) directories[item.Locality] = items = [];
            items.Add(body.ToArray());
            break;
          case PluginNodePointer when leaf[26] == 2:
            if (body.Length != 8) return false;
            break;
          default: return false;
        }
      }
    }

    foreach (var parts in extents.Values) {
      ulong logical = 0;
      foreach (var part in parts.OrderBy(static p => p.Offset)) {
        if (part.Offset != logical) return false;
        foreach (var run in part.Runs) {
          if (run.Start < 25 || nodeBlocks.Any(node => node >= run.Start && node - run.Start < run.Width) || run.Start >= (ulong)(this.Length / this.BlockSize) ||
              run.Width > (ulong)(this.Length / this.BlockSize) - run.Start ||
              run.Width > (ulong)long.MaxValue / (ulong)this.BlockSize - logical / (ulong)this.BlockSize) return false;
          logical += run.Width * (ulong)this.BlockSize;
        }
      }
    }
    var joinedExtents = extents.ToDictionary(static pair => pair.Key,
      static pair => (IReadOnlyList<NativeRun>)pair.Value.OrderBy(static part => part.Offset)
        .SelectMany(static part => part.Runs).ToArray());
    var visited = new HashSet<ulong>();
    if (!WalkNativeDirectory(0x2a, "", statData, joinedExtents, directories, visited))
      return false;
    if (!statData.TryGetValue((0x29, 0x2a), out var rootMetadata) || (rootMetadata.Mode & 0xF000) != 0x4000)
      return false;
    if (statData.Count != this._entries.Count + 1 || directories.Keys.Any(key => !visited.Contains(key)) ||
        extents.Keys.Any(key => !statData.TryGetValue(key, out var metadata) || (metadata.Mode & 0xF000) != 0x8000))
      return false;
    this.RootMetadata = rootMetadata;
    this.NativeNodeBlocks = nodeBlocks.ToArray();
    return true;
  }

  private const ushort PluginNodePointer = 3;

  private readonly record struct NativeItem(
    ulong Locality, byte Minor, ulong Ordering, ulong ObjectId, ulong KeyOffset, ushort Plugin, ushort BodyOffset);

  private bool WalkNativeNodes(ulong block, HashSet<ulong> visited,
      List<(ulong Block, byte[] Data)> leaves, int depth, int expectedLevel = 0) {
    if (depth > 16 || block >= (ulong)(this._image.Length / this.BlockSize) || !visited.Add(block)) return false;
    var node = this._image.Read(checked((long)block * this.BlockSize), this.BlockSize);
    if (node.Length != this.BlockSize || BinaryPrimitives.ReadUInt16LittleEndian(node) != 0
        || BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(8, 4)) != unchecked((uint)Reiser4Tree.NodeMagic))
      return false;
    var level = node[26];
    if (expectedLevel != 0 && level != expectedLevel) return false;
    var count = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(2, 2));
    if (count > (this.BlockSize - Reiser4Tree.NodeHeaderBytes) / ItemHeaderBytes) return false;
    if (level == 1) {
      leaves.Add((block, node));
      return true;
    }
    if (level < 2 || count == 0) return false;

    if (!TryReadLeafItems(node, out var items, out var bodiesEnd)) return false;
    if (level == 2) leaves.Add((block, node));
    var children = new List<ulong>(count);
    var byBody = items.OrderBy(static item => item.BodyOffset).ToArray();
    for (var i = 0; i < byBody.Length; ++i) {
      var item = byBody[i];
      var end = i + 1 < byBody.Length ? byBody[i + 1].BodyOffset : bodiesEnd;
      if (item.Plugin == PluginExtent40 && level == 2) continue;
      if (item.Plugin != PluginNodePointer || end - item.BodyOffset != sizeof(ulong)) return false;
      children.Add(BinaryPrimitives.ReadUInt64LittleEndian(node.AsSpan(item.BodyOffset, sizeof(ulong))));
    }
    foreach (var child in children)
      if (!this.WalkNativeNodes(child, visited, leaves, depth + 1, level - 1)) return false;
    return true;
  }

  private bool TryReadLeafItems(byte[] leaf, out List<NativeItem> items, out int bodiesEnd) {
    items = [];
    var count = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(2, 2));
    bodiesEnd = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(6, 2));
    if (count == 0 || count > (this.BlockSize - Reiser4Tree.NodeHeaderBytes) / ItemHeaderBytes
        || bodiesEnd < Reiser4Tree.NodeHeaderBytes || bodiesEnd > this.BlockSize - count * ItemHeaderBytes)
      return false;
    for (var i = 0; i < count; ++i) {
      var header = this.BlockSize - (i + 1) * ItemHeaderBytes;
      var key0 = BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header, 8));
      var bodyOffset = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 32, 2));
      var plugin = BinaryPrimitives.ReadUInt16LittleEndian(leaf.AsSpan(header + 36, 2));
      if (bodyOffset < Reiser4Tree.NodeHeaderBytes || bodyOffset > bodiesEnd) return false;
      items.Add(new NativeItem(key0 >> 4, (byte)(key0 & 0xf),
        BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 8, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 16, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(leaf.AsSpan(header + 24, 8)), plugin, bodyOffset));
    }
    return true;
  }

  private bool WalkNativeDirectory(ulong directoryId, string path,
      IReadOnlyDictionary<(ulong Locality, ulong ObjectId), FileMetadata> statData,
      IReadOnlyDictionary<(ulong Locality, ulong ObjectId), IReadOnlyList<NativeRun>> extents,
      IReadOnlyDictionary<ulong, List<byte[]>> directories, HashSet<ulong> visited) {
    if (path.Count(static c => c == '/') > 128 || !visited.Add(directoryId)) return false;
    if (!directories.TryGetValue(directoryId, out var directoryItems)) return false;

    foreach (var directory in directoryItems) {
      if (directory.Length < 2) return false;
      var count = BinaryPrimitives.ReadUInt16LittleEndian(directory);
      var unitsStart = 2 + count * DirectoryUnitHeaderBytes;
      if (unitsStart > directory.Length) return false;

      for (var i = 0; i < count; ++i) {
        var header = 2 + i * DirectoryUnitHeaderBytes;
        var ordering = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(header, 8));
        var objectIdPart = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(header + 8, 8));
        var offsetPart = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(header + 16, 8));
        var unit = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(header + 24, 2));
        var unitEnd = i + 1 < count
          ? BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(header + DirectoryUnitHeaderBytes + 24, 2))
          : directory.Length;
        if (unit < unitsStart || unitEnd < unit || unitEnd > directory.Length || unit + TargetKeyBytes > unitEnd)
          return false;

        var name = DecodeName(directory.AsSpan(unit, unitEnd - unit), ordering, objectIdPart, offsetPart);
        if (name.Length == 0 || name is "." or "..") continue;
        if (name.Contains('/') || name.Contains('\\') || name.Contains('\0') || name.Contains(':') || name.Any(static c => c > 127)) return false;
        var targetLocality = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(unit, 8)) >> 4;
        var targetObjectId = BinaryPrimitives.ReadUInt64LittleEndian(directory.AsSpan(unit + 16, 8));
        if (!statData.TryGetValue((targetLocality, targetObjectId), out var metadata)) return false;

        var fullName = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
        if (!this._nativeNames.Add(fullName)) return false;
        var modeType = metadata.Mode & 0xF000;
        if (modeType == 0x4000) {
          this._entries.Add(new Entry(fullName, 0, 0) { IsDirectory = true, Metadata = metadata });
          if (!this.WalkNativeDirectory(targetObjectId, fullName, statData, extents, directories, visited))
            return false;
          continue;
        }

        if (modeType != 0x8000) return false;
        extents.TryGetValue((targetLocality, targetObjectId), out var runs);
        runs ??= Array.Empty<NativeRun>();
        if (runs.Aggregate(0UL, static (sum, run) => sum + run.Width) * (ulong)this.BlockSize < (ulong)metadata.Size) return false;
        this._entries.Add(new Entry(fullName, runs.Count == 0 ? 0 : runs[0].Start, metadata.Size) {
          NativeRuns = runs,
          Metadata = metadata,
        });
      }
    }
    return true;
  }

  private static string DecodeName(
      ReadOnlySpan<byte> unit, ulong ordering, ulong objectId, ulong offset) {
    if (ordering == 0 && objectId == 0 && offset == 0) return ".";
    if ((ordering & HashedNameBit) != 0) {
      var stored = unit[TargetKeyBytes..];
      var nul = stored.IndexOf((byte)0);
      if (nul >= 0) stored = stored[..nul];
      return Encoding.Latin1.GetString(stored);
    }

    Span<byte> name = stackalloc byte[23];
    var length = 0;
    length += Unpack(name[length..], ordering & 0x00FF_FFFF_FFFF_FFFFUL, 7);
    length += Unpack(name[length..], objectId, 8);
    length += Unpack(name[length..], offset, 8);
    return Encoding.Latin1.GetString(name[..length]);
  }

  private static int Unpack(Span<byte> destination, ulong value, int bytes) {
    var written = 0;
    for (var shift = (bytes - 1) * 8; shift >= 0; shift -= 8) {
      var b = (byte)(value >> shift);
      if (b == 0) break;
      destination[written++] = b;
    }
    return written;
  }

  private void ReadLegacyDirectory(ulong firstBlock) {
    var visited = new HashSet<ulong>();
    var block = firstBlock;
    while (block != 0 && visited.Add(block)) {
      var offset = (long)block * this.BlockSize;
      if (offset < 0 || offset + this.BlockSize > this._image.Length) break;
      var buf = this._image.Read(offset, this.BlockSize);
      if (!buf.AsSpan(0, Reiser4Writer.DirMagic.Length).SequenceEqual(Reiser4Writer.DirMagic)) break;

      var next = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(8, 8));
      var count = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4));
      var capacity = (this.BlockSize - Reiser4Writer.DirHeadSize) / Reiser4Writer.DirEntrySize;
      for (var i = 0; i < count && i < capacity; ++i) {
        var o = Reiser4Writer.DirHeadSize + i * Reiser4Writer.DirEntrySize;
        var name = ReadCString(buf.AsSpan(o, Reiser4Writer.DirNameLength));
        if (name.Length == 0) continue;
        var first = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength, 8));
        var size = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(o + Reiser4Writer.DirNameLength + 8, 8));
        if (size < 0) continue;
        this._entries.Add(new Entry(name, first, size));
      }
      block = next;
    }
  }

  private static bool IsBitmapBlock(ulong block, ulong blocksPerBitmap)
    => block == 18 || (block != 0 && block % blocksPerBitmap == 0);

  private static string ReadCString(ReadOnlySpan<byte> span) {
    var n = span.IndexOf((byte)0);
    if (n < 0) n = span.Length;
    return n == 0 ? "" : Encoding.UTF8.GetString(span[..n]);
  }

  /// <summary>Releases resources held by this instance.</summary>
  public void Dispose() => this._image.Dispose();
}
