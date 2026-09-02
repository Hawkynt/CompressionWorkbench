#pragma warning disable CS1591
using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Layout;
using Compression.Registry;

namespace FileSystem.Udf;

/// <summary>
/// In-place UDF block mover. Moves sector-aligned extents and patches the
/// file's File Entry allocation descriptors (short_ad / long_ad) so the file
/// points to its new location.
/// <para>
/// Streaming: reads only the AVDP + VDS + FSD + the matching File Entry
/// sector via a <see cref="SectorCache"/>. A 100 GB BD-R UDF image never
/// gets loaded as a whole — only the touched sectors are read, and each
/// metadata write is followed by a <see cref="Stream.Flush"/> barrier so a
/// crash mid-move can never reference garbage.
/// </para>
/// </summary>
public sealed class UdfBlockMover : IFilesystemBlockMover {
  private const int SectorSize = 2048;
  private const int AvdpLba = 256;

  /// <summary>
  /// Gets the first data byte.
  /// </summary>
public long FirstDataByte => 0;

  /// <summary>A sector. An allocation descriptor names one, not a byte.</summary>
  public int BlockSize => SectorSize;

  /// <summary>
  /// Each call repoints the descriptor naming the run it is given and leaves
  /// the file entry's other descriptors alone, so an owner in several runs is
  /// simply several calls.
  /// </summary>
  public bool RepointsRunsIndependently => true;

  /// <summary>
  /// A run may be held outside the volume while the rest of the layout moves,
  /// which is what lets a full volume be rearranged at all.
  /// </summary>
  public bool SupportsHeldRuns => true;

  /// <summary>Where the partition begins, and each file's entry inside it.</summary>
  /// <remarks>
  /// Read once. Walking anchor, volume descriptor sequence, partition, file set
  /// and root directory again for every move costs the square of the move
  /// count, which is why this mover was written and then not used.
  /// </remarks>
  private int _partitionStart = -1;
  private readonly Dictionary<string, int> _fileEntryOf = new(StringComparer.OrdinalIgnoreCase);

