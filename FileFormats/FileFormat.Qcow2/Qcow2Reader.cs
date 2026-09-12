#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Layout;

namespace FileFormat.Qcow2;

/// <summary>
/// Reads self-contained QCOW2 v2/v3 disk images with standard L2 entries.
/// Supports unallocated, zero, uncompressed and deflate-compressed guest clusters.
/// Backing files, encryption and incompatible v3 feature profiles are rejected
/// instead of being interpreted as zero-filled data.
/// </summary>
public sealed class Qcow2Reader : IDisposable {
  private readonly Stream _stream;
  private readonly SectorCache _cache;
  private readonly Qcow2Header _header;
  private readonly long _streamLength;
  private readonly int _clusterSize;
  private readonly int _l2Entries;
  private readonly List<Qcow2Entry> _entries = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<Qcow2Entry> Entries => _entries;

  /// <summary>Virtual disk size in bytes.</summary>
  public long VirtualSize => _header.VirtualSize;

  /// <summary>
  /// Initializes a new instance of <see cref="Qcow2Reader"/>.
  /// </summary>
  public Qcow2Reader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _stream = stream;
    _streamLength = stream.Length;
    _header = Qcow2Structures.ReadHeader(stream);
    Qcow2Structures.ValidateReadableProfile(_header);
    _clusterSize = _header.ClusterSize;
    _l2Entries = _header.L2Entries;
    _cache = new SectorCache(stream);

    _entries.Add(new Qcow2Entry {
      Name = "disk.img",
      Size = _header.VirtualSize,
      Offset = 0,
    });
  }

  /// <summary>
  /// Extracts the full virtual disk image, resolving all active L1/L2 table entries.
  /// </summary>
  public byte[] ExtractDisk() {
    if (_header.VirtualSize == 0)
      return [];
    if (_header.VirtualSize > int.MaxValue)
      throw new NotSupportedException(
        "QCOW2: ExtractDisk materializes the whole guest disk and is limited to Int32-sized buffers. " +
        "Use Qcow2Stream for larger images.");

    var result = new byte[(int)_header.VirtualSize];
    var totalClusters = checked((int)((_header.VirtualSize + _clusterSize - 1) / _clusterSize));
    Span<byte> entryBuffer = stackalloc byte[8];

    for (var clusterIndex = 0; clusterIndex < totalClusters; ++clusterIndex) {
      var l1Index = clusterIndex / _l2Entries;
      var l2Index = clusterIndex % _l2Entries;
      if (l1Index >= _header.L1Size)
        break;

      var l1EntryOffset = checked(_header.L1TableOffset + l1Index * 8L);
      EnsureReadableRange(l1EntryOffset, 8, "L1 entry");
      _cache.Read(l1EntryOffset, entryBuffer);
      var l1Entry = BinaryPrimitives.ReadUInt64BigEndian(entryBuffer);
      ValidateL1Entry(l1Entry);
      var l2TableOffset = Qcow2Structures.ReadClusterOffset(l1Entry);
      if (l2TableOffset == 0)
        continue;

      var l2EntryOffset = checked(l2TableOffset + l2Index * 8L);
      EnsureReadableRange(l2EntryOffset, 8, "L2 entry");
      _cache.Read(l2EntryOffset, entryBuffer);
      var l2Entry = BinaryPrimitives.ReadUInt64BigEndian(entryBuffer);

      var destinationOffset = checked(clusterIndex * _clusterSize);
      var writeLength = Math.Min(_clusterSize, result.Length - destinationOffset);
      ReadCluster(l2Entry, result.AsSpan(destinationOffset, writeLength));
    }

    return result;
  }

  private void ReadCluster(ulong l2Entry, Span<byte> destination) {
    if (l2Entry == 0)
      return;

    if ((l2Entry & Qcow2Structures.CompressedFlag) != 0) {
      if ((l2Entry & Qcow2Structures.CopiedFlag) != 0)
        throw new InvalidDataException("QCOW2: compressed cluster has the copied flag set.");
      var cluster = new byte[_clusterSize];
      Qcow2Structures.InflateCompressedCluster(_stream, l2Entry, _header.ClusterBits, cluster);
      cluster.AsSpan(0, destination.Length).CopyTo(destination);
      return;
    }

    ValidateStandardL2Entry(l2Entry);
    if (_header.Version >= 3 && (l2Entry & Qcow2Structures.ZeroFlag) != 0)
      return;

    var hostOffset = Qcow2Structures.ReadClusterOffset(l2Entry);
    if (hostOffset == 0)
      return;

    EnsureReadableRange(hostOffset, destination.Length, "guest data cluster");
    _cache.Read(hostOffset, destination);
  }

  private void ValidateL1Entry(ulong entry) {
    if ((entry & Qcow2Structures.L1ReservedMask) != 0)
      throw new InvalidDataException("QCOW2: L1 entry uses reserved bits.");
    var offset = Qcow2Structures.ReadClusterOffset(entry);
    if (offset != 0 && (offset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: L2 table offset is not cluster aligned.");
  }

  private void ValidateStandardL2Entry(ulong entry) {
    if ((entry & Qcow2Structures.StandardL2ReservedMask) != 0)
      throw new InvalidDataException("QCOW2: standard L2 entry uses reserved bits.");
    if (_header.Version == 2 && (entry & Qcow2Structures.ZeroFlag) != 0)
      throw new InvalidDataException("QCOW2: version 2 L2 entry uses the version 3 zero flag.");
    var offset = Qcow2Structures.ReadClusterOffset(entry);
    if (offset != 0 && (offset & (_clusterSize - 1L)) != 0)
      throw new InvalidDataException("QCOW2: guest cluster offset is not cluster aligned.");
  }

  private void EnsureReadableRange(long offset, int length, string description) {
    if (offset < 0 || length < 0 || offset > _streamLength - length)
      throw new InvalidDataException($"QCOW2: {description} points outside the image.");
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() => _cache.Dispose();
}
