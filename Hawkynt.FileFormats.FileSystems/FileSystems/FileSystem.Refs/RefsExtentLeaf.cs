using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>One extent entry of an embedded extent leaf; <see cref="Crcs"/> holds an integrity stream's per-cluster CRC32C values.</summary>
internal sealed record RefsExtent(ulong Vlcn, uint Flags, uint Vcn, uint Run, uint[]? Crcs) {
  public bool IsIntegrity => this.Crcs is not null;

  /// <summary>The entry's size: 24 bytes, plus 4 per cluster for an integrity stream.</summary>
  public int Size => 0x18 + (this.Crcs is null ? 0 : 4 * this.Crcs.Length);
}

/// <summary>
/// The embedded extent leaf a file record holds in its directory row, as Windows Server 2022
/// (ReFS 3.7) and 2025 (ReFS 3.14) write it (measured on volumes they formatted and edited):
/// <list type="bullet">
/// <item>header [data start 0x28, data end, free, flags, index start, count, …, index end at
/// +0x20], flags 0x0600 on 3.7 and 0x0E00 on 3.14;</item>
/// <item>entries from +0x28, each 8-byte aligned: [VLCN u64, flags u32 (entry size in the high
/// half), VCN u32, u32 0, cluster count u32], an integrity stream's entry followed by the
/// CRC32C of each of its clusters (entry size 0x18 + 4 × count);</item>
/// <item>an index of one u32 per entry ending at the index end: offset | 0xFFFF0000 on 3.7,
/// offset | VCN &lt;&lt; 16 on 3.14.</item>
/// </list>
/// A 3.7 leaf keeps its size and the entries take its free space. A 3.14 leaf is sized to fit:
/// index end = max(0x70, align8(data end + 4 × count)), and the record around it grows or
/// shrinks with it — the attribute row holding the leaf (row size and value length) and the
/// record's node (data end, index start, index end) — so the directory row's value changes
/// length, as on Windows-written volumes.
/// </summary>
internal static class RefsExtentLeaf {
  private const uint Legacy = 0x0600;
  private const uint Current = 0x0E00;

  /// <summary>The offset of the one leaf holding <paramref name="count"/> entries, or -1 when there is none or more than one.</summary>
  public static int Find(byte[] value, uint count) {
    var found = -1;
    for (var p = 0x80; p + 0x28 <= value.Length; p += 4) {
      if (U32(value, p) != 0x28 || U32(value, p + 12) is not (Legacy or Current) || U32(value, p + 20) != count) continue;
      var end = U32(value, p + 4);
      var free = U32(value, p + 8);
      var indexStart = U32(value, p + 16);
      var indexEnd = U32(value, p + 0x20);
      if (end < 0x28 || end > indexStart || indexStart != indexEnd - count * 4 || free != indexStart - end || p + indexEnd > value.Length) continue;
      if (found >= 0) return -1;
      found = p;
    }
    return found;
  }

  /// <summary>The entries in index (file) order, or null when one is of a shape this editor does not rewrite.</summary>
  public static List<RefsExtent>? Read(byte[] value, int leaf) {
    var end = (int)U32(value, leaf + 4);
    var count = (int)U32(value, leaf + 20);
    var indexStart = (int)U32(value, leaf + 16);
    var entries = new List<RefsExtent>(count);
    for (var i = 0; i < count; ++i) {
      var at = (int)(U32(value, leaf + indexStart + i * 4) & 0xFFFF);
      if (at < 0x28 || at + 0x18 > end) return null;
      var e = leaf + at;
      var flags = U32(value, e + 8);
      var run = U32(value, e + 20);
      var size = (int)(flags >> 16);
      if (U32(value, e + 16) != 0) return null;
      uint[]? crcs = null;
      if (size != 0x18) {
        if (size != 0x18 + 4 * (long)run || at + size > end) return null;
        crcs = new uint[run];
        for (var c = 0; c < run; ++c) crcs[c] = U32(value, e + 0x18 + 4 * c);
      }
      entries.Add(new RefsExtent(U64(value, e), flags, U32(value, e + 12), run, crcs));
    }
    return entries;
  }

