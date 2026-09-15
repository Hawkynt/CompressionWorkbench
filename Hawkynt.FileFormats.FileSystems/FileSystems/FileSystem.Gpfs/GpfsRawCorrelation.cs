#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Gpfs;

/// <summary>
/// Clean-room correlation helpers for raw GPFS evidence. Every result is a
/// candidate constrained by an IBM oracle; none of these helpers declares an
/// on-disk field layout by itself.
/// </summary>
internal static class GpfsRawCorrelation {
  internal static long GetByteOffset(
    GpfsDiskAddress address,
    int sectorBytes,
    int recordBytes,
    long imageLength) {
    if (address.DiskId < 0 || address.Sector < 0)
      throw new ArgumentOutOfRangeException(nameof(address));
    if (sectorBytes <= 0)
      throw new ArgumentOutOfRangeException(nameof(sectorBytes));
    if (recordBytes <= 0)
      throw new ArgumentOutOfRangeException(nameof(recordBytes));
    if (imageLength < 0)
      throw new ArgumentOutOfRangeException(nameof(imageLength));

    long offset;
    try {
      offset = checked(address.Sector * sectorBytes);
    } catch (OverflowException ex) {
      throw new InvalidDataException($"GPFS disk address {address} overflows the image byte address space.", ex);
    }

    if (offset > imageLength || recordBytes > imageLength - offset)
      throw new InvalidDataException(
        $"GPFS disk address {address} resolves to byte {offset:N0}, outside the {imageLength:N0}-byte image for a {recordBytes:N0}-byte record.");

    return offset;
  }

  internal static IReadOnlyList<GpfsInodeReplicaBytes> ReadInodeReplicas(
    GpfsEvidenceManifest manifest,
    GpfsInodeOracle inode,
    IReadOnlyDictionary<int, Stream> imagesByDiskId) {
    ArgumentNullException.ThrowIfNull(manifest);
    ArgumentNullException.ThrowIfNull(inode);
    ArgumentNullException.ThrowIfNull(imagesByDiskId);
    if (inode.InodeSize <= 0)
      throw new InvalidDataException("IBM inode oracle reports a non-positive inode size.");

    var nsdsByDiskId = manifest.Nsds.ToDictionary(static x => x.DiskId);
    var result = new List<GpfsInodeReplicaBytes>(inode.PhysicalAddresses.Count);
    foreach (var address in inode.PhysicalAddresses) {
      if (!nsdsByDiskId.TryGetValue(address.DiskId, out var nsd))
        throw new InvalidDataException($"IBM inode oracle refers to disk {address.DiskId}, which is absent from the capture manifest.");
      if (!imagesByDiskId.TryGetValue(address.DiskId, out var image))
        throw new InvalidDataException($"No raw NSD stream was supplied for GPFS disk {address.DiskId}.");
      if (!image.CanRead || !image.CanSeek)
        throw new ArgumentException($"Raw NSD stream for disk {address.DiskId} must be readable and seekable.", nameof(imagesByDiskId));
      if (image.Length != nsd.DeviceSize)
        throw new InvalidDataException(
          $"Raw NSD stream for '{nsd.Name}' has length {image.Length:N0}, but the manifest records {nsd.DeviceSize:N0} bytes.");

      var bytes = new byte[inode.InodeSize];
      var originalPosition = image.Position;
      try {
        image.Position = GetByteOffset(address, nsd.SectorBytes, bytes.Length, image.Length);
        image.ReadExactly(bytes);
      } finally {
        image.Position = originalPosition;
      }
      result.Add(new GpfsInodeReplicaBytes(address, nsd.Name, bytes));
    }

    return result;
  }

  internal static IReadOnlyList<GpfsUInt32FieldCandidate> FindUInt32Candidates(ReadOnlySpan<byte> bytes, uint value) {
    var result = new List<GpfsUInt32FieldCandidate>();
    for (var offset = 0; offset <= bytes.Length - sizeof(uint); ++offset) {
      var slice = bytes[offset..];
      if (BinaryPrimitives.ReadUInt32LittleEndian(slice) == value)
        result.Add(new GpfsUInt32FieldCandidate(offset, GpfsByteOrder.LittleEndian));
      if (BinaryPrimitives.ReadUInt32BigEndian(slice) == value)
        result.Add(new GpfsUInt32FieldCandidate(offset, GpfsByteOrder.BigEndian));
    }
    return result.Distinct().ToArray();
  }

  internal static IReadOnlyList<GpfsAddressFieldCandidate> FindAddressCandidates(
    ReadOnlySpan<byte> bytes,
    GpfsDiskAddress address) {
    if (address.DiskId < 0 || address.Sector < 0)
      throw new ArgumentOutOfRangeException(nameof(address));

    ReadOnlySpan<int> diskWidths = [1, 2, 4, 8];
    ReadOnlySpan<int> sectorWidths = [4, 8];
    ReadOnlySpan<bool> sectorOrders = [false, true];
    var encoded = new byte[16];
    var result = new List<GpfsAddressFieldCandidate>();
    foreach (var diskWidth in diskWidths) {
      if (!FitsUnsigned((ulong)address.DiskId, diskWidth))
        continue;
      foreach (var sectorWidth in sectorWidths) {
        if (!FitsUnsigned((ulong)address.Sector, sectorWidth))
          continue;
        foreach (var byteOrder in Enum.GetValues<GpfsByteOrder>()) {
          foreach (var sectorFirst in sectorOrders) {
            var width = diskWidth + sectorWidth;
            var destination = encoded.AsSpan(0, width);
            if (sectorFirst) {
              WriteUnsigned(destination[..sectorWidth], (ulong)address.Sector, byteOrder);
              WriteUnsigned(destination[sectorWidth..], (ulong)address.DiskId, byteOrder);
            } else {
              WriteUnsigned(destination[..diskWidth], (ulong)address.DiskId, byteOrder);
              WriteUnsigned(destination[diskWidth..], (ulong)address.Sector, byteOrder);
            }

            for (var offset = 0; offset <= bytes.Length - width; ++offset)
              if (bytes.Slice(offset, width).SequenceEqual(destination))
                result.Add(new GpfsAddressFieldCandidate(offset, diskWidth, sectorWidth, byteOrder, sectorFirst));
          }
        }
      }
    }

    return result.Distinct().ToArray();
  }

