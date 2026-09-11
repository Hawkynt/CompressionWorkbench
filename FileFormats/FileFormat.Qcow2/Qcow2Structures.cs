#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;

namespace FileFormat.Qcow2;

internal readonly record struct Qcow2Header(
  uint Version,
  ulong BackingFileOffset,
  uint BackingFileSize,
  int ClusterBits,
  long VirtualSize,
  uint CryptMethod,
  int L1Size,
  long L1TableOffset,
  long RefcountTableOffset,
  int RefcountTableClusters,
  uint SnapshotCount,
  long SnapshotsOffset,
  ulong IncompatibleFeatures,
  ulong CompatibleFeatures,
  ulong AutoclearFeatures,
  uint RefcountOrder,
  uint HeaderLength
) {
  public int ClusterSize => 1 << this.ClusterBits;
  public int L2Entries => this.ClusterSize / sizeof(ulong);
  public int RefcountBits => 1 << checked((int)this.RefcountOrder);
  public int RefcountEntriesPerBlock => checked(this.ClusterSize * 8 / this.RefcountBits);
}

internal static class Qcow2Structures {
  internal static ReadOnlySpan<byte> Magic => [0x51, 0x46, 0x49, 0xFB];

  internal const ulong CopiedFlag = 1UL << 63;
  internal const ulong CompressedFlag = 1UL << 62;
  internal const ulong ZeroFlag = 1UL;
  internal const ulong ClusterOffsetMask = 0x00FF_FFFF_FFFF_FE00UL;
  internal const ulong L1ReservedMask = 0x7F00_0000_0000_01FFUL;
  internal const ulong StandardL2ReservedMask = 0x3F00_0000_0000_01FEUL;

  internal static Qcow2Header ReadHeader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("QCOW2 parsing requires a readable, seekable stream.", nameof(stream));
    if (stream.Length < 72)
      throw new InvalidDataException("QCOW2: file too small to contain a valid header.");

    Span<byte> fixedHeader = stackalloc byte[104];
    fixedHeader.Clear();
    stream.Position = 0;
    stream.ReadExactly(fixedHeader[..72]);

    if (!fixedHeader[..4].SequenceEqual(Magic))
      throw new InvalidDataException("QCOW2: invalid magic bytes.");