  /// <summary>
  /// <paramref name="value"/> with the leaf at <paramref name="leaf"/> holding
  /// <paramref name="entries"/>; a new array when the leaf (ReFS 3.14) changes size. Refused
  /// when a 3.7 leaf has no room or a 3.14 record is not laid out as described above.
  /// </summary>
  public static byte[] Write(byte[] value, int leaf, IReadOnlyList<RefsExtent> entries, string name) {
    var flags = U32(value, leaf + 12);
    var oldIndexEnd = (int)U32(value, leaf + 0x20);
    var body = new List<byte>();
    var offsets = new List<int>(entries.Count);
    foreach (var entry in entries) {
      offsets.Add(0x28 + body.Count);
      var bytes = new byte[(entry.Size + 7) & ~7];
      W64(bytes, 0, entry.Vlcn);
      W32(bytes, 8, (entry.Flags & 0xFFFF) | (uint)entry.Size << 16);
      W32(bytes, 12, entry.Vcn);
      W32(bytes, 20, entry.Run);
      if (entry.Crcs is { } crcs)
        for (var c = 0; c < crcs.Length; ++c) W32(bytes, 0x18 + 4 * c, crcs[c]);
      body.AddRange(bytes);
    }
    var dataEnd = 0x28 + body.Count;
    var indexBytes = entries.Count * 4;
    int indexEnd;
    if (flags == Legacy) {
      indexEnd = oldIndexEnd;
      if (dataEnd + indexBytes > indexEnd)
        throw new NotSupportedException($"ReFS: the extent table of '{name}' has no room for its entries in the record.");
    } else {
      indexEnd = Math.Max(0x70, (dataEnd + indexBytes + 7) & ~7);
      if (entries.Any(static e => e.Vcn > 0xFFFF))
        throw new NotSupportedException($"ReFS: the extent table of '{name}' reaches past what its index records.");
    }
    var leafBytes = new byte[indexEnd];
    value.AsSpan(leaf, 0x28).CopyTo(leafBytes);
    body.CopyTo(leafBytes, 0x28);
    var indexStart = indexEnd - indexBytes;
    W32(leafBytes, 4, (uint)dataEnd);
    W32(leafBytes, 8, (uint)(indexStart - dataEnd));
    W32(leafBytes, 16, (uint)indexStart);
    W32(leafBytes, 20, (uint)entries.Count);
    W32(leafBytes, 0x20, (uint)indexEnd);
    for (var i = 0; i < entries.Count; ++i)
      W32(leafBytes, indexStart + i * 4, (uint)offsets[i] | (flags == Legacy ? 0xFFFF0000u : entries[i].Vcn << 16));

    var delta = indexEnd - oldIndexEnd;
    if (delta == 0) {
      var same = value.ToArray();
      leafBytes.CopyTo(same, leaf);
      return same;
    }
    // ReFS 3.14: the attribute row holding the leaf and the record's node move with it.
    var node = (int)U32(value, 0);
    if (node < 0x18 || node + 0x28 > value.Length || U32(value, node) != 0x28)
      throw new NotSupportedException($"ReFS: the record of '{name}' is not laid out as this editor expects.");
    var nodeIndexStart = (int)U32(value, node + 16);
    var nodeCount = (int)U32(value, node + 20);
    var nodeIndexEnd = (int)U32(value, node + 0x20);
    if (node + nodeIndexEnd != value.Length || nodeIndexStart + nodeCount * 4 != nodeIndexEnd)
      throw new NotSupportedException($"ReFS: the record of '{name}' is not laid out as this editor expects.");
    var holder = -1;
    for (var i = 0; i < nodeCount; ++i) {
      var r = node + (int)(U32(value, node + nodeIndexStart + i * 4) & 0xFFFF);
      if (r <= node || r + 16 > value.Length || r >= leaf) continue;
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(r + 10));
      var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(r + 12));
      if (r + valueOffset + valueLength == leaf + oldIndexEnd && U32(value, r) == (uint)((valueOffset + valueLength + 7) & ~7)) holder = r;
    }
    if (holder < 0) throw new NotSupportedException($"ReFS: the extent leaf of '{name}' does not close its attribute row.");

    var result = new byte[value.Length + delta];
    value.AsSpan(0, leaf).CopyTo(result);
    leafBytes.CopyTo(result, leaf);
    value.AsSpan(leaf + oldIndexEnd).CopyTo(result.AsSpan(leaf + indexEnd));
    W32(result, holder, (uint)((int)U32(result, holder) + delta));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(holder + 12), checked((ushort)(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(holder + 12)) + delta)));
    W32(result, node + 4, (uint)((int)U32(result, node + 4) + delta));
    W32(result, node + 16, (uint)(nodeIndexStart + delta));
    W32(result, node + 0x20, (uint)(nodeIndexEnd + delta));
    for (var i = 0; i < nodeCount; ++i) {
      var p = node + nodeIndexStart + delta + i * 4;
      var entry = U32(result, p);
      if (node + (int)(entry & 0xFFFF) > holder) W32(result, p, entry + (uint)delta);
    }
    return result;
  }

  /// <summary>The CRC32C of each cluster of <paramref name="data"/>, the last one padded with zeros as it is written.</summary>
  public static uint[] ClusterCrcs(byte[] data, int clusterSize, long firstCluster, int clusters) {
    var crcs = new uint[clusters];
    var buffer = new byte[clusterSize];
    for (var i = 0; i < clusters; ++i) {
      Array.Clear(buffer);
      var offset = (firstCluster + i) * clusterSize;
      if (offset < data.LongLength) data.AsSpan((int)offset, (int)Math.Min(clusterSize, data.LongLength - offset)).CopyTo(buffer);
      crcs[i] = RefsChecksum.Crc32C(buffer);
    }
    return crcs;
  }

  /// <summary>Writes <paramref name="data"/> over <paramref name="clusters"/> in whole clusters, the last one padded with zeros.</summary>
  public static void WritePadded(Stream image, int clusterSize, IReadOnlyList<ulong> clusters, byte[] data) {
    var buffer = new byte[clusterSize];
    for (var i = 0; i < clusters.Count; ++i) {
      Array.Clear(buffer);
      var offset = (long)i * clusterSize;
      if (offset < data.LongLength) data.AsSpan((int)offset, (int)Math.Min(clusterSize, data.LongLength - offset)).CopyTo(buffer);
      image.Position = (long)clusters[i] * clusterSize;
      image.Write(buffer);
    }
    image.Flush();
  }

  private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4));
  private static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8));
  private static void W32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
  private static void W64(byte[] b, int o, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o, 8), v);
}
