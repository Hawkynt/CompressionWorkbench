#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// In-place exFAT block mover. Moves cluster-aligned extents and patches
/// FAT chain entries, allocation bitmap, directory entry sets, and VBR PercentInUse.
/// <para>
/// Streaming: never loads the whole image. All metadata updates are targeted
/// writes with <see cref="Stream.Flush"/> barriers between the four steps so
/// a crash mid-operation leaves the image in an fsck-recoverable state. The
/// FAT (potentially 50 GB on a 50 TB volume) is navigated via
/// <see cref="SectorCache"/> with a bounded ~256 MB memory cap.
/// </para>
/// </summary>
public sealed class ExFatBlockMover : IFilesystemBlockMover {
  private int _bytesPerSector;
  private int _sectorsPerCluster;
  private int _clusterSize;
  private long _fatOffset;
  private long _clusterHeapOffset;
  private uint _clusterCount;
  private uint _rootCluster;

  /// <summary>Initialises the mover by parsing exFAT VBR from a byte buffer.</summary>
  public void Init(byte[] image) => InitFromVbr(image.AsSpan(0, Math.Min(image.Length, 512)));

  /// <summary>Stream-based init — reads only the 512-byte VBR.</summary>
  public void Init(Stream image) {
    Span<byte> vbr = stackalloc byte[512];
    image.Position = 0;
    image.ReadExactly(vbr);
    InitFromVbr(vbr);
  }

