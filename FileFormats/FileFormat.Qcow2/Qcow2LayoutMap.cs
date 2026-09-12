#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Qcow2;

/// <summary>
/// Walks the active QCOW2 mapping plus the refcount metadata and emits a
/// fail-closed byte-level host layout. Host clusters with refcount zero are
/// explicitly reported as free, enabling forensic wipe without confusing guest
/// disk offsets with container offsets. Allocated clusters not understood by the
/// active mapping (for example snapshot metadata/data) remain metadata-reserved.
/// </summary>
public static class Qcow2LayoutMap {
  private sealed record KnownBlock(DefragBlockKind Kind, string? Name, DefragBlockClass? Classification);

  /// <summary>Enumerates the physical host-file layout.</summary>
  public static IEnumerable<DefragBlockInfo> Enumerate(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    try {
      return Build(stream);
    } catch (InvalidDataException) {
      return [];
    } catch (NotSupportedException) {
      return [];
    } catch (IOException) {
      return [];
    } catch (OverflowException) {
      return [];
    }
  }

  private static IReadOnlyList<DefragBlockInfo> Build(Stream stream) {
    if (!stream.CanRead || !stream.CanSeek)
      return [];

    var header = Qcow2Structures.ReadHeader(stream);
    if (header.CryptMethod != 0 || header.IncompatibleFeatures != 0 || header.RefcountOrder != 4)
      return [];
    if (header.RefcountTableOffset <= 0 || header.RefcountTableClusters <= 0)
      return [];

    var clusterSize = header.ClusterSize;
    var hostClusterCount = (stream.Length + clusterSize - 1) / clusterSize;
    if (hostClusterCount == 0)
      return [];

    var known = new Dictionary<long, KnownBlock>();
    MarkRange(known, 0, clusterSize, clusterSize,
      new KnownBlock(DefragBlockKind.MetadataReserved, $"QCOW2 Header (v{header.Version})", DefragBlockClass.Directory));

    if (header.L1Size > 0) {
      var l1Bytes = checked((long)header.L1Size * sizeof(ulong));
      var l1Span = Qcow2Structures.AlignUp(l1Bytes, clusterSize);
      MarkRange(known, header.L1TableOffset, l1Span, clusterSize,
        new KnownBlock(DefragBlockKind.MetadataReserved, "L1 table", DefragBlockClass.Directory));
    }

    MarkRange(
      known,
      header.RefcountTableOffset,
      checked((long)header.RefcountTableClusters * clusterSize),
      clusterSize,
      new KnownBlock(DefragBlockKind.MetadataReserved, "Refcount table", DefragBlockClass.Directory));

    var refcountTableCapacity = checked((long)header.RefcountTableClusters * clusterSize / sizeof(ulong));
    var refcountBlocksNeeded = (hostClusterCount + header.RefcountEntriesPerBlock - 1) / header.RefcountEntriesPerBlock;
    if (refcountBlocksNeeded > refcountTableCapacity)
      return [];

    for (var tableIndex = 0L; tableIndex < refcountBlocksNeeded; ++tableIndex) {
      var tableEntry = Qcow2Structures.ReadUInt64BigEndianAt(
        stream,
        checked(header.RefcountTableOffset + tableIndex * sizeof(ulong)));
      var blockOffset = Qcow2Structures.ReadRefcountBlockOffset(tableEntry);
      if (blockOffset == 0)
        continue;
      if ((blockOffset & (clusterSize - 1L)) != 0 || blockOffset > stream.Length - clusterSize)
        return [];
      MarkRange(known, blockOffset, clusterSize, clusterSize,
        new KnownBlock(DefragBlockKind.MetadataReserved, "Refcount block", DefragBlockClass.Directory));
    }

    var guestClusterCount = (header.VirtualSize + clusterSize - 1) / clusterSize;
    for (var l1Index = 0; l1Index < header.L1Size; ++l1Index) {
      var l1Entry = Qcow2Structures.ReadUInt64BigEndianAt(
        stream,
        checked(header.L1TableOffset + l1Index * sizeof(ulong)));
      if ((l1Entry & Qcow2Structures.L1ReservedMask) != 0)
        return [];
      var l2TableOffset = Qcow2Structures.ReadClusterOffset(l1Entry);
      if (l2TableOffset == 0)
        continue;
      if ((l2TableOffset & (clusterSize - 1L)) != 0 || l2TableOffset > stream.Length - clusterSize)
        return [];

      MarkRange(known, l2TableOffset, clusterSize, clusterSize,
        new KnownBlock(DefragBlockKind.MetadataReserved, "L2 table", DefragBlockClass.Directory));

      var guestBase = (long)l1Index * header.L2Entries;
      var entries = (int)Math.Min(header.L2Entries, Math.Max(0, guestClusterCount - guestBase));
      for (var l2Index = 0; l2Index < entries; ++l2Index) {
        var l2Entry = Qcow2Structures.ReadUInt64BigEndianAt(
          stream,
          checked(l2TableOffset + l2Index * sizeof(ulong)));
        if (l2Entry == 0)
          continue;

        if ((l2Entry & Qcow2Structures.CompressedFlag) != 0) {
          var (compressedOffset, compressedLength) =
            Qcow2Structures.DecodeCompressedRange(l2Entry, header.ClusterBits, stream.Length);
          MarkRange(known, compressedOffset, compressedLength, clusterSize,
            new KnownBlock(DefragBlockKind.Used, "Guest data (compressed)", DefragBlockClass.Cold));
          continue;
        }

        if ((l2Entry & Qcow2Structures.StandardL2ReservedMask) != 0)
          return [];
        if (header.Version == 2 && (l2Entry & Qcow2Structures.ZeroFlag) != 0)
          return [];

        var hostOffset = Qcow2Structures.ReadClusterOffset(l2Entry);
        if (hostOffset == 0)
          continue;
        if ((hostOffset & (clusterSize - 1L)) != 0 || hostOffset > stream.Length - clusterSize)
          return [];

        var isZero = header.Version >= 3 && (l2Entry & Qcow2Structures.ZeroFlag) != 0;
        MarkRange(
          known,
          hostOffset,
          clusterSize,
          clusterSize,
          isZero
            ? new KnownBlock(DefragBlockKind.Free, "Zero-cluster preallocation", null)
            : new KnownBlock(DefragBlockKind.Used, "Guest data", DefragBlockClass.Normal));
      }
    }

    var result = new List<DefragBlockInfo>();
    long cachedTableIndex = -1;
    byte[]? cachedRefcountBlock = null;

    for (var hostCluster = 0L; hostCluster < hostClusterCount; ++hostCluster) {
      var refcount = ReadRefcount(hostCluster);
      known.TryGetValue(hostCluster, out var knownBlock);
      if (knownBlock is { Kind: not DefragBlockKind.Free } && refcount == 0)
        return [];

      var block = knownBlock ?? (refcount == 0
        ? new KnownBlock(DefragBlockKind.Free, null, null)
        : new KnownBlock(DefragBlockKind.MetadataReserved, "Allocated QCOW2 structure/snapshot", DefragBlockClass.Directory));
      var offset = checked(hostCluster * (long)clusterSize);
      var length = Math.Min(clusterSize, stream.Length - offset);
      AppendCoalesced(result, offset, length, block);
    }

    return result;

    ushort ReadRefcount(long hostCluster) {
      var tableIndex = hostCluster / header.RefcountEntriesPerBlock;
      var blockIndex = checked((int)(hostCluster % header.RefcountEntriesPerBlock));
      if (tableIndex != cachedTableIndex) {
        var tableEntry = Qcow2Structures.ReadUInt64BigEndianAt(
          stream,
          checked(header.RefcountTableOffset + tableIndex * sizeof(ulong)));
        var blockOffset = Qcow2Structures.ReadRefcountBlockOffset(tableEntry);
        cachedTableIndex = tableIndex;
        if (blockOffset == 0) {
          cachedRefcountBlock = null;
        } else {
          if ((blockOffset & (clusterSize - 1L)) != 0 || blockOffset > stream.Length - clusterSize)
            throw new InvalidDataException("QCOW2: refcount block points outside the image.");
          cachedRefcountBlock = new byte[clusterSize];
          Qcow2Structures.ReadExactlyAt(stream, blockOffset, cachedRefcountBlock);
        }
      }

      if (cachedRefcountBlock is null)
        return 0;
      return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(
        cachedRefcountBlock.AsSpan(blockIndex * sizeof(ushort), sizeof(ushort)));
    }
  }