  internal static IReadOnlyList<GpfsAddressFieldCandidate> IntersectAddressCandidates(
    ReadOnlySpan<byte> firstBytes,
    GpfsDiskAddress firstAddress,
    ReadOnlySpan<byte> secondBytes,
    GpfsDiskAddress secondAddress) {
    var first = FindAddressCandidates(firstBytes, firstAddress).ToHashSet();
    first.IntersectWith(FindAddressCandidates(secondBytes, secondAddress));
    return first.OrderBy(static x => x.Offset)
      .ThenBy(static x => x.DiskWidth)
      .ThenBy(static x => x.SectorWidth)
      .ThenBy(static x => x.ByteOrder)
      .ThenBy(static x => x.SectorFirst)
      .ToArray();
  }

  internal static IReadOnlyList<GpfsWordVectorCandidate> FindWordVectorCandidates(
    ReadOnlySpan<byte> bytes,
    IReadOnlyList<ulong> words) {
    ArgumentNullException.ThrowIfNull(words);
    if (words.Count == 0)
      return [];

    var byteCount = checked(words.Count * sizeof(ulong));
    if (byteCount > bytes.Length)
      return [];

    var result = new List<GpfsWordVectorCandidate>();
    foreach (var byteOrder in Enum.GetValues<GpfsByteOrder>()) {
      var encoded = new byte[byteCount];
      for (var i = 0; i < words.Count; ++i) {
        var slice = encoded.AsSpan(i * sizeof(ulong), sizeof(ulong));
        if (byteOrder == GpfsByteOrder.LittleEndian)
          BinaryPrimitives.WriteUInt64LittleEndian(slice, words[i]);
        else
          BinaryPrimitives.WriteUInt64BigEndian(slice, words[i]);
      }

      for (var offset = 0; offset <= bytes.Length - encoded.Length; ++offset)
        if (bytes.Slice(offset, encoded.Length).SequenceEqual(encoded))
          result.Add(new GpfsWordVectorCandidate(offset, byteOrder, words.Count));
    }

    return result;
  }

  internal static IReadOnlyList<GpfsBitmapOrderCandidate> InferBitmapOrder(
    IReadOnlyList<GpfsBitmapTransitionObservation> observations) {
    ArgumentNullException.ThrowIfNull(observations);
    if (observations.Count < 2)
      return [];

    var result = new List<GpfsBitmapOrderCandidate>();
    TryOrder(lsbFirst: true);
    TryOrder(lsbFirst: false);
    return result;

    void TryOrder(bool lsbFirst) {
      var first = observations[0];
      var firstPhysicalBit = checked((long)first.Transition.ByteOffset * 8 + BitWithinByte(first.Transition, lsbFirst));
      var baseBit = checked(firstPhysicalBit - first.LogicalIndex);
      for (var i = 1; i < observations.Count; ++i) {
        var item = observations[i];
        var physicalBit = checked((long)item.Transition.ByteOffset * 8 + BitWithinByte(item.Transition, lsbFirst));
        if (physicalBit - item.LogicalIndex != baseBit)
          return;
      }
      result.Add(new GpfsBitmapOrderCandidate(lsbFirst, baseBit));
    }
  }

  private static int BitWithinByte(GpfsBitTransition transition, bool lsbFirst)
    => lsbFirst ? transition.LsbFirstBitIndex : transition.MsbFirstBitIndex;

  private static bool FitsUnsigned(ulong value, int width)
    => width == sizeof(ulong) || value < (1UL << (width * 8));

  private static void WriteUnsigned(Span<byte> destination, ulong value, GpfsByteOrder byteOrder) {
    if (byteOrder == GpfsByteOrder.LittleEndian) {
      for (var i = 0; i < destination.Length; ++i) {
        destination[i] = (byte)value;
        value >>= 8;
      }
    } else {
      for (var i = destination.Length - 1; i >= 0; --i) {
        destination[i] = (byte)value;
        value >>= 8;
      }
    }
  }
}

internal enum GpfsByteOrder {
  LittleEndian,
  BigEndian,
}

internal sealed record GpfsInodeReplicaBytes(GpfsDiskAddress Address, string NsdName, byte[] Bytes);
internal readonly record struct GpfsUInt32FieldCandidate(int Offset, GpfsByteOrder ByteOrder);
internal readonly record struct GpfsAddressFieldCandidate(
  int Offset,
  int DiskWidth,
  int SectorWidth,
  GpfsByteOrder ByteOrder,
  bool SectorFirst);
internal readonly record struct GpfsWordVectorCandidate(int Offset, GpfsByteOrder ByteOrder, int WordCount);
internal readonly record struct GpfsBitmapTransitionObservation(long LogicalIndex, GpfsBitTransition Transition);
internal readonly record struct GpfsBitmapOrderCandidate(bool LsbFirst, long BaseBit);