    var version = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[4..]);
    if (version is not (2 or 3))
      throw new InvalidDataException($"QCOW2: unsupported version {version}.");

    if (version == 3) {
      if (stream.Length < 104)
        throw new InvalidDataException("QCOW2: truncated version 3 header.");
      stream.Position = 72;
      stream.ReadExactly(fixedHeader[72..104]);
    }

    var backingFileOffset = BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[8..]);
    var backingFileSize = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[16..]);
    var clusterBits = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[20..]));
    if (clusterBits is < 9 or > 21)
      throw new InvalidDataException($"QCOW2: cluster_bits {clusterBits} out of valid range [9..21].");

    var virtualSizeRaw = BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[24..]);
    if (virtualSizeRaw > long.MaxValue)
      throw new InvalidDataException("QCOW2: virtual disk size exceeds the supported signed stream range.");
    var virtualSize = (long)virtualSizeRaw;

    var cryptMethod = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[32..]);
    var l1SizeRaw = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[36..]);
    if (l1SizeRaw > int.MaxValue)
      throw new InvalidDataException("QCOW2: L1 table is too large.");
    var l1Size = (int)l1SizeRaw;

    var l1TableOffsetRaw = BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[40..]);
    var refcountTableOffsetRaw = BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[48..]);
    var refcountTableClustersRaw = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[56..]);
    var snapshotCount = BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[60..]);
    var snapshotsOffsetRaw = BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[64..]);

    if (l1TableOffsetRaw > long.MaxValue || refcountTableOffsetRaw > long.MaxValue || snapshotsOffsetRaw > long.MaxValue)
      throw new InvalidDataException("QCOW2: metadata offset exceeds the supported signed stream range.");
    if (refcountTableClustersRaw > int.MaxValue)
      throw new InvalidDataException("QCOW2: refcount table is too large.");

    var incompatibleFeatures = version == 3 ? BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[72..]) : 0UL;
    var compatibleFeatures = version == 3 ? BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[80..]) : 0UL;
    var autoclearFeatures = version == 3 ? BinaryPrimitives.ReadUInt64BigEndian(fixedHeader[88..]) : 0UL;
    var refcountOrder = version == 3 ? BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[96..]) : 4U;
    var headerLength = version == 3 ? BinaryPrimitives.ReadUInt32BigEndian(fixedHeader[100..]) : 72U;

    if (refcountOrder > 6)
      throw new InvalidDataException($"QCOW2: refcount_order {refcountOrder} exceeds the specification limit of 6.");
    if (version == 3 && (headerLength < 104 || (headerLength & 7) != 0))
      throw new InvalidDataException($"QCOW2: invalid version 3 header length {headerLength}.");

    var clusterSize = 1L << clusterBits;
    var l1TableOffset = (long)l1TableOffsetRaw;
    var refcountTableOffset = (long)refcountTableOffsetRaw;
    var snapshotsOffset = (long)snapshotsOffsetRaw;
    if (l1Size > 0 && (l1TableOffset == 0 || (l1TableOffset & (clusterSize - 1)) != 0))
      throw new InvalidDataException("QCOW2: L1 table is not cluster aligned.");
    if (refcountTableClustersRaw > 0 && (refcountTableOffset == 0 || (refcountTableOffset & (clusterSize - 1)) != 0))
      throw new InvalidDataException("QCOW2: refcount table is not cluster aligned.");

    return new Qcow2Header(
      version,
      backingFileOffset,
      backingFileSize,
      clusterBits,
      virtualSize,
      cryptMethod,
      l1Size,
      l1TableOffset,
      refcountTableOffset,
      (int)refcountTableClustersRaw,
      snapshotCount,
      snapshotsOffset,
      incompatibleFeatures,
      compatibleFeatures,
      autoclearFeatures,
      refcountOrder,
      headerLength);
  }

  internal static void ValidateReadableProfile(in Qcow2Header header) {
    if (header.BackingFileOffset != 0 || header.BackingFileSize != 0)
      throw new NotSupportedException("QCOW2: backing files require an external path resolver and are not supported by the stream reader.");
    if (header.CryptMethod != 0)
      throw new NotSupportedException("QCOW2: encrypted images are not supported.");
    if (header.IncompatibleFeatures != 0)
      throw new NotSupportedException($"QCOW2: unsupported incompatible feature mask 0x{header.IncompatibleFeatures:X16}.");
  }

  internal static bool IsWritableProfile(in Qcow2Header header)
    => header.BackingFileOffset == 0
       && header.BackingFileSize == 0
       && header.CryptMethod == 0
       && header.IncompatibleFeatures == 0
       && header.AutoclearFeatures == 0
       && header.RefcountOrder == 4
       && header.RefcountTableOffset > 0
       && header.RefcountTableClusters > 0
       && header.SnapshotCount == 0
       && header.SnapshotsOffset == 0;

  internal static long ReadClusterOffset(ulong entry) => checked((long)(entry & ClusterOffsetMask));

  internal static ulong ReadUInt64BigEndianAt(Stream stream, long offset) {
    Span<byte> buffer = stackalloc byte[8];
    ReadExactlyAt(stream, offset, buffer);
    return BinaryPrimitives.ReadUInt64BigEndian(buffer);
  }

  internal static ushort ReadUInt16BigEndianAt(Stream stream, long offset) {
    Span<byte> buffer = stackalloc byte[2];
    ReadExactlyAt(stream, offset, buffer);
    return BinaryPrimitives.ReadUInt16BigEndian(buffer);
  }

  internal static void WriteUInt64BigEndianAt(Stream stream, long offset, ulong value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
    stream.Position = offset;
    stream.Write(buffer);
  }

  internal static void WriteUInt16BigEndianAt(Stream stream, long offset, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
    stream.Position = offset;
    stream.Write(buffer);
  }

  internal static void ReadExactlyAt(Stream stream, long offset, Span<byte> destination) {
    if (offset < 0 || offset > stream.Length - destination.Length)
      throw new InvalidDataException("QCOW2: metadata or payload points outside the image.");
    stream.Position = offset;
    stream.ReadExactly(destination);
  }

  internal static (long Offset, int MaximumLength) DecodeCompressedRange(ulong entry, int clusterBits, long streamLength) {
    if ((entry & CompressedFlag) == 0)
      throw new ArgumentException("Entry is not a compressed-cluster descriptor.", nameof(entry));
    if ((entry & CopiedFlag) != 0)
      throw new InvalidDataException("QCOW2: compressed cluster has the copied flag set.");

    var sizeBits = clusterBits - 8;
    var offsetBits = 62 - sizeBits;
    var offsetMask = (1UL << offsetBits) - 1;
    var sizeMask = (1UL << sizeBits) - 1;
    var hostOffsetRaw = entry & offsetMask;
    if (hostOffsetRaw > long.MaxValue)
      throw new InvalidDataException("QCOW2: compressed cluster offset exceeds the supported signed stream range.");

    var hostOffset = (long)hostOffsetRaw;
    var additionalSectors = (entry >> offsetBits) & sizeMask;
    var sectorCount = checked((long)additionalSectors + 1);
    var bytesFromOffset = checked(sectorCount * 512 - (hostOffset & 511));
    if (bytesFromOffset <= 0 || bytesFromOffset > int.MaxValue)
      throw new InvalidDataException("QCOW2: invalid compressed-cluster storage length.");
    if (hostOffset < 0 || hostOffset > streamLength - bytesFromOffset)
      throw new InvalidDataException("QCOW2: compressed cluster points outside the image.");

    return (hostOffset, (int)bytesFromOffset);
  }

  internal static void InflateCompressedCluster(Stream stream, ulong entry, int clusterBits, Span<byte> destination) {
    var (offset, maximumLength) = DecodeCompressedRange(entry, clusterBits, stream.Length);
    var compressed = new byte[maximumLength];
    ReadExactlyAt(stream, offset, compressed);

    using var source = new MemoryStream(compressed, writable: false);
    using var inflater = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: false);
    var total = 0;
    while (total < destination.Length) {
      var read = inflater.Read(destination[total..]);
      if (read == 0) break;
      total += read;
    }
    if (total != destination.Length)
      throw new InvalidDataException($"QCOW2: compressed cluster expanded to {total} bytes instead of {destination.Length}.");
  }

  internal static long AlignUp(long value, int alignment) {
    var mask = alignment - 1L;
    return checked((value + mask) & ~mask);
  }
}
