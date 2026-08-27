#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Active ReFS 3.x metadata bootstrapper. This is deliberately a reader: it
/// follows only pages reachable from the winning SUPB/CHKP pair, so stale CoW
/// pages are never mistaken for live metadata.
/// </summary>
internal sealed class RefsMetadataReader {
  private const int RootCount = 13;
  private readonly Stream _stream;
  private readonly Dictionary<ulong, ulong> _containers = [];
  private readonly List<RefsPageReference> _roots = [];
  private readonly HashSet<ulong> _visitedMetadataPages = [];
  private uint _clustersPerContainer;
  private int _containerShift;

  private RefsMetadataReader(Stream stream, RefsVolumeHeader header) {
    this._stream = stream;
    this.Header = header;
    this.ClusterSize = checked((int)header.BytesPerCluster);
    this.PageSize = this.ClusterSize <= 4096 ? 16 * 1024 : 64 * 1024;
  }

  public RefsVolumeHeader Header { get; }
  public int ClusterSize { get; }
  public int PageSize { get; }
  public ulong ActiveCheckpointLcn { get; private set; }
  public ulong ActiveCheckpointClock { get; private set; }
  public uint CheckpointFlags { get; private set; }
  public uint PageReferenceSize { get; private set; }
  public IReadOnlyList<RefsPageReference> Roots => this._roots;
  public IReadOnlySet<ulong> VisitedMetadataPages => this._visitedMetadataPages;

  public static RefsMetadataReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("ReFS metadata walking requires a readable, seekable stream.", nameof(stream));

    var vbr = new byte[512];
    stream.Position = 0;
    stream.ReadExactly(vbr);
    var header = RefsVolumeHeader.TryParse(vbr);
    if (!header.Valid)
      throw new InvalidDataException("The stream does not contain a valid ReFS 3.x VBR.");
    if (header.MajorVersion != 3)
      throw new NotSupportedException($"ReFS {header.MajorVersion}.{header.MinorVersion} is not a ReFS 3.x volume.");
    if (header.BytesPerCluster is not (4096 or 65536))
      throw new NotSupportedException($"Unsupported ReFS cluster size {header.BytesPerCluster:N0} bytes.");

