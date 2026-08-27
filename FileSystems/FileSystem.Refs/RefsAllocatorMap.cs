#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Decodes the three ReFS allocator B+ trees into physical allocation state.
/// The returned map is conservative: ambiguous compact rows are treated as
/// allocated. A cluster is considered verified-free only when a decoded
/// allocator row explicitly says so.
/// </summary>
internal static class RefsAllocatorMap {
  internal sealed record State(
    IReadOnlyList<(ulong Start, ulong Count)> Allocated,
    IReadOnlyList<(ulong Start, ulong Count)> Free,
    IReadOnlyList<(ulong Start, ulong Count)> Covered);

  public static State Read(RefsMetadataReader metadata) {
    var allocated = new List<ulong>();
    var free = new List<ulong>();
    var covered = new List<ulong>();

    foreach (var rootIndex in new[] { 1, 2, 12 }) {
      var physicalAddressing = rootIndex == 12;
      IEnumerable<RefsBTreeRow> rows;
      try { rows = metadata.WalkRoot(rootIndex).ToArray(); }
      catch (InvalidDataException) { continue; }

      foreach (var row in rows) {
        var value = row.Value;
        if (value.Length < 24) continue;
        var rangeStart = ReadU64(value, 0x00);
        var rangeLength = ReadU64(value, 0x08);
        var freeCount = ReadU16(value, 0x10);
        var flags = ReadU16(value, 0x12);
        var usedCount = ReadU16(value, 0x16);
        if (rangeLength == 0 || rangeLength > 1_048_576) continue;

        // A bitmap row describes at most 16384 allocation bits. Compact rows
        // can cover the same logical range without the 2048-byte bitmap.
        var hasBitmap = value.Length >= 24 + 2048;
        var maxBits = hasBitmap ? Math.Min(rangeLength, 16384UL) : rangeLength;
        if (maxBits == 0) continue;

        for (ulong i = 0; i < maxBits; ++i) {
          ulong address;
          try {
            var allocatorLcn = checked(rangeStart + i);
            address = physicalAddressing ? allocatorLcn : metadata.TranslateVirtualLcn(allocatorLcn);
          } catch (Exception e) when (e is InvalidDataException or OverflowException) {
            continue;
          }

          covered.Add(address);
          bool isAllocated;
          if (hasBitmap) {
            var byteIndex = checked((int)(i >> 3));
            var bit = 1 << checked((int)(i & 7));
            isAllocated = (value[0x18 + byteIndex] & bit) != 0;
          } else if (flags is 0x05 or 0x09 || usedCount == 0) {
            isAllocated = false;
          } else if (freeCount == 0 || flags == 0x02) {
            // Compact rows without an explicit free marker are fail-closed.
            isAllocated = true;
          } else {
            isAllocated = true;
          }

          (isAllocated ? allocated : free).Add(address);
        }
      }
    }

    return new State(Coalesce(allocated), Coalesce(free), Coalesce(covered));
  }

  private static IReadOnlyList<(ulong Start, ulong Count)> Coalesce(List<ulong> clusters) {
    if (clusters.Count == 0) return [];
    clusters.Sort();
    var result = new List<(ulong Start, ulong Count)>();
    var start = clusters[0];
    var previous = start;
    for (var i = 1; i < clusters.Count; ++i) {
      var current = clusters[i];
      if (current == previous) continue;
      if (current == previous + 1) {
        previous = current;
        continue;
      }
      result.Add((start, previous - start + 1));
      start = previous = current;
    }
    result.Add((start, previous - start + 1));
    return result;
  }

  private static ushort ReadU16(byte[] bytes, int offset)
    => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
  private static ulong ReadU64(byte[] bytes, int offset)
    => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
}
