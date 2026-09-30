#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Secure-remove implementation for NTFS images. Resolves the path through the
/// directory indexes, removes the entry from its parent's index, zeros and releases
/// every cluster the record's non-resident attributes own (data, named streams,
/// index blocks), and zeros the MFT record. After the operation no bytes of the
/// original filename or content remain recoverable from the image.
/// </summary>
public static class NtfsRemover {
  private const int MftRecordSize = 1024;
  private const int FirstUserRecord = 16;

  /// <summary>
  /// Removes the file at <paramref name="fileName"/> — a path from the root, separated
  /// by <c>/</c> or <c>\</c> — from the in-memory NTFS image. Throws
  /// <see cref="FileNotFoundException"/> when no such file exists. The image is
  /// modified in place.
  /// </summary>
  /// <remarks>
  /// The file is found by walking the directory indexes from the root, not by
  /// searching the MFT for a record whose name matches: a leaf name is not unique,
  /// and matching on it removed the first <c>readme.txt</c> anywhere on the volume
  /// whichever folder was meant. A record that carries more than one name (a hard
  /// link) is refused rather than half-removed, and so is a directory that still
  /// has entries.
  /// </remarks>
  public static void Remove(byte[] image, string fileName) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(fileName);

    // --- Boot sector fields ---
    var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11));
    if (bytesPerSector == 0) bytesPerSector = 512;
    var sectorsPerCluster = image[13];
    if (sectorsPerCluster == 0) sectorsPerCluster = 8;
    var clusterSize = bytesPerSector * sectorsPerCluster;
    var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(image.AsSpan(48));

    var clustersPerRecord = (sbyte)image[64];
    var mftRecordSize = clustersPerRecord < 0
      ? 1 << (-clustersPerRecord)
      : clustersPerRecord * clusterSize;

    var mftOffset = mftCluster * clusterSize;
    if (mftOffset + mftRecordSize > image.Length)
      throw new InvalidDataException("NTFS: MFT offset out of range.");

    // --- Resolve the path through the directory indexes ---
    var parts = fileName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) throw new FileNotFoundException($"File '{fileName}' not found in NTFS image.");
    uint parent = RootRecord;
    for (var i = 0; i < parts.Length - 1; ++i) {
      parent = NtfsInPlaceAdder.FindChildInIndex(image, parent, parts[i]);
      if (parent == 0) throw new FileNotFoundException($"File '{fileName}' not found in NTFS image.");
    }
    var matchRecord = (int)NtfsInPlaceAdder.FindChildInIndex(image, parent, parts[^1]);
    if (matchRecord < FirstUserRecord)
      throw new FileNotFoundException($"File '{fileName}' not found in NTFS image.");

    var recordOffsetFinal = (int)NtfsInPlaceAdder.MftRecordByteOffset(image, matchRecord);
    if (recordOffsetFinal < 0 || recordOffsetFinal + mftRecordSize > image.Length)
      throw new InvalidDataException($"NTFS: record {matchRecord} lies outside the image.");
    var matchCopy = image.AsSpan(recordOffsetFinal, mftRecordSize).ToArray();
    ApplyFixup(matchCopy);
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(matchCopy.AsSpan(22));
    if ((flags & 0x01) == 0 || matchCopy[0] != (byte)'F')
      throw new InvalidDataException($"NTFS: the index names record {matchRecord} for '{fileName}', which is not in use.");
    if (LinkCount(matchCopy) > 1)
      throw new NotSupportedException(
        $"NTFS: '{fileName}' is one of several hard links to the same record; removing one name in place is not supported.");
    if ((flags & 0x02) != 0 && NtfsInPlaceAdder.HasIndexEntries(image, (uint)matchRecord))
      throw new IOException($"NTFS: directory '{fileName}' is not empty.");

    // --- Unlink first: until the index entry is gone nothing has been destroyed. ---
    if (!NtfsInPlaceAdder.RemoveIndexEntry(image, parent, (uint)matchRecord))
      throw new InvalidDataException($"NTFS: '{fileName}' is not in its parent directory's index.");

    // --- Zero and release every non-resident attribute the record owns: the data,
    //     named streams, a directory's index blocks. ---
    ZeroNonResidentAttributes(image, matchCopy, clusterSize);
    TryFreeAllClustersInBitmap(image, matchCopy, clusterSize, mftOffset, mftRecordSize);
    TryClearMftBitmapBit(image, mftOffset, mftRecordSize, (uint)matchRecord);

    // --- Zero the entire MFT record. Reader skips records without "FILE" signature. ---
    image.AsSpan(recordOffsetFinal, mftRecordSize).Clear();
  }

  private const uint RootRecord = 5;

  /// <summary>
  /// The number of names the record is known by. A DOS 8.3 alias (namespace 2) is a
  /// second spelling of the Win32 name beside it, not another link.
  /// </summary>
  private static int LinkCount(byte[] record) {
    var count = 0;
    var first = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
    var pos = (int)first;
    while (pos + 16 <= used && pos + 16 <= record.Length) {
      var t = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
      if (t == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
      if (len < 16 || pos + len > record.Length) break;
      if (t == 0x30 && record[pos + 8] == 0) {
        var v = pos + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 20));
        if (v + 66 <= record.Length && record[v + 65] != 2) ++count;
      }
      pos += len;
    }
    return count;
  }

  // Zeros the clusters of every non-resident attribute in the record.
  private static void ZeroNonResidentAttributes(byte[] image, byte[] record, int clusterSize) {
    var first = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
    var pos = (int)first;
    while (pos + 16 <= used && pos + 16 <= record.Length) {
      var t = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
      if (t == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
      if (len < 16 || pos + len > record.Length) break;
      if (record[pos + 8] != 0)
        ZeroClustersFromDataRuns(image, record, pos + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 32)), clusterSize);
      pos += len;
    }
  }

  // Releases in $Bitmap the clusters of every non-resident attribute in the record.
  private static void TryFreeAllClustersInBitmap(byte[] image, byte[] record, int clusterSize,
      long mftOffset, int mftRecordSize) {
    try {
      var bmOffset = BitmapByteOffset(image, mftOffset, mftRecordSize, clusterSize);
      if (bmOffset < 0) return;
      var firstAttr = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
      var used = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
      var pos = (int)firstAttr;
      while (pos + 16 <= used && pos + 16 <= record.Length) {
        var t = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos));
        if (t == 0xFFFFFFFF) break;
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(pos + 4));
        if (len < 16) break;
        if (record[pos + 8] != 0) {
          var runsOff = pos + BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(pos + 32));
          FreeRunsInBitmap(image, record, runsOff, clusterSize, bmOffset);
        }
        pos += len;
      }
    } catch { /* best effort */ }
  }

  private static void FreeRunsInBitmap(byte[] image, byte[] record, int offset, int clusterSize, int bmOffset) {
    long prevLcn = 0;
    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;
      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;
      offset++;
      long length = 0;
      for (var i = 0; i < lengthBytes; ++i) length |= (long)record[offset + i] << (i * 8);
      offset += lengthBytes;
      long delta = 0;
      for (var i = 0; i < offsetBytes; ++i) delta |= (long)record[offset + i] << (i * 8);
      if (offsetBytes > 0 && (record[offset + offsetBytes - 1] & 0x80) != 0)
        for (var i = offsetBytes; i < 8; ++i) delta |= (long)0xFF << (i * 8);
      offset += offsetBytes;
      if (offsetBytes == 0) continue;          // a hole owns no cluster
      prevLcn += delta;
      if (length <= 0 || prevLcn < 0) continue;
      for (long c = prevLcn; c < prevLcn + length; ++c) {
        var b = bmOffset + (int)(c / 8);
        if (b >= 0 && b < image.Length) image[b] &= (byte)~(1 << (int)(c % 8));
      }
    }
  }

  private static int BitmapByteOffset(byte[] image, long mftOffset, int mftRecordSize, int clusterSize) {
    var rec6 = image.AsSpan((int)(mftOffset + 6L * mftRecordSize), mftRecordSize).ToArray();
    ApplyFixup(rec6);
    var firstAttr = BinaryPrimitives.ReadUInt16LittleEndian(rec6.AsSpan(20));
    var used = BinaryPrimitives.ReadUInt32LittleEndian(rec6.AsSpan(24));
    var pos = (int)firstAttr;
    while (pos + 16 <= used && pos + 16 <= rec6.Length) {
      var t = BinaryPrimitives.ReadUInt32LittleEndian(rec6.AsSpan(pos));
      if (t == 0xFFFFFFFF) break;
      var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec6.AsSpan(pos + 4));
      if (len < 16) break;
      if (t == 0x80 && rec6[pos + 9] == 0 && rec6[pos + 8] != 0) {
        var runsOff = pos + BinaryPrimitives.ReadUInt16LittleEndian(rec6.AsSpan(pos + 32));
        var lcn = FirstRunLcnLocal(rec6, runsOff);
        return (int)(lcn * clusterSize);
      }
      pos += len;
    }
    return -1;
  }

  // Clears the removed record's bit in $MFT:$BITMAP (record 0's non-resident $BITMAP).
  private static void TryClearMftBitmapBit(byte[] image, long mftOffset, int mftRecordSize, uint recordNum) {
    try {
      var rec0 = image.AsSpan((int)mftOffset, mftRecordSize).ToArray();
      ApplyFixup(rec0);
      var firstAttr = BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(20));
      var used = BinaryPrimitives.ReadUInt32LittleEndian(rec0.AsSpan(24));
      var pos = (int)firstAttr;
      while (pos + 16 <= used && pos + 16 <= rec0.Length) {
        var t = BinaryPrimitives.ReadUInt32LittleEndian(rec0.AsSpan(pos));
        if (t == 0xFFFFFFFF) break;
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(rec0.AsSpan(pos + 4));
        if (len < 16) break;
        if (t == 0xB0) {
          if (rec0[pos + 8] == 0) return; // resident bitmap edit would need record-0 re-fixup + mirror; skip
          var runsOff = pos + BinaryPrimitives.ReadUInt16LittleEndian(rec0.AsSpan(pos + 32));
          var lcn = FirstRunLcnLocal(rec0, runsOff);
          var bmByte = (int)(lcn * (BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(11)) * (image[13] == 0 ? 8 : image[13])) + recordNum / 8);
          if (bmByte >= 0 && bmByte < image.Length) image[bmByte] &= (byte)~(1 << (int)(recordNum % 8));
          return;
        }
        pos += len;
      }
    } catch { /* best effort */ }
  }

  private static long FirstRunLcnLocal(byte[] record, int runsOffset) {
    var header = record[runsOffset];
    var lengthBytes = header & 0x0F;
    var offsetBytes = (header >> 4) & 0x0F;
    long lcn = 0;
    var o = runsOffset + 1 + lengthBytes;
    for (var i = 0; i < offsetBytes; i++) lcn |= (long)record[o + i] << (i * 8);
    if (offsetBytes > 0 && (record[o + offsetBytes - 1] & 0x80) != 0)
      for (var i = offsetBytes; i < 8; i++) lcn |= (long)0xFF << (i * 8);
    return lcn;
  }

  /// <summary>
  /// Reverses the USN fixup. Each sector's trailing 2 bytes (which held the USN
  /// sentinel on disk) are restored from the fixup array at <c>usaOffset + i*2</c>.
  /// </summary>
  private static void ApplyFixup(byte[] record) {
    if (!NtfsRecordLayout.TryReadUpdateSequence(record, out var usaOffset, out var usaCount)) return;

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));

    for (var i = 1; i < usaCount; ++i) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;

      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
      if (actual != usn) continue;

      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  private static void ZeroClustersFromDataRuns(byte[] image, byte[] record, int offset, int clusterSize) {
    long previousLcn = 0;

    while (offset < record.Length) {
      var header = record[offset];
      if (header == 0) break;

      var lengthBytes = header & 0x0F;
      var offsetBytes = (header >> 4) & 0x0F;

      offset++;
      if (offset + lengthBytes + offsetBytes > record.Length) break;

      long length = 0;
      for (var i = 0; i < lengthBytes; ++i)
        length |= (long)record[offset + i] << (i * 8);
      offset += lengthBytes;

      long clusterOffset = 0;
      if (offsetBytes > 0) {
        for (var i = 0; i < offsetBytes; ++i)
          clusterOffset |= (long)record[offset + i] << (i * 8);
        if ((record[offset + offsetBytes - 1] & 0x80) != 0) {
          for (var i = offsetBytes; i < 8; ++i)
            clusterOffset |= (long)0xFF << (i * 8);
        }
        offset += offsetBytes;
      }

      if (offsetBytes == 0) continue;          // a hole owns no cluster
      var lcn = previousLcn + clusterOffset;
      previousLcn = lcn;

      if (length <= 0 || lcn < 0) continue;

      var byteStart = lcn * clusterSize;
      var byteLen = length * clusterSize;
      if (byteStart >= image.Length) continue;
      if (byteStart + byteLen > image.Length)
        byteLen = image.Length - byteStart;
      if (byteLen > 0)
        image.AsSpan((int)byteStart, (int)byteLen).Clear();
    }
  }

}
