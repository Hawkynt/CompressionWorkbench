#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Layout;

namespace FileFormat.Qcow2;

/// <summary>
/// Reads QCOW2 (QEMU Copy-On-Write v2/v3) disk images.
/// Supports uncompressed, zlib-compressed, and zero clusters.
/// Magic: 0x514649FB ("QFI\xFB") at offset 0, big-endian header.
/// <para>
/// Streams reads via <see cref="SectorCache"/> so opening a multi-TB image
/// does not load the whole file into RAM — only the header, L1/L2 tables
/// and (during <see cref="ExtractDisk"/>) the requested cluster bytes are
/// fetched on demand.
/// </para>
/// </summary>
public sealed class Qcow2Reader : IDisposable {
  private static readonly byte[] Magic = [0x51, 0x46, 0x49, 0xFB];

  private readonly SectorCache _cache;
  private readonly long _streamLength;

  // Header fields
  private readonly uint _version;
  private readonly int _clusterBits;
  private readonly long _virtualSize;
  private readonly uint _l1Size;
  private readonly long _l1TableOffset;

  private readonly int _clusterSize;
  private readonly int _l2Entries; // entries per L2 table = clusterSize / 8

  private readonly List<Qcow2Entry> _entries = [];

  public IReadOnlyList<Qcow2Entry> Entries => _entries;

  /// <summary>Virtual disk size in bytes.</summary>
  public long VirtualSize => _virtualSize;

  public Qcow2Reader(Stream stream, bool leaveOpen = false) {
    ArgumentNullException.ThrowIfNull(stream);
    _streamLength = stream.Length;
    _cache = new SectorCache(stream);

    if (_streamLength < 72)
      throw new InvalidDataException("QCOW2: file too small to contain a valid header.");

    // The header is at most 104 bytes for v2/v3. Read 128 to be safe.
    var hdrLen = (int)Math.Min(_streamLength, 128);
    var hdr = _cache.Read(0, hdrLen);

    if (!hdr.AsSpan(0, 4).SequenceEqual(Magic))
      throw new InvalidDataException("QCOW2: invalid magic bytes.");

    _version = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(4));
    if (_version != 2 && _version != 3)
      throw new InvalidDataException($"QCOW2: unsupported version {_version}.");