  /// <summary>Reads the descriptor chain once and notes where every file entry is.</summary>
  public void Init(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    this._fileEntryOf.Clear();
    this._partitionStart = -1;

    using var cache = new SectorCache(image);
    if (image.Length < (AvdpLba + 1) * SectorSize) return;

    var anchor = new byte[SectorSize];
    cache.Read((long)AvdpLba * SectorSize, anchor);
    if (BinaryPrimitives.ReadUInt16LittleEndian(anchor) != 2) return;

    var vdsLocation = (int)BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20));
    var vdsLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16));

    int partitionStart = 0, fileSetLbn = 0;
    var sector = new byte[SectorSize];
    for (var i = 0; i < vdsLength / SectorSize && i < 64; ++i) {
      var at = (long)(vdsLocation + i) * SectorSize;
      if (at + SectorSize > image.Length) break;
      cache.Read(at, sector);
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(sector);
      if (tag == 5) partitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(188));
      else if (tag == 6) fileSetLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(252));
      else if (tag == 8) break;
    }

    var fileSetOffset = (long)(partitionStart + fileSetLbn) * SectorSize;
    if (fileSetOffset + SectorSize > image.Length) return;
    cache.Read(fileSetOffset, sector);
    var rootIcb = (int)BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(404));

    this._partitionStart = partitionStart;

    this.IndexDirectory(image, cache, partitionStart, rootIcb, string.Empty, 0);
  }

  /// <summary>
  /// Notes where every file entry below <paramref name="dirIcbLbn" /> lives,
  /// under the same slash-joined path the reader gives it.
  /// </summary>
  private void IndexDirectory(Stream image, SectorCache cache, int partitionStart, int dirIcbLbn,
      string basePath, int depth) {
    if (depth > 64) return;

    var dirBytes = ReadDirectoryBytes(image, cache, partitionStart, dirIcbLbn);
    if (dirBytes == null) return;

    var pos = 0;
    while (pos + 38 < dirBytes.Length) {
      if (BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos)) != 257) break;

      var implementationUseLength = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 36));
      var nameLength = dirBytes[pos + 19];
      var flags = dirBytes[pos + 18];
      var childIcb = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 24));
      var isParent = (flags & 0x08) != 0;
      var isDeleted = (flags & 0x04) != 0;
      var isDirectory = (flags & 0x02) != 0;

      if (!isParent && !isDeleted && nameLength > 0) {
        var name = ReadFileIdentifier(dirBytes, pos + 38 + implementationUseLength, nameLength);
        var path = basePath.Length == 0 ? name : $"{basePath}/{name}";
        if (isDirectory)
          this.IndexDirectory(image, cache, partitionStart, childIcb, path, depth + 1);
        else
          this._fileEntryOf[path] = childIcb;
      }

      pos += (38 + implementationUseLength + nameLength + 3) & ~3;
    }
  }

  /// <summary>The bytes of the directory whose file entry sits at the given block, or null.</summary>
  private static byte[]? ReadDirectoryBytes(Stream image, SectorCache cache, int partitionStart,
      int icbLbn) {
    var at = (long)(partitionStart + icbLbn) * SectorSize;
    if (at < 0 || at + SectorSize > image.Length) return null;

    var entry = ArrayPool<byte>.Shared.Rent(SectorSize);
    try {
      cache.Read(at, entry.AsSpan(0, SectorSize));
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(0));
      if (tag is not (261 or 266)) return null;

      var descriptorType = BinaryPrimitives.ReadUInt16LittleEndian(entry.AsSpan(34)) & 0x07;
      var informationLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry.AsSpan(56));
      int extendedAttributeLength, allocationLength, allocationAt;
      if (tag == 261) {
        extendedAttributeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(168));
        allocationLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(172));
        allocationAt = 176 + extendedAttributeLength;
      } else {
        extendedAttributeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(208));
        allocationLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(212));
        allocationAt = 216 + extendedAttributeLength;
      }

      return ReadAllocDataStream(image, cache, entry, partitionStart, allocationAt, allocationLength,
        descriptorType, informationLength);
    } finally {
      ArrayPool<byte>.Shared.Return(entry);
    }
  }

  /// <summary>Decodes a file identifier, which names its own character set in its first byte.</summary>
  private static string ReadFileIdentifier(byte[] bytes, int at, int length) {
    if (at + length > bytes.Length) return string.Empty;
    var name = length > 1 && bytes[at] == 8 ? Encoding.UTF8.GetString(bytes, at + 1, length - 1)
      : length > 1 && bytes[at] == 16 ? Encoding.BigEndianUnicode.GetString(bytes, at + 1, length - 1)
      : Encoding.ASCII.GetString(bytes, at, length);
    return name.TrimEnd('\0');
  }

  /// <inheritdoc />
  /// <summary>
  /// Performs the move extent operation.
  /// </summary>
public void MoveExtent(Stream image, long srcOffset, long dstOffset, long length, bool zeroSource = false) {
    if (length <= 0 || srcOffset == dstOffset) return;

    // Overlap-safe: a run shifted forward by less than its own length
    // overwrites its own tail, and copying that front to back reads bytes
    // the copy has already replaced.
    Compression.Core.DiskImage.ExtentCopy.Move(image, srcOffset, dstOffset, length);
    if (zeroSource)
      Compression.Core.DiskImage.ExtentCopy.Zero(image, srcOffset, length);
  }

  /// <inheritdoc />
  /// <remarks>
  /// Power-fail-safe in-place metadata update via targeted sector writes:
  /// reads the AVDP/VDS/FSD/root FE through the cache, walks the root
  /// directory's FIDs to locate the matching file, reads the file's FE
  /// sector, patches the allocation descriptor that references
  /// <paramref name="oldOffset"/>, recomputes the FE descriptor tag CRC +
  /// checksum, and writes the sector back. Each write is followed by a
  /// <see cref="Stream.Flush"/>. No full-image load — multi-GB DVD/BD images
  /// require only a handful of sector reads/writes per move.
  /// </remarks>
  /// <summary>
  /// Performs the update allocation after move operation.
  /// </summary>
