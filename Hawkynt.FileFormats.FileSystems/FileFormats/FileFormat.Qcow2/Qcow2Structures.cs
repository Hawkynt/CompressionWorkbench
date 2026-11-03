#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Deflate;
using Compression.Core.Streams;

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
  uint HeaderLength,
  byte CompressionType = 0,
  string? BackingFormat = null
) {
  public int ClusterSize => 1 << this.ClusterBits;
  /// <summary>Whether L2 entries carry a 64-bit subcluster bitmap (128-bit entries).</summary>
  public bool ExtendedL2 => (this.IncompatibleFeatures & Qcow2Structures.IncompatibleExtendedL2) != 0;
  public int L2EntrySize => this.ExtendedL2 ? 16 : 8;
  public int L2Entries => this.ClusterSize / this.L2EntrySize;
  public bool HasBackingFile => this.BackingFileOffset != 0 && this.BackingFileSize != 0;
  public int RefcountBits => 1 << checked((int)this.RefcountOrder);
  public int RefcountEntriesPerBlock => checked(this.ClusterSize * 8 / this.RefcountBits);
}

internal static class Qcow2Structures {
  internal static ReadOnlySpan<byte> Magic => [0x51, 0x46, 0x49, 0xFB];

  internal const ulong CopiedFlag = 1UL << 63;
  internal const ulong CompressedFlag = 1UL << 62;
  internal const ulong ZeroFlag = 1UL;
  internal const ulong ClusterOffsetMask = 0x00FF_FFFF_FFFF_FE00UL;
  internal const ulong RefcountBlockOffsetMask = 0xFFFF_FFFF_FFFF_FE00UL;
  internal const ulong L1ReservedMask = 0x7F00_0000_0000_01FFUL;
  internal const ulong StandardL2ReservedMask = 0x3F00_0000_0000_01FEUL;

  // Incompatible feature bits (qcow2.txt, "incompatible_features").
  internal const ulong IncompatibleDirty = 1UL << 0;
  internal const ulong IncompatibleCorrupt = 1UL << 1;
  internal const ulong IncompatibleExternalData = 1UL << 2;
  internal const ulong IncompatibleCompressionType = 1UL << 3;
  internal const ulong IncompatibleExtendedL2 = 1UL << 4;
  private const ulong KnownIncompatible = IncompatibleDirty | IncompatibleCorrupt | IncompatibleExternalData
                                          | IncompatibleCompressionType | IncompatibleExtendedL2;

  internal const byte CompressionZlib = 0;
  internal const byte CompressionZstd = 1;

  // Header extension types.
  private const uint ExtensionEnd = 0;
  private const uint ExtensionBackingFormat = 0xE2792ACA;

  /// <summary>Number of subclusters per cluster when extended L2 entries are enabled.</summary>
  internal const int Subclusters = 32;

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

    // compression_type (v3, header_length > 104) and the header extensions behind the header.
    byte compressionType = 0;
    string? backingFormat = null;
    var extensionsStart = version == 3 ? headerLength : 72U;
    var headerAreaEnd = Math.Min(clusterSize, stream.Length);
    if (version == 3 && headerLength > 104 && headerLength <= headerAreaEnd) {
      stream.Position = 104;
      compressionType = (byte)stream.ReadByte();
    }
    if (extensionsStart + 8 <= headerAreaEnd) {
      var area = new byte[headerAreaEnd - extensionsStart];
      stream.Position = extensionsStart;
      stream.ReadExactly(area);
      for (var at = 0; at + 8 <= area.Length;) {
        var type = BinaryPrimitives.ReadUInt32BigEndian(area.AsSpan(at));
        var length = BinaryPrimitives.ReadUInt32BigEndian(area.AsSpan(at + 4));
        if (type == ExtensionEnd)
          break;
        if (length > area.Length - at - 8)
          throw new InvalidDataException("QCOW2: header extension runs past the first cluster.");
        if (type == ExtensionBackingFormat)
          backingFormat = System.Text.Encoding.ASCII.GetString(area, at + 8, (int)length);
        at += 8 + (int)((length + 7) & ~7U);
      }
    }

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
      headerLength,
      compressionType,
      backingFormat);
  }

  /// <summary>
  /// Refuses what the reader cannot decode: encryption, an external data file, unknown
  /// incompatible features and unknown compression types. Dirty and corrupt images are read
  /// (QEMU reads them too; neither bit changes what the guest clusters contain), zstd clusters
  /// and extended L2 entries are decoded, and backing files are followed by <see cref="Qcow2Stream"/>.
  /// </summary>
  internal static void ValidateReadableProfile(in Qcow2Header header) {
    if (header.CryptMethod != 0)
      throw new NotSupportedException("QCOW2: encrypted images are not supported.");
    var unknown = header.IncompatibleFeatures & ~KnownIncompatible;
    if (unknown != 0)
      throw new NotSupportedException($"QCOW2: unsupported incompatible feature mask 0x{unknown:X16}.");
    if ((header.IncompatibleFeatures & IncompatibleExternalData) != 0)
      throw new NotSupportedException("QCOW2: guest data in an external data file is not supported.");
    var declared = (header.IncompatibleFeatures & IncompatibleCompressionType) != 0;
    if (!declared && header.CompressionType != CompressionZlib)
      throw new InvalidDataException("QCOW2: a compression type other than zlib needs the compression-type feature bit.");
    if (header.CompressionType is not (CompressionZlib or CompressionZstd))
      throw new NotSupportedException($"QCOW2: compression type {header.CompressionType} is not supported.");
    if (header.ExtendedL2 && header.ClusterBits < 14)
      throw new InvalidDataException("QCOW2: extended L2 entries need clusters of at least 16 KiB.");
  }

  /// <summary>
  /// A self-contained image without encryption or incompatible features: the only kind a
  /// rebuild or an in-place write may touch without changing what the image means.
  /// </summary>
  internal static bool IsSelfContainedPlain(in Qcow2Header header)
    => !header.HasBackingFile && header.BackingFileOffset == 0 && header.CryptMethod == 0 && header.IncompatibleFeatures == 0;

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
  internal static long ReadRefcountBlockOffset(ulong entry)
    => checked((long)(entry & RefcountBlockOffsetMask));

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

  internal static void InflateCompressedCluster(Stream stream, ulong entry, int clusterBits, Span<byte> destination)
    => InflateCompressedCluster(stream, entry, clusterBits, CompressionZlib, destination);

  /// <summary>
  /// Decodes one compressed cluster: raw deflate (compression type 0) or a zstd frame (type 1),
  /// read from the sector-rounded range the descriptor names; bytes after the stream are padding.
  /// </summary>
  internal static void InflateCompressedCluster(Stream stream, ulong entry, int clusterBits, byte compressionType, Span<byte> destination) {
    var (offset, maximumLength) = DecodeCompressedRange(entry, clusterBits, stream.Length);
    var compressed = new byte[maximumLength];
    ReadExactlyAt(stream, offset, compressed);

    using var source = new MemoryStream(compressed, writable: false);
    using Stream inflater = compressionType == CompressionZstd
      ? new FileFormat.Zstd.ZstdStream(source, CompressionStreamMode.Decompress, leaveOpen: false)
      : new RawDeflateStream(source, CompressionStreamMode.Decompress, leaveOpen: false);
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