    var result = new RefsMetadataReader(stream, header);
    result.Bootstrap();
    return result;
  }

  private void Bootstrap() {
    var totalClusters = this.Header.TotalClusters;
    if (totalClusters < 32)
      throw new InvalidDataException("ReFS volume is too small to contain its bootstrap metadata.");

    var superblockCandidates = new[] { 0x1EUL, totalClusters - 2, totalClusters - 3 };
    (ulong Lcn, ulong Generation, byte[] Page)? bestSuperblock = null;
    foreach (var lcn in superblockCandidates.Distinct()) {
      var page = this.TryReadPage(lcn);
      if (page == null || !HasSignature(page, "SUPB"u8)) continue;
      var generation = ReadUInt64(page, 0x68);
      if (bestSuperblock == null || generation > bestSuperblock.Value.Generation)
        bestSuperblock = (lcn, generation, page);
    }
    if (bestSuperblock == null)
      throw new InvalidDataException("No live ReFS superblock copy could be located.");

    this.MarkContiguousPage(bestSuperblock.Value.Lcn);
    var supb = bestSuperblock.Value.Page;
    var refsOffset = checked((int)ReadUInt32(supb, 0x70));
    var refsCount = checked((int)ReadUInt32(supb, 0x74));
    if (refsCount is < 1 or > 8 || refsOffset < 0 || refsOffset + refsCount * 8 > supb.Length)
      throw new InvalidDataException("ReFS superblock checkpoint reference list is malformed.");

    var checkpoints = new List<(ulong Lcn, ulong Clock, byte[] Page)>();
    for (var i = 0; i < refsCount; ++i) {
      var lcn = ReadUInt64(supb, refsOffset + i * 8);
      if (lcn is 0 or ulong.MaxValue) continue;
      var page = this.TryReadPage(lcn);
      if (page == null || !HasSignature(page, "CHKP"u8)) continue;
      checkpoints.Add((lcn, ReadUInt64(page, 0x60), page));
    }
    if (checkpoints.Count == 0)
      throw new InvalidDataException("ReFS superblock did not reference a readable checkpoint.");

    var active = checkpoints.MaxBy(c => c.Clock);
    this.ActiveCheckpointLcn = active.Lcn;
    this.ActiveCheckpointClock = active.Clock;
    this.MarkContiguousPage(active.Lcn);
    this.ParseCheckpoint(active.Page);
    this.LoadContainerMap();
  }

  private void ParseCheckpoint(byte[] page) {
    this.PageReferenceSize = ReadUInt32(page, 0x5C);
    if (this.PageReferenceSize is not (48 or 72 or 104))
      throw new NotSupportedException($"Unsupported ReFS page-reference size {this.PageReferenceSize}.");

    this.CheckpointFlags = ReadUInt32(page, 0x78);
    var count = checked((int)ReadUInt32(page, 0x90));
    if (count < RootCount || count > 32)
      throw new InvalidDataException($"ReFS checkpoint exposes an invalid root count ({count}).");

    var offsetListBase = (this.CheckpointFlags & 0x0200) != 0
      ? checked((int)ReadUInt32(page, 0x94))
      : 0x94;
    if (offsetListBase < 0x94 || offsetListBase + count * 4 > page.Length)
      throw new InvalidDataException("ReFS checkpoint root offset list lies outside the checkpoint page.");

    this._roots.Clear();
    for (var i = 0; i < RootCount; ++i) {
      var descriptorOffset = checked((int)ReadUInt32(page, offsetListBase + i * 4));
      if (descriptorOffset <= 0 || descriptorOffset + this.PageReferenceSize > page.Length) {
        this._roots.Add(RefsPageReference.Empty);
        continue;
      }
      this._roots.Add(RefsPageReference.Parse(page.AsSpan(descriptorOffset, checked((int)this.PageReferenceSize))));
    }
  }

  private void LoadContainerMap() {
    foreach (var rootIndex in new[] { 7, 8 }) {
      var map = new Dictionary<ulong, ulong>();
      uint cpc = 0;
      try {
        foreach (var row in this.WalkTree(this._roots[rootIndex], virtualAddresses: false)) {
          if (row.Key.Length < 16 || row.Value.Length < 0x98) continue;
          var id = BinaryPrimitives.ReadUInt64LittleEndian(row.Key);
          var rowCpc = BinaryPrimitives.ReadUInt32LittleEndian(row.Value.AsSpan(0x18, 4));
          var physical = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(row.Value.Length - 16, 8));
          if (rowCpc == 0) continue;
          if (cpc == 0) cpc = rowCpc;
          if (rowCpc != cpc) throw new InvalidDataException("Container table mixes incompatible CPC values.");
          map[id] = physical;
        }
      } catch (InvalidDataException) {
        map.Clear();
      }

      if (map.Count == 0 || cpc == 0 || (cpc & (cpc - 1)) != 0) continue;
      this._containers.Clear();
      foreach (var item in map) this._containers[item.Key] = item.Value;
      this._clustersPerContainer = cpc;
      this._containerShift = BitLength(cpc);
      return;
    }

    throw new InvalidDataException("Neither ReFS Container Table copy could be decoded.");
  }

  public ulong TranslateVirtualLcn(ulong virtualLcn) {
    if (this._clustersPerContainer == 0)
      throw new InvalidOperationException("Container map has not been loaded.");
    var container = virtualLcn >> this._containerShift;
    var within = virtualLcn & (this._clustersPerContainer - 1UL);
    if (!this._containers.TryGetValue(container, out var physicalBase))
      throw new InvalidDataException($"Virtual ReFS container {container} is not mapped.");
    return checked(physicalBase + within);
  }

  public bool TryPhysicalToVirtualLcn(ulong physicalLcn, out ulong virtualLcn) {
    foreach (var (container, start) in this._containers) {
      if (physicalLcn < start || physicalLcn >= start + this._clustersPerContainer) continue;
      var within = physicalLcn - start;
      virtualLcn = (container << this._containerShift) | within;
      return true;
    }
    virtualLcn = 0;
    return false;
  }

  public IEnumerable<RefsBTreeRow> WalkRoot(int rootIndex) {
    if (rootIndex < 0 || rootIndex >= this._roots.Count) yield break;
    var physical = rootIndex is 7 or 8 or 12;
    foreach (var row in this.WalkTree(this._roots[rootIndex], virtualAddresses: !physical))
      yield return row;
  }

  public IEnumerable<RefsBTreeRow> WalkTree(RefsPageReference root, bool virtualAddresses) {
    if (root.Lcns.Count == 0) yield break;
    var pending = new Stack<RefsPageReference>();
    pending.Push(root);
    var seen = new HashSet<(bool Virtual, ulong Head)>();

    while (pending.Count > 0) {
      var reference = pending.Pop();
      if (reference.Lcns.Count == 0) continue;

      var physicalLcns = new List<ulong>(reference.Lcns.Count);
      foreach (var lcn in reference.Lcns)
        physicalLcns.Add(virtualAddresses ? this.TranslateVirtualLcn(lcn) : lcn);
      if (!seen.Add((virtualAddresses, physicalLcns[0]))) continue;

      var page = this.TryReadPage(physicalLcns);
      if (page == null || !HasSignature(page, "MSB+"u8)) continue;
      foreach (var lcn in physicalLcns) this._visitedMetadataPages.Add(lcn);

      if (!this.TryParseNode(page, physicalLcns[0], out var node)) continue;
      if (!node.IsInner) {
        foreach (var row in node.Rows) yield return row;
        continue;
      }

      for (var i = node.Rows.Count - 1; i >= 0; --i) {
        var value = node.Rows[i].Value;
        if (value.Length < 32) continue;
        var child = RefsPageReference.Parse(value);
        if (child.Lcns.Count > 0) pending.Push(child);
      }
    }
  }

  public byte[]? TryReadPage(ulong physicalLcn) {
    var clusterCount = this.PageSize / this.ClusterSize;
    var lcns = new ulong[clusterCount];
    for (var i = 0; i < clusterCount; ++i) lcns[i] = checked(physicalLcn + (ulong)i);
    return this.TryReadPage(lcns);
  }

  private byte[]? TryReadPage(IReadOnlyList<ulong> physicalLcns) {
    if (physicalLcns.Count == 0) return null;
    var page = new byte[this.PageSize];
    var written = 0;
    foreach (var lcn in physicalLcns) {
      if (written >= page.Length) break;
      var offset = checked((long)lcn * this.ClusterSize);
      if (offset < 0 || offset + this.ClusterSize > this._stream.Length) return null;
      this._stream.Position = offset;
      var take = Math.Min(this.ClusterSize, page.Length - written);
      this._stream.ReadExactly(page.AsSpan(written, take));
      written += take;
    }
    return written == page.Length ? page : null;
  }

  private void MarkContiguousPage(ulong headLcn) {
    var clusterCount = this.PageSize / this.ClusterSize;
    for (var i = 0; i < clusterCount; ++i)
      this._visitedMetadataPages.Add(checked(headLcn + (ulong)i));
  }

  private bool TryParseNode(byte[] page, ulong physicalHeadLcn, out RefsBTreeNode node) {
    node = default!;
    if (page.Length < 0x80 || !HasSignature(page, "MSB+"u8)) return false;

    const int nodeOffsetField = 0x50;
    var rel = checked((int)ReadUInt32(page, nodeOffsetField));
    var nodeOffset = nodeOffsetField + rel;
    if (nodeOffset < 0x50 || nodeOffset + 40 > page.Length) return false;

    var nodeFlags = ReadUInt32(page, nodeOffset + 3 * 4);
    var offsetsStartRel = checked((int)ReadUInt32(page, nodeOffset + 4 * 4));
    var offsetsEndRel = checked((int)ReadUInt32(page, nodeOffset + 8 * 4));
    if (offsetsStartRel < 0 || offsetsEndRel < offsetsStartRel) return false;
    var offsetsStart = nodeOffset + offsetsStartRel;
    var offsetsEnd = nodeOffset + offsetsEndRel;
    if (offsetsStart < nodeOffset || offsetsEnd > page.Length || ((offsetsEnd - offsetsStart) & 3) != 0)
      return false;

    var rows = new List<RefsBTreeRow>((offsetsEnd - offsetsStart) / 4);
    for (var entry = offsetsStart; entry < offsetsEnd; entry += 4) {
      var encoded = ReadUInt32(page, entry);
      var rowRel = (int)(encoded & 0xFFFF);
      var rowOffset = nodeOffset + rowRel;
      if (rowOffset < nodeOffset || rowOffset + 16 > page.Length) continue;

      var rowSize = checked((int)ReadUInt32(page, rowOffset));
      if (rowSize < 16 || rowOffset + rowSize > page.Length) continue;
      var keyOffset = ReadUInt16(page, rowOffset + 4);
      var keyLength = ReadUInt16(page, rowOffset + 6);
      var flags = ReadUInt16(page, rowOffset + 8);
      var valueOffset = ReadUInt16(page, rowOffset + 10);
      var valueLength = ReadUInt16(page, rowOffset + 12);
      if (keyOffset + keyLength > rowSize || valueOffset + valueLength > rowSize) continue;

      var key = page.AsSpan(rowOffset + keyOffset, keyLength).ToArray();
      var value = page.AsSpan(rowOffset + valueOffset, valueLength).ToArray();
      rows.Add(new RefsBTreeRow(
        physicalHeadLcn,
        checked((long)physicalHeadLcn * this.ClusterSize + rowOffset),
        rowOffset,
        rowSize,
        keyOffset,
        valueOffset,
        flags,
        key,
        value));
    }

    node = new RefsBTreeNode((nodeFlags & 0x100) != 0, rows);
    return true;
  }

  private static int BitLength(uint value) {
    var bits = 0;
    while (value != 0) { ++bits; value >>= 1; }
    return bits;
  }

  private static bool HasSignature(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> signature)
    => bytes.Length >= signature.Length && bytes[..signature.Length].SequenceEqual(signature);

  private static ushort ReadUInt16(byte[] bytes, int offset)
    => offset >= 0 && offset + 2 <= bytes.Length
      ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
      : throw new InvalidDataException("ReFS metadata field lies outside its page.");

  private static uint ReadUInt32(byte[] bytes, int offset)
    => offset >= 0 && offset + 4 <= bytes.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
      : throw new InvalidDataException("ReFS metadata field lies outside its page.");

  private static ulong ReadUInt64(byte[] bytes, int offset)
    => offset >= 0 && offset + 8 <= bytes.Length
      ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8))
      : throw new InvalidDataException("ReFS metadata field lies outside its page.");
}

internal readonly record struct RefsPageReference(IReadOnlyList<ulong> Lcns) {
  public static RefsPageReference Empty { get; } = new(Array.Empty<ulong>());

  public static RefsPageReference Parse(ReadOnlySpan<byte> value) {
    if (value.Length < 8) return Empty;
    var lcns = new List<ulong>(4);
    for (var i = 0; i < 4 && i * 8 + 8 <= value.Length; ++i) {
      var lcn = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(i * 8, 8));
      if (lcn is not (0 or ulong.MaxValue)) lcns.Add(lcn);
    }
    return lcns.Count == 0 ? Empty : new RefsPageReference(lcns);
  }
}

internal sealed record RefsBTreeRow(
  ulong PhysicalPageLcn,
  long AbsoluteRowOffset,
  int RowOffset,
  int RowSize,
  int KeyOffset,
  int ValueOffset,
  ushort Flags,
  byte[] Key,
  byte[] Value) {
  public long AbsoluteValueOffset => this.AbsoluteRowOffset + this.ValueOffset;
}

internal sealed record RefsBTreeNode(bool IsInner, IReadOnlyList<RefsBTreeRow> Rows);