  private static void MarkRange(
      Dictionary<long, KnownBlock> known,
      long offset,
      long length,
      int clusterSize,
      KnownBlock block) {
    if (offset < 0 || length <= 0)
      throw new InvalidDataException("QCOW2: invalid mapped host range.");
    var first = offset / clusterSize;
    var last = checked((offset + length - 1) / clusterSize);
    for (var cluster = first; cluster <= last; ++cluster) {
      if (!known.TryGetValue(cluster, out var existing) || Rank(block.Kind) > Rank(existing.Kind))
        known[cluster] = block;
    }
  }

  private static int Rank(DefragBlockKind kind) => kind switch {
    DefragBlockKind.MetadataReserved => 3,
    DefragBlockKind.Used => 2,
    DefragBlockKind.Free => 1,
    _ => 3,
  };

  private static void AppendCoalesced(List<DefragBlockInfo> result, long offset, long length, KnownBlock block) {
    if (length <= 0)
      return;
    if (result.Count > 0) {
      var previous = result[^1];
      if (previous.Offset + previous.Length == offset
          && previous.Kind == block.Kind
          && previous.FileName == block.Name
          && previous.Classification == block.Classification) {
        result[^1] = previous with { Length = previous.Length + length };
        return;
      }
    }
    result.Add(new DefragBlockInfo(offset, length, block.Kind, block.Name, block.Classification));
  }
}
