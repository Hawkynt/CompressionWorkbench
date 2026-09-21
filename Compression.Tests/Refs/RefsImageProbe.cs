#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Refs;

/// <summary>
/// Independent on-disk decoder for ReFS images, written straight from the
/// structure definitions and deliberately sharing no code with the package's
/// reader, writer or CoW engine.
///
/// Its whole purpose is to be a second pair of eyes: assertions that go through
/// <c>RefsMetadataReader</c> only prove that our reader agrees with our writer,
/// which is exactly how a shared defect survives. Everything this type returns
/// is decoded from raw bytes — checkpoint clocks, root descriptors, B+ rows,
/// Block Refcount words, allocator bitmaps and extent tables.
/// </summary>
internal sealed class RefsImageProbe {
  public const int SuperblockCluster = 0x1E;

  private readonly byte[] _image;

  public RefsImageProbe(byte[] image) {
    this._image = image;
    if (!image.AsSpan(3, 4).SequenceEqual("ReFS"u8)) throw new InvalidDataException("Not a ReFS image.");
    var sectorSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x20, 4));
    var sectorsPerCluster = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x24, 4));
    this.ClusterSize = sectorSize * sectorsPerCluster;
    this.PageSize = this.ClusterSize <= 4096 ? 16 * 1024 : 64 * 1024;
    this.ClustersPerPage = this.PageSize / this.ClusterSize;
    this.LoadBootstrap();
    this.LoadContainers();
  }

  public int ClusterSize { get; }
  public int PageSize { get; }
  public int ClustersPerPage { get; }
  public int PageReferenceSize { get; private set; }
  public IReadOnlyList<ulong> CheckpointLcns { get; private set; } = [];
  public ulong ActiveCheckpointLcn { get; private set; }
  public ulong ActiveCheckpointClock { get; private set; }

  private readonly Dictionary<ulong, ulong> _containerBases = [];
  private uint _clustersPerContainer;
  private int _containerShift;

  // ── bootstrap ──────────────────────────────────────────────────────────────

  private void LoadBootstrap() {
    var supb = this.Cluster(SuperblockCluster);
    if (!supb.AsSpan(0, 4).SequenceEqual("SUPB"u8))
      throw new InvalidDataException("Probe found no SUPB at cluster 0x1E.");
    var listOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(supb.AsSpan(0x70, 4));
    var listCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(supb.AsSpan(0x74, 4));
    var slots = new List<ulong>(listCount);
    for (var i = 0; i < listCount; ++i) {
      var lcn = BinaryPrimitives.ReadUInt64LittleEndian(supb.AsSpan(listOffset + i * 8, 8));
      if (lcn is not (0 or ulong.MaxValue)) slots.Add(lcn);
    }
    this.CheckpointLcns = slots;

    ulong bestLcn = 0;
    ulong bestClock = 0;
    var found = false;
    foreach (var lcn in slots) {
      var page = this.Page(lcn);
      if (page == null || !page.AsSpan(0, 4).SequenceEqual("CHKP"u8)) continue;
      var clock = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x60, 8));
      if (found && clock <= bestClock) continue;
      bestLcn = lcn;
      bestClock = clock;
      found = true;
    }
    if (!found) throw new InvalidDataException("Probe found no readable CHKP.");
    this.ActiveCheckpointLcn = bestLcn;
    this.ActiveCheckpointClock = bestClock;
    this.PageReferenceSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(this.Page(bestLcn)!.AsSpan(0x5C, 4));
  }

  /// <summary>Clock and the thirteen root page addresses of one checkpoint slot.</summary>
  internal sealed record CheckpointView(ulong Lcn, ulong Clock, IReadOnlyList<IReadOnlyList<ulong>> Roots);

  public CheckpointView ReadCheckpoint(ulong lcn) {
    var page = this.Page(lcn) ?? throw new InvalidDataException($"Checkpoint 0x{lcn:X} is unreadable.");
    if (!page.AsSpan(0, 4).SequenceEqual("CHKP"u8))
      throw new InvalidDataException($"Cluster 0x{lcn:X} does not contain CHKP.");
    var clock = BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(0x60, 8));
    var referenceSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x5C, 4));
    var flags = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x78, 4));
    var listBase = (flags & 0x0200) != 0
      ? (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x94, 4))
      : 0x94;

    var roots = new List<IReadOnlyList<ulong>>(13);
    for (var i = 0; i < 13; ++i) {
      var descriptor = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(listBase + i * 4, 4));
      if (descriptor <= 0 || descriptor + referenceSize > page.Length) {
        roots.Add([]);
        continue;
      }
      roots.Add(ParseReference(page.AsSpan(descriptor, referenceSize)));
    }
    return new CheckpointView(lcn, clock, roots);
  }

  public CheckpointView ActiveCheckpoint() => this.ReadCheckpoint(this.ActiveCheckpointLcn);

  private static IReadOnlyList<ulong> ParseReference(ReadOnlySpan<byte> reference) {
    var lcns = new List<ulong>(4);
    for (var i = 0; i < 4 && i * 8 + 8 <= reference.Length; ++i) {
      var lcn = BinaryPrimitives.ReadUInt64LittleEndian(reference.Slice(i * 8, 8));
      if (lcn is not (0 or ulong.MaxValue)) lcns.Add(lcn);
    }
    return lcns;
  }

  // ── container translation ──────────────────────────────────────────────────

  private void LoadContainers() {
    var active = this.ActiveCheckpoint();
    foreach (var rootIndex in new[] { 7, 8 }) {
      var root = active.Roots[rootIndex];
      if (root.Count == 0) continue;
      var bases = new Dictionary<ulong, ulong>();
      uint cpc = 0;
      foreach (var (key, value) in this.WalkPhysicalTree(root)) {
        if (key.Length < 16 || value.Length < 0x98) continue;
        var id = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
        var rowCpc = BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0x18, 4));
        if (rowCpc == 0) continue;
        if (cpc == 0) cpc = rowCpc;
        if (rowCpc != cpc) { bases.Clear(); break; }
        bases[id] = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(value.Length - 16, 8));
      }
      if (bases.Count == 0 || cpc == 0 || (cpc & (cpc - 1)) != 0) continue;
      foreach (var item in bases) this._containerBases[item.Key] = item.Value;
      this._clustersPerContainer = cpc;
      this._containerShift = 0;
      while ((1u << this._containerShift) < cpc) ++this._containerShift;
      return;
    }
    throw new InvalidDataException("Probe could not decode a Container Table.");
  }

  public ulong ToPhysical(ulong virtualLcn) {
    var container = virtualLcn >> this._containerShift;
    var within = virtualLcn & (this._clustersPerContainer - 1UL);
    if (!this._containerBases.TryGetValue(container, out var physicalBase))
      throw new InvalidDataException($"Probe cannot map virtual container {container}.");
    return physicalBase + within;
  }

  // ── B+ walking ─────────────────────────────────────────────────────────────

  public IReadOnlyList<(byte[] Key, byte[] Value)> WalkVirtualTree(IReadOnlyList<ulong> rootLcns)
    => this.Walk(rootLcns.Select(this.ToPhysical).ToArray(), virtualChildren: true);

  public IReadOnlyList<(byte[] Key, byte[] Value)> WalkPhysicalTree(IReadOnlyList<ulong> rootLcns)
    => this.Walk(rootLcns, virtualChildren: false);

  private IReadOnlyList<(byte[] Key, byte[] Value)> Walk(
      IReadOnlyList<ulong> physicalLcns,
      bool virtualChildren) {
    var result = new List<(byte[], byte[])>();
    var seen = new HashSet<ulong>();
    var pending = new Stack<IReadOnlyList<ulong>>();
    pending.Push(physicalLcns);

    while (pending.Count > 0) {
      var slots = pending.Pop();
      if (slots.Count == 0 || !seen.Add(slots[0])) continue;
      var page = this.PageFromSlots(slots);
      if (page == null || !page.AsSpan(0, 4).SequenceEqual("MSB+"u8)) continue;

      var nodeOffset = 0x50 + (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0x50, 4));
      var isInner = (BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(nodeOffset + 0x0C, 4)) & 0x100) != 0;
      var indexStart = nodeOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(nodeOffset + 0x10, 4));
      var indexEnd = nodeOffset + (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(nodeOffset + 0x20, 4));

      for (var entry = indexStart; entry < indexEnd; entry += 4) {
        var rowOffset = nodeOffset + (int)(BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(entry, 4)) & 0xFFFF);
        var rowSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(rowOffset, 4));
        if (rowSize < 16 || rowOffset + rowSize > page.Length) continue;
        var keyOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 4, 2));
        var keyLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 6, 2));
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 10, 2));
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(rowOffset + 12, 2));
        if (keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize) continue;
        var key = page.AsSpan(rowOffset + keyOffset, keyLength).ToArray();
        var value = page.AsSpan(rowOffset + valueOffset, valueLength).ToArray();

        if (!isInner) {
          result.Add((key, value));
          continue;
        }
        if (value.Length < 32) continue;
        var child = ParseReference(value);
        if (child.Count == 0) continue;
        pending.Push(virtualChildren ? child.Select(this.ToPhysical).ToArray() : child);
      }
    }
    return result;
  }

  // ── decoded views ──────────────────────────────────────────────────────────

  /// <summary>One decoded Block Refcount word: the 14-bit count and its two flag bits.</summary>
  internal readonly record struct RefcountWord(ushort Count, bool DedupMetadata, bool DedupManaged);

  /// <summary>
  /// Decodes every tracked Block Refcount slot of root #6 straight from the
  /// 0x820-byte rows. Only slots inside an existing row appear; absence is the
  /// sparse "one ordinary owner" state.
  /// </summary>
  public IReadOnlyDictionary<ulong, RefcountWord> ReadRefcounts(CheckpointView checkpoint) {
    var result = new Dictionary<ulong, RefcountWord>();
    foreach (var (key, value) in this.WalkVirtualTree(checkpoint.Roots[6])) {
      if (key.Length < 16 || value.Length < 0x820) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
      if (BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8)) != 0x400) continue;
      for (var i = 0; i < 0x400; ++i) {
        var raw = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x1C + i * 2, 2));
        if (raw == 0) continue;
        result[start + (ulong)i] = new RefcountWord(
          (ushort)(raw & 0x3FFF),
          (raw & 0x4000) != 0,
          (raw & 0x8000) != 0);
      }
    }
    return result;
  }

  /// <summary>Sum of the low 14-bit counts each Block Refcount row advertises at +0x18.</summary>
  public IReadOnlyDictionary<ulong, (uint Stated, uint Actual)> ReadRefcountTotals(CheckpointView checkpoint) {
    var result = new Dictionary<ulong, (uint, uint)>();
    foreach (var (key, value) in this.WalkVirtualTree(checkpoint.Roots[6])) {
      if (key.Length < 16 || value.Length < 0x820) continue;
      if (BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8)) != 0x400) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(0, 8));
      uint actual = 0;
      for (var i = 0; i < 0x400; ++i)
        actual += (uint)(BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x1C + i * 2, 2)) & 0x3FFF);
      result[start] = (BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(0x18, 4)), actual);
    }
    return result;
  }

  public int CountRefcountRows(CheckpointView checkpoint)
    => this.WalkVirtualTree(checkpoint.Roots[6])
      .Count(row => row.Key.Length >= 16
        && BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8)) == 0x400);

  /// <summary>Allocation state of the Medium Allocator (root #1) as a physical-LCN set.</summary>
  public HashSet<ulong> ReadAllocatedClusters(CheckpointView checkpoint) {
    var allocated = new HashSet<ulong>();
    foreach (var (_, value) in this.WalkVirtualTree(checkpoint.Roots[1])) {
      if (value.Length < 24) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0x00, 8));
      var length = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0x08, 8));
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
      if (length == 0 || length > 16384) continue;
      for (ulong i = 0; i < length; ++i) {
        var isAllocated = flags switch {
          0x01 when value.Length >= 0x18 + 2048 => (value[0x18 + (int)(i >> 3)] & (1 << (int)(i & 7))) != 0,
          0x02 => true,
          0x05 or 0x09 => false,
          _ => throw new InvalidDataException($"Probe met an allocator row with flags 0x{flags:X4}."),
        };
        if (isAllocated) allocated.Add(this.ToPhysical(start + i));
      }
    }
    return allocated;
  }

  /// <summary>A file's on-disk stream layout, decoded from its type-0x40 backing row.</summary>
  internal sealed record FileView(
    string Name,
    ulong FileId,
    ulong Size,
    ulong AllocatedSize,
    IReadOnlyList<(uint Vcn, ulong Lcn, uint Run, uint Flags)> Extents) {
    public IReadOnlyList<ulong> Clusters {
      get {
        var result = new List<ulong>();
        foreach (var extent in this.Extents.OrderBy(e => e.Vcn))
          for (uint i = 0; i < extent.Run; ++i)
            result.Add(extent.Lcn + i);
        return result;
      }
    }
  }

  public IReadOnlyDictionary<string, FileView> ReadFiles(CheckpointView checkpoint) {
    var objects = new Dictionary<ulong, IReadOnlyList<ulong>>();
    foreach (var (key, value) in this.WalkVirtualTree(checkpoint.Roots[0])) {
      if (key.Length < 16 || value.Length < 0x40) continue;
      var oid = BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8));
      var reference = ParseReference(value.AsSpan(0x20));
      if (reference.Count > 0) objects[oid] = reference;
    }
    if (!objects.TryGetValue(RefsSyntheticVolume.RootDirectoryOid, out var directoryRoot))
      throw new InvalidDataException("Probe found no root directory object.");

    var rows = this.WalkVirtualTree(directoryRoot);
    var backing = new Dictionary<ulong, byte[]>();
    foreach (var (key, value) in rows) {
      if (key.Length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(0, 2)) != 0x40) continue;
      backing[BinaryPrimitives.ReadUInt64LittleEndian(key.AsSpan(8, 8))] = value;
    }

    var result = new Dictionary<string, FileView>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in rows) {
      if (key.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(0, 2)) != 0x30) continue;
      if (BinaryPrimitives.ReadUInt16LittleEndian(key.AsSpan(2, 2)) != 0x02) continue;
      if (value.Length is < 0x48 or > 0x54) continue;
      var name = Encoding.Unicode.GetString(key.AsSpan(4)).TrimEnd('\0');
      var fileId = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(0x00, 8));
      if (!backing.TryGetValue(fileId, out var holder)) continue;
      result[name] = new FileView(
        name,
        fileId,
        BinaryPrimitives.ReadUInt64LittleEndian(holder.AsSpan(0x58, 8)),
        BinaryPrimitives.ReadUInt64LittleEndian(holder.AsSpan(0x60, 8)),
        this.DecodeExtents(holder));
    }
    return result;
  }

  private IReadOnlyList<(uint Vcn, ulong Lcn, uint Run, uint Flags)> DecodeExtents(byte[] holder) {
    var result = new List<(uint, ulong, uint, uint)>();
    if (holder.Length < 0xB0) return result;
    var recordSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(holder.AsSpan(0xA8, 4));
    if (recordSize <= 0 || 0xA8 + recordSize > holder.Length) return result;
    var header = 0xAC;
    var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(holder.AsSpan(header + 0x00, 4));
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(holder.AsSpan(header + 0x14, 4));
    var entries = header + start;
    for (var i = 0; i < count && entries + (i + 1) * 24 <= holder.Length; ++i) {
      var entry = holder.AsSpan(entries + i * 24, 24);
      result.Add((
        BinaryPrimitives.ReadUInt32LittleEndian(entry[0x0C..0x10]),
        this.ToPhysical(BinaryPrimitives.ReadUInt64LittleEndian(entry[0x00..0x08])),
        BinaryPrimitives.ReadUInt32LittleEndian(entry[0x14..0x18]),
        BinaryPrimitives.ReadUInt32LittleEndian(entry[0x08..0x0C])));
    }
    return result;
  }

  /// <summary>Reads a file's bytes by following its decoded extents in the raw image.</summary>
  public byte[] ReadFileContent(FileView file) {
    var result = new byte[file.Size];
    var written = 0;
    foreach (var lcn in file.Clusters) {
      var take = Math.Min(this.ClusterSize, result.Length - written);
      if (take <= 0) break;
      this._image.AsSpan((int)((long)lcn * this.ClusterSize), take).CopyTo(result.AsSpan(written));
      written += take;
    }
    return result;
  }

  // ── raw access ─────────────────────────────────────────────────────────────

  public byte[] Cluster(ulong lcn) {
    var offset = (long)lcn * this.ClusterSize;
    if (offset < 0 || offset + this.ClusterSize > this._image.Length)
      throw new InvalidDataException($"Cluster 0x{lcn:X} lies outside the image.");
    return this._image.AsSpan((int)offset, this.ClusterSize).ToArray();
  }

  public byte[]? Page(ulong headLcn) {
    var offset = (long)headLcn * this.ClusterSize;
    if (offset < 0 || offset + this.PageSize > this._image.Length) return null;
    return this._image.AsSpan((int)offset, this.PageSize).ToArray();
  }

  private byte[]? PageFromSlots(IReadOnlyList<ulong> slots) {
    var page = new byte[this.PageSize];
    var written = 0;
    foreach (var lcn in slots) {
      if (written >= page.Length) break;
      var offset = (long)lcn * this.ClusterSize;
      if (offset < 0 || offset + this.ClusterSize > this._image.Length) return null;
      var take = Math.Min(this.ClusterSize, page.Length - written);
      this._image.AsSpan((int)offset, take).CopyTo(page.AsSpan(written, take));
      written += take;
    }
    return written == page.Length ? page : null;
  }
}