  private void InitFromVbr(ReadOnlySpan<byte> vbr) {
    _bytesPerSector = 1 << vbr[108];
    _sectorsPerCluster = 1 << vbr[109];
    _clusterSize = _bytesPerSector * _sectorsPerCluster;
    var fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr[80..]);
    var clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(vbr[88..]);
    _clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(vbr[92..]);
    _rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(vbr[96..]);
    _fatOffset = (long)fatOffsetSectors * _bytesPerSector;
    _clusterHeapOffset = (long)clusterHeapOffsetSectors * _bytesPerSector;
  }

  public long FirstDataByte => _clusterHeapOffset;
  public int ClusterSize => _clusterSize;

  /// <summary>
  /// Upper bound of the exFAT volume as declared by the VBR — clusterHeapOffset
  /// + clusterCount × clusterSize. The defrag planner must use THIS as its
  /// "imageSize" rather than the stream length: when the exFAT image sits
  /// inside a larger container (partition window, sparse VHD), the stream
  /// length includes padding bytes that are outside the volume. Targeting
  /// offsets above this bound corrupts the FAT (cluster N's entry lives at
  /// fatOffset + N*4 — large N writes into the cluster heap).
  /// </summary>
  public long VolumeSize => _clusterHeapOffset + (long)_clusterCount * _clusterSize;

  private uint OffsetToCluster(long offset) => (uint)((offset - _clusterHeapOffset) / _clusterSize) + 2;

  /// <inheritdoc />
  public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;
    var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min(length, 64 * 1024));
    try {
      var remaining = length;
      var src = srcOffset;
      var dst = dstOffset;
      while (remaining > 0) {
        var chunk = (int)Math.Min(remaining, buffer.Length);
        image.Position = src;
        image.ReadExactly(buffer, 0, chunk);
        image.Position = dst;
        image.Write(buffer, 0, chunk);
        src += chunk;
        dst += chunk;
        remaining -= chunk;
      }
      image.Flush(); // Data lands on disk before any metadata references it.

      if (zeroSource) {
        Array.Clear(buffer, 0, buffer.Length);
        remaining = length;
        src = srcOffset;
        while (remaining > 0) {
          var chunk = (int)Math.Min(remaining, buffer.Length);
          image.Position = src;
          image.Write(buffer, 0, chunk);
          src += chunk;
          remaining -= chunk;
        }
        image.Flush();
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buffer);
    }
  }

  /// <inheritdoc />
  /// <remarks>
  /// Power-fail-safe four-step update:
  ///   1. Allocate new FAT chain (targeted writes, flush).
  ///   2. Patch directory entry-set (32-byte targeted write + checksum recompute, flush).
  ///   3. Free old FAT entries (targeted writes, flush).
  ///   4. Update allocation bitmap + PercentInUse (targeted RMW writes, flush).
  /// </remarks>
  public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    var clusterCount = (int)((length + _clusterSize - 1) / _clusterSize);
    var oldFirstCluster = OffsetToCluster(oldOffset);
    var newFirstCluster = OffsetToCluster(newOffset);

    using var cache = new SectorCache(image);

    // Step 1: Allocate new FAT chain.
    for (var i = 0; i < clusterCount; i++) {
      var next = i + 1 < clusterCount ? newFirstCluster + (uint)(i + 1) : 0xFFFFFFFFu;
      WriteFatStream(image, newFirstCluster + (uint)i, next);
      cache.Invalidate(_fatOffset + (long)(newFirstCluster + i) * 4, 4);
    }
    image.Flush();

    // Step 2: Patch directory entry — find Stream Extension by old FirstCluster
    // + name match, write new FirstCluster (32-byte rewrite + checksum).
    PatchDirectoryEntriesStream(image, cache, fileName, oldFirstCluster, newFirstCluster);
    image.Flush();

    // Step 3: Free old FAT entries.
    for (var i = 0; i < clusterCount; i++) {
      WriteFatStream(image, oldFirstCluster + (uint)i, 0);
      cache.Invalidate(_fatOffset + (long)(oldFirstCluster + i) * 4, 4);
    }
    image.Flush();

    // Step 4: Update allocation bitmap (RMW per bit) + PercentInUse.
    var bmpOffset = FindBitmapOffsetStream(image, cache);
    if (bmpOffset >= 0) {
      for (var i = 0; i < clusterCount; i++) {
        ClearBitmapBitStream(image, bmpOffset, oldFirstCluster + (uint)i);
        SetBitmapBitStream(image, bmpOffset, newFirstCluster + (uint)i);
      }
      image.Flush();
      UpdatePercentInUseStream(image, bmpOffset);
      image.Flush();
    }
  }

  // ── Streaming FAT / bitmap helpers ─────────────────────────────────────

  private void WriteFatStream(Stream image, uint cluster, uint value) {
    var pos = _fatOffset + (long)cluster * 4;
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
    image.Position = pos;
    image.Write(buf);
  }

  private static uint ReadFatStream(SectorCache cache, long fatOffset, uint cluster) {
    Span<byte> buf = stackalloc byte[4];
    cache.Read(fatOffset + (long)cluster * 4, buf);
    return BinaryPrimitives.ReadUInt32LittleEndian(buf);
  }

  private static void SetBitmapBitStream(Stream image, long bmpOffset, uint cluster) {
    var bit = (int)(cluster - 2);
    var bytePos = bmpOffset + bit / 8;
    Span<byte> buf = stackalloc byte[1];
    image.Position = bytePos;
    image.ReadExactly(buf);
    buf[0] |= (byte)(1 << (bit % 8));
    image.Position = bytePos;
    image.Write(buf);
  }

  private static void ClearBitmapBitStream(Stream image, long bmpOffset, uint cluster) {
    var bit = (int)(cluster - 2);
    var bytePos = bmpOffset + bit / 8;
    Span<byte> buf = stackalloc byte[1];
    image.Position = bytePos;
    image.ReadExactly(buf);
    buf[0] &= (byte)~(1 << (bit % 8));
    image.Position = bytePos;
    image.Write(buf);
  }

  /// <summary>Locates the allocation-bitmap data offset by walking the root dir.</summary>
  private long FindBitmapOffsetStream(Stream image, SectorCache cache) {
    var clusterBuf = ArrayPool<byte>.Shared.Rent(_clusterSize);
    try {
      var cluster = _rootCluster;
      var seen = new HashSet<uint>();
      while (cluster >= 2 && cluster <= _clusterCount + 1 && cluster < 0xFFFFFFF8 && seen.Add(cluster)) {
        var off = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
        if (off + _clusterSize > image.Length) return -1;
        image.Position = off;
        image.ReadExactly(clusterBuf, 0, _clusterSize);
        for (var i = 0; i < _clusterSize; i += 32) {
          if (clusterBuf[i] == 0x00) return -1;
          if (clusterBuf[i] == 0x81) {
            var bmpCluster = BinaryPrimitives.ReadUInt32LittleEndian(clusterBuf.AsSpan(i + 20));
            return _clusterHeapOffset + (long)(bmpCluster - 2) * _clusterSize;
          }
        }
        cluster = ReadFatStream(cache, _fatOffset, cluster);
      }
      return -1;
    } finally {
      ArrayPool<byte>.Shared.Return(clusterBuf);
    }
  }

  /// <summary>
  /// Walks the root dir from disk, finds the entry-set whose Stream Extension
  /// matches <paramref name="oldFirst"/> + name, patches the FirstCluster
  /// field, recomputes the entry-set checksum, and writes the 32-byte primary
  /// entry back (single-sector targeted write — atomic on most hardware).
  /// </summary>
  private void PatchDirectoryEntriesStream(Stream image, SectorCache cache, string fileName,
      uint oldFirst, uint newFirst) {
    var clusterBuf = ArrayPool<byte>.Shared.Rent(_clusterSize);
    try {
      var cluster = _rootCluster;
      var seen = new HashSet<uint>();
      while (cluster >= 2 && cluster <= _clusterCount + 1 && cluster < 0xFFFFFFF8 && seen.Add(cluster)) {
        var off = _clusterHeapOffset + (long)(cluster - 2) * _clusterSize;
        if (off + _clusterSize > image.Length) return;
        image.Position = off;
        image.ReadExactly(clusterBuf, 0, _clusterSize);
        for (var i = 0; i < _clusterSize; i += 32) {
          if (clusterBuf[i] == 0x00) return;
          if (clusterBuf[i] != 0x85) continue;
          var secCount = clusterBuf[i + 1];
          var streamStart = i + 32;
          if (streamStart + 32 > _clusterSize || clusterBuf[streamStart] != 0xC0) continue;
          var firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(clusterBuf.AsSpan(streamStart + 20));
          if (firstCluster != oldFirst) { i += secCount * 32; continue; }

          // Confirm name match — secondaries follow the stream entry.
          var nameLength = clusterBuf[streamStart + 3];
          var nameEntries = (nameLength + 14) / 15;
          var sb = new StringBuilder();
          for (var n = 0; n < nameEntries; n++) {
            var nPos = streamStart + 32 + n * 32;
            if (nPos + 32 > _clusterSize || clusterBuf[nPos] != 0xC1) break;
            var chars = Math.Min(15, nameLength - n * 15);
            for (var c = 0; c < chars; c++) {
              var ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(clusterBuf.AsSpan(nPos + 2 + c * 2));
              if (ch == 0) break;
              sb.Append(ch);
            }
          }
          if (!sb.ToString().Equals(fileName, StringComparison.OrdinalIgnoreCase) &&
              !fileName.Equals("*", StringComparison.Ordinal)) {
            i += secCount * 32;
            continue;
          }

          // Patch FirstCluster + recompute entry-set checksum.
          BinaryPrimitives.WriteUInt32LittleEndian(clusterBuf.AsSpan(streamStart + 20), newFirst);
          var setBytes = (1 + secCount) * 32;
          ushort checksum = 0;
          for (var j = 0; j < setBytes; j++) {
            if (j == 2 || j == 3) continue; // skip checksum field itself
            checksum = (ushort)((((checksum & 1) != 0 ? 0x8000 : 0) + (checksum >> 1) + clusterBuf[i + j]) & 0xFFFF);
          }
          BinaryPrimitives.WriteUInt16LittleEndian(clusterBuf.AsSpan(i + 2), checksum);

          // Write the full entry-set back. Each entry is 32 bytes; total set
          // is (1 + secCount) × 32 bytes. May span sectors but typically not.
          image.Position = off + i;
          image.Write(clusterBuf, i, setBytes);
          // Invalidate cache for this range so subsequent reads see the new bytes.
          cache.Invalidate(off + i, setBytes);
          return;
        }
        cluster = ReadFatStream(cache, _fatOffset, cluster);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(clusterBuf);
    }
  }

  /// <summary>
  /// Computes the PercentInUse field from the allocation bitmap and writes it
  /// to VBR offset 112 + backup VBR (sector 12 + 112). Streams the bitmap
  /// in chunks rather than loading it whole.
  /// </summary>
  private void UpdatePercentInUseStream(Stream image, long bmpOffset) {
    if (_clusterCount == 0) return;
    var bmpLen = (int)((_clusterCount + 7) / 8);
    var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    try {
      var used = 0u;
      var read = 0;
      while (read < bmpLen) {
        var chunk = Math.Min(buf.Length, bmpLen - read);
        image.Position = bmpOffset + read;
        var n = 0;
        while (n < chunk) {
          var got = image.Read(buf, n, chunk - n);
          if (got <= 0) break;
          n += got;
        }
        for (var i = 0; i < n; i++)
          used += (uint)BitOperations.PopCount(buf[i]);
        read += n;
        if (n < chunk) break;
      }
      var pct = (byte)Math.Min(100u, used * 100u / _clusterCount);
      Span<byte> single = stackalloc byte[1] { pct };
      image.Position = 112;
      image.Write(single);
      var backupOff = 12 * _bytesPerSector;
      if (backupOff + 113 <= image.Length) {
        image.Position = backupOff + 112;
        image.Write(single);
      }
    } finally {
      ArrayPool<byte>.Shared.Return(buf);
    }
  }
}