public void UpdateAllocationAfterMove(Stream image, string fileName, long oldOffset, long newOffset, long length) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);
    if (this._partitionStart < 0) this.Init(image);
    if (this._partitionStart < 0) return;

    if (!this._fileEntryOf.TryGetValue(fileName, out var fileEntryLbn))
      throw new InvalidOperationException(
        $"UDF: no file entry was found for '{fileName}', so it cannot be repointed.");

    var oldLbn = (int)((oldOffset / SectorSize) - this._partitionStart);
    var newLbn = (int)((newOffset / SectorSize) - this._partitionStart);
    if (oldLbn == newLbn) return;

    using var cache = new SectorCache(image);
    PatchFileEntryStream(image, cache, this._partitionStart, fileEntryLbn, oldLbn, newLbn);
    image.Flush();
  }

  /// <summary>
  /// Walks the root directory's FIDs to find a child whose name matches
  /// <paramref name="targetName"/>. Each FE sector is read on demand through
  /// the cache; the directory's data extents are also streamed in on demand.
  /// </summary>
  private static int FindFileIcbStream(Stream image, SectorCache cache, int partStart,
      int rootIcbLbn, string targetName) {
    var feOff = (long)(partStart + rootIcbLbn) * SectorSize;
    if (feOff + SectorSize > image.Length) return -1;

    var feSector = ArrayPool<byte>.Shared.Rent(SectorSize);
    byte[]? dirBytes = null;
    try {
      cache.Read(feOff, feSector.AsSpan(0, SectorSize));
      var feTag = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(0));
      if (feTag is not (261 or 266)) return -1;

      var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(34));
      var adType = icbFlags & 0x07;
      var infoLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(feSector.AsSpan(56));

      int lEa, lAd, adRel;
      if (feTag == 261) {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(168));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(172));
        adRel = 176 + lEa;
      } else {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(208));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(212));
        adRel = 216 + lEa;
      }

      // Read the directory's bytes via the cache (one extent at a time).
      dirBytes = ReadAllocDataStream(image, cache, feSector, partStart, adRel, lAd, adType, infoLength);
      if (dirBytes == null) return -1;

      var pos = 0;
      while (pos + 38 < dirBytes.Length) {
        var fidTag = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos));
        if (fidTag != 257) break;
        var lIu = BinaryPrimitives.ReadUInt16LittleEndian(dirBytes.AsSpan(pos + 36));
        var idLen = dirBytes[pos + 19];
        var fidLen = (38 + lIu + idLen + 3) & ~3;
        var fidFlags = dirBytes[pos + 18];
        var isParent = (fidFlags & 0x08) != 0;
        var isDeleted = (fidFlags & 0x04) != 0;
        var childIcbLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(pos + 24));

        if (!isParent && !isDeleted && idLen > 0) {
          var nameStart = pos + 38 + lIu;
          string name;
          if (idLen > 1 && dirBytes[nameStart] == 8)
            name = Encoding.UTF8.GetString(dirBytes, nameStart + 1, idLen - 1);
          else if (idLen > 1 && dirBytes[nameStart] == 16)
            name = Encoding.BigEndianUnicode.GetString(dirBytes, nameStart + 1, idLen - 1);
          else
            name = Encoding.ASCII.GetString(dirBytes, nameStart, idLen);
          name = name.TrimEnd('\0');

          if (name.Equals(targetName, StringComparison.OrdinalIgnoreCase) ||
              targetName.Equals("*", StringComparison.Ordinal))
            return childIcbLbn;
        }
        pos += fidLen;
      }
      return -1;
    } finally {
      ArrayPool<byte>.Shared.Return(feSector);
    }
  }

  /// <summary>
  /// Patches one FE sector: rewrites the matching allocation descriptor's LBN
  /// from <paramref name="oldLbn"/> to <paramref name="newLbn"/>, recomputes
  /// the FE tag's CRC + checksum, and writes the sector back via a single
  /// targeted write.
  /// </summary>
  private static void PatchFileEntryStream(Stream image, SectorCache cache, int partStart,
      int feLbn, int oldLbn, int newLbn) {
    var feOff = (long)(partStart + feLbn) * SectorSize;
    if (feOff + SectorSize > image.Length) return;

    var feSector = ArrayPool<byte>.Shared.Rent(SectorSize);
    try {
      cache.Read(feOff, feSector.AsSpan(0, SectorSize));

      var feTag = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(0));
      if (feTag is not (261 or 266)) return;

      var icbFlags = BinaryPrimitives.ReadUInt16LittleEndian(feSector.AsSpan(34));
      var adType = icbFlags & 0x07;
      if (adType is not (0 or 1)) return; // only short_ad / long_ad

      int lEa, lAd, adRel, feHeaderSize;
      if (feTag == 261) {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(168));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(172));
        feHeaderSize = 176;
        adRel = feHeaderSize + lEa;
      } else {
        lEa = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(208));
        lAd = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(212));
        feHeaderSize = 216;
        adRel = feHeaderSize + lEa;
      }

      var stride = adType == 0 ? 8 : 16;
      var pos = adRel;
      var end = adRel + lAd;
      if (end > SectorSize) end = SectorSize;

      while (pos + stride <= end) {
        var extLen = BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos)) & 0x3FFFFFFF;
        var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
        if (extLen == 0) break;
        if (extLbn == oldLbn) {
          BinaryPrimitives.WriteUInt32LittleEndian(feSector.AsSpan(pos + 4), (uint)newLbn);
          // Re-CRC the FE tag.
          var bodyLength = (feHeaderSize - 16) + lEa + lAd;
          FinalizeTag(feSector.AsSpan(0, SectorSize), 0, bodyLength);

          // Write the entire FE sector back as a single targeted write.
          image.Position = feOff;
          image.Write(feSector, 0, SectorSize);
          // Invalidate the cached sector so subsequent reads see fresh bytes.
          cache.Invalidate(feOff, SectorSize);
          return;
        }
        pos += stride;
      }
    } finally {
      ArrayPool<byte>.Shared.Return(feSector);
    }
  }

  /// <summary>
  /// Reads the bytes addressed by a sequence of allocation descriptors (in
  /// <paramref name="feSector"/> at relative offset <paramref name="adRel"/>),
  /// pulling each referenced extent from the underlying stream via the cache.
  /// </summary>
  private static byte[]? ReadAllocDataStream(Stream image, SectorCache cache, byte[] feSector,
      int partStart, int adRel, int lAd, int adType, long infoLength) {
    if (adType == 3) {
      if (adRel + lAd <= SectorSize) return feSector.AsSpan(adRel, lAd).ToArray();
      return null;
    }
    using var ms2 = new MemoryStream();
    var pos = adRel;
    var end = adRel + lAd;
    if (end > SectorSize) end = SectorSize;
    var stride = adType == 0 ? 8 : 16;

    while (pos + stride <= end && ms2.Length < infoLength) {
      var extLen = (int)(BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos)) & 0x3FFFFFFF);
      var extLbn = (int)BinaryPrimitives.ReadUInt32LittleEndian(feSector.AsSpan(pos + 4));
      pos += stride;
      if (extLen <= 0) continue;
      var off = (long)(partStart + extLbn) * SectorSize;
      if (off + extLen > image.Length) break;
      var buf = ArrayPool<byte>.Shared.Rent(extLen);
      try {
        cache.Read(off, buf.AsSpan(0, extLen));
        ms2.Write(buf, 0, extLen);
      } finally {
        ArrayPool<byte>.Shared.Return(buf);
      }
    }
    return ms2.ToArray();
  }

  private static void FinalizeTag(Span<byte> buf, int tagOffset, int bodyLength) {
    var bodyStart = tagOffset + 16;
    if (bodyStart + bodyLength > buf.Length) bodyLength = buf.Length - bodyStart;
    if (bodyLength < 0) bodyLength = 0;
    var crc = Crc16Ccitt.Compute(buf.Slice(bodyStart, bodyLength));
    BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(tagOffset + 8), crc);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(tagOffset + 10), (ushort)bodyLength);
    buf[tagOffset + 4] = 0;
    byte sum = 0;
    for (var i = 0; i < 16; i++) {
      if (i == 4) continue;
      sum = (byte)(sum + buf[tagOffset + i]);
    }
    buf[tagOffset + 4] = sum;
  }
}
