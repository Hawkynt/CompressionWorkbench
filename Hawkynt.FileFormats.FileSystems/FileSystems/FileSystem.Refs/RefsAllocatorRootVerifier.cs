#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

/// <summary>
/// Verifies that clusters reserved for unpublished CoW pages are already marked
/// allocated in the allocator root that will be published with them. This is a
/// native-commit guard: a checkpoint must never make a metadata page reachable
/// while the matching allocator still advertises that cluster as free.
/// </summary>
internal static class RefsAllocatorRootVerifier {
  private const int BitmapOffset = 0x18;
  private const int BitmapBytes = 2048;
  private const ushort PartialFlag = 0x01;
  private const ushort CompactAllocatedFlag = 0x02;
  private const ushort FullyFreeFlag = 0x05;
  private const ushort FullyFreeAlternativeFlag = 0x09;

  private sealed record Row(ulong Start, ulong Length, byte[] Value);

  public static void RequireAllocated(
      RefsMetadataReader metadata,
      RefsPageReference replacementRoot,
      RefsAllocatorTier tier,
      IEnumerable<ulong> physicalClusters) {
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(physicalClusters);
    var requested = physicalClusters.Distinct().ToArray();
    if (requested.Length == 0) return;
    if (replacementRoot.Lcns.Count == 0)
      throw new InvalidDataException($"Replacement ReFS {tier} allocator root is empty.");

    var physicalAddressing = tier == RefsAllocatorTier.Small;
    var rows = new List<Row>();
    foreach (var treeRow in metadata.WalkTree(replacementRoot, virtualAddresses: !physicalAddressing)) {
      if (treeRow.Value.Length < 24) continue;
      var start = BinaryPrimitives.ReadUInt64LittleEndian(treeRow.Value.AsSpan(0, 8));
      var length = BinaryPrimitives.ReadUInt64LittleEndian(treeRow.Value.AsSpan(8, 8));
      if (length == 0 || length > BitmapBytes * 8UL) continue;
      if (!IsStructurallyValid(treeRow.Value, length)) continue;
      rows.Add(new Row(start, length, treeRow.Value));
    }
    rows.Sort((a, b) => a.Start.CompareTo(b.Start));
    if (rows.Count == 0)
      throw new InvalidDataException($"Replacement ReFS {tier} allocator root contains no decoded allocation rows.");

    foreach (var physical in requested) {
      ulong allocatorLcn;
      if (physicalAddressing) {
        allocatorLcn = physical;
      } else if (!metadata.TryPhysicalToVirtualLcn(physical, out allocatorLcn)) {
        throw new InvalidDataException(
          $"ReFS CoW target PLCN 0x{physical:X} has no VLCN mapping for the {tier} allocator.");
      }

      var row = Find(rows, allocatorLcn)
        ?? throw new InvalidDataException(
          $"Replacement ReFS {tier} allocator does not cover {(physicalAddressing ? "PLCN" : "VLCN")} 0x{allocatorLcn:X}.");
      var index = allocatorLcn - row.Start;
      if (!ReadAllocated(row.Value, row.Length, index))
        throw new InvalidDataException(
          $"Replacement ReFS {tier} allocator still marks CoW target PLCN 0x{physical:X} free.");
    }
  }

  internal static bool ReadAllocated(byte[] value, ulong rangeLength, ulong index) {
    if (index >= rangeLength) throw new InvalidDataException("ReFS allocator index lies outside its row range.");
    if (value.Length < 24) throw new InvalidDataException("ReFS allocator row is shorter than its fixed header.");
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
    return flags switch {
      PartialFlag when value.Length >= BitmapOffset + BitmapBytes
        => (value[BitmapOffset + checked((int)(index >> 3))] & (1 << checked((int)(index & 7)))) != 0,
      CompactAllocatedFlag => true,
      FullyFreeFlag or FullyFreeAlternativeFlag => false,
      _ => throw new InvalidDataException($"ReFS allocator row has unsupported flags 0x{flags:X4}/size {value.Length}.")
    };
  }

  internal static bool IsStructurallyValid(byte[] value, ulong rangeLength) {
    if (value.Length < 24 || rangeLength == 0 || rangeLength > BitmapBytes * 8UL) return false;
    var free = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x10, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x12, 2));
    var used = BinaryPrimitives.ReadUInt16LittleEndian(value.AsSpan(0x16, 2));
    if ((ulong)free + used != rangeLength) return false;

    if (flags == PartialFlag) {
      if (value.Length < BitmapOffset + BitmapBytes) return false;
      var popcount = 0;
      for (ulong i = 0; i < rangeLength; ++i)
        if ((value[BitmapOffset + checked((int)(i >> 3))] & (1 << checked((int)(i & 7)))) != 0) ++popcount;
      return popcount == used;
    }
    if (flags == CompactAllocatedFlag) return free == 0 && used == rangeLength;
    if (flags is FullyFreeFlag or FullyFreeAlternativeFlag) return used == 0 && free == rangeLength;
    return false;
  }

  private static Row? Find(IReadOnlyList<Row> rows, ulong allocatorLcn) {
    var lo = 0;
    var hi = rows.Count - 1;
    while (lo <= hi) {
      var mid = lo + ((hi - lo) >> 1);
      var row = rows[mid];
      if (allocatorLcn < row.Start) { hi = mid - 1; continue; }
      if (allocatorLcn - row.Start >= row.Length) { lo = mid + 1; continue; }
      return row;
    }
    return null;
  }
}