    // uint64 backing_file_offset  @ 8
    // uint32 backing_file_size    @ 16
    _clusterBits = (int)BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(20));
    _virtualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(24));
    // uint32 crypt_method         @ 32
    _l1Size = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(36));
    _l1TableOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(40));
    // uint64 refcount_table_offset @ 48
    // uint32 refcount_table_clusters @ 56
    // uint32 nb_snapshots          @ 60
    // uint64 snapshots_offset      @ 64

    if (_clusterBits < 9 || _clusterBits > 21)
      throw new InvalidDataException($"QCOW2: cluster_bits {_clusterBits} out of valid range [9..21].");

    _clusterSize = 1 << _clusterBits;
    _l2Entries = _clusterSize / 8;

    _entries.Add(new Qcow2Entry {
      Name = "disk.img",
      Size = _virtualSize,
      Offset = 0,
    });
  }

  /// <summary>
  /// Extracts the full virtual disk image, resolving all L1/L2 table entries.
  /// Zero L2 entries yield zero-filled clusters; compressed entries are inflated via raw deflate.
  /// </summary>
  public byte[] ExtractDisk() {
    if (_virtualSize == 0)
      return [];

    var result = new byte[_virtualSize];
    var totalClusters = (int)((_virtualSize + _clusterSize - 1) / _clusterSize);

    Span<byte> entryBuf = stackalloc byte[8];

    for (var clusterIdx = 0; clusterIdx < totalClusters; clusterIdx++) {
      var l1Idx = clusterIdx / _l2Entries;
      var l2Idx = clusterIdx % _l2Entries;

      if (l1Idx >= (int)_l1Size)
        break;

      var l1EntryOffset = _l1TableOffset + l1Idx * 8L;
      if (l1EntryOffset + 8 > _streamLength)
        break;

      _cache.Read(l1EntryOffset, entryBuf);
      var l1Entry = BinaryPrimitives.ReadUInt64BigEndian(entryBuf);
      // Bits [0..8] of L1 entry are reserved/flags; the L2 table offset is masked
      var l2TableOffset = (long)(l1Entry & 0x00FFFFFFFFFFFE00UL);

      if (l2TableOffset == 0) {
        // L2 table not allocated — entire L2 range is zero
        continue;
      }

      var l2EntryOffset = l2TableOffset + l2Idx * 8L;
      if (l2EntryOffset + 8 > _streamLength)
        continue;

      _cache.Read(l2EntryOffset, entryBuf);
      var l2Entry = BinaryPrimitives.ReadUInt64BigEndian(entryBuf);

      var destOffset = (long)clusterIdx * _clusterSize;
      var writeLen = (int)Math.Min(_clusterSize, _virtualSize - destOffset);

      if (l2Entry == 0) {
        // Zero cluster — already zeroed by array initializer
        continue;
      }

      var isCompressed = (l2Entry & (1UL << 62)) != 0;

      if (isCompressed) {
        ReadCompressedCluster(l2Entry, result, (int)destOffset, writeLen);
      } else {
        var hostOffset = (long)(l2Entry & 0x00FFFFFFFFFFFE00UL);
        if (hostOffset > 0 && hostOffset + writeLen <= _streamLength) {
          _cache.Read(hostOffset, result.AsSpan((int)destOffset, writeLen));
        }
      }
    }

    return result;
  }

  private void ReadCompressedCluster(ulong l2Entry, byte[] dest, int destOffset, int writeLen) {
    // Compressed cluster descriptor layout (v2):
    //   bits [62]           = compressed flag (1)
    //   bits [cluster_bits-8 .. 61] = host cluster offset (in 512-byte sectors)
    //   bits [0 .. cluster_bits-9]  = compressed data size - 1 (in bytes)
    //
    // sector_offset_bits = 62 - (cluster_bits - 8) = 70 - cluster_bits
    // size_bits          = cluster_bits - 8 + 1     = cluster_bits - 7  (but stored as value-1)

    var compSizeBits = _clusterBits - 8;             // number of bits for compressed size - 1

    // Mask off bit 63 (reserved) and bit 62 (compressed flag)
    var descriptor = l2Entry & 0x3FFFFFFFFFFFFFFFUL;

    // Compressed size - 1 is in the low compSizeBits bits
    var compSizeMask = (1UL << compSizeBits) - 1UL;
    var compSizeM1 = (int)(descriptor & compSizeMask);
    var compSize = compSizeM1 + 1;

    // Host offset in 512-byte sectors is in the remaining upper bits
    var hostSectors = descriptor >> compSizeBits;
    var hostByteOffset = (long)hostSectors * 512;

    if (hostByteOffset < 0 || hostByteOffset >= _streamLength)
      return;

    var availBytes = _streamLength - hostByteOffset;
    if (compSize > availBytes)
      compSize = (int)availBytes;

    if (compSize < 2)
      return;

    // Pull the compressed payload via the cache (one allocation per compressed cluster).
    var compBuf = _cache.Read(hostByteOffset, compSize);

    // The compressed data is a raw deflate stream preceded by a 2-byte zlib header.
    // Skip the zlib header (CMF + FLG bytes) and inflate the raw deflate payload.
    var deflateStart = 2;
    var deflateLen = compSize - 2;
    if (deflateLen <= 0)
      return;

    try {
      using var deflateMs = new MemoryStream(compBuf, deflateStart, deflateLen);
      using var deflateStream = new System.IO.Compression.DeflateStream(
          deflateMs, System.IO.Compression.CompressionMode.Decompress);

      var cluster = new byte[_clusterSize];
      var totalRead = 0;
      while (totalRead < _clusterSize) {
        var read = deflateStream.Read(cluster, totalRead, _clusterSize - totalRead);
        if (read == 0) break;
        totalRead += read;
      }

      var copyLen = Math.Min(writeLen, totalRead);
      cluster.AsSpan(0, copyLen).CopyTo(dest.AsSpan(destOffset));
    } catch (InvalidDataException) {
      // Silently skip corrupt compressed clusters
    }
  }

  public void Dispose() => _cache.Dispose();
}
