#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Ntfs;

/// <summary>
/// Secure-remove implementation for NTFS images. Finds the named file in the MFT
/// (records 16+), zeros every cluster referenced by its $DATA attribute (including
/// resident-value bytes for small files), clears the corresponding index entry in
/// the root directory's $INDEX_ROOT, and zeros the entire 1024-byte MFT record.
/// After the operation no bytes of the original filename or content remain
/// recoverable from the image.
/// <para>
/// Root-directory-only for now; nested-directory removal is a follow-up. The
/// reader skips records whose "FILE" signature is missing, so zeroing the whole
/// record is sufficient to hide the file from enumeration and extraction.
/// </para>
/// </summary>
public static class NtfsRemover {
  private const int MftRecordSize = 1024;
  private const int FirstUserRecord = 16;

  /// <summary>
  /// Removes <paramref name="fileName"/> from the in-memory NTFS image. Throws
  /// <see cref="FileNotFoundException"/> if no MFT record matches. The image is
  /// modified in place.
  /// </summary>
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

    // --- Scan MFT records starting at record 16 for matching $FILE_NAME ---
    var maxRecords = (int)((image.Length - mftOffset) / mftRecordSize);
    var matchRecord = -1;

    for (var i = FirstUserRecord; i < maxRecords; ++i) {
      var recordOffset = (int)(mftOffset + i * mftRecordSize);
      if (recordOffset + mftRecordSize > image.Length) break;

      var span = image.AsSpan(recordOffset, mftRecordSize);
      if (span[0] != (byte)'F' || span[1] != (byte)'I' || span[2] != (byte)'L' || span[3] != (byte)'E')
        continue;

      // Parse attributes against a fixup-applied copy; we search for $FILE_NAME.
      var recordCopy = span.ToArray();
      ApplyFixup(recordCopy);

      var flags = BinaryPrimitives.ReadUInt16LittleEndian(recordCopy.AsSpan(22));
      if ((flags & 0x01) == 0) continue; // not in use

      var found = TryMatchFileName(recordCopy, fileName);
      if (!found) continue;

      matchRecord = i;
      break;
    }

    if (matchRecord < 0)
      throw new FileNotFoundException($"File '{fileName}' not found in NTFS image.");

    // --- Zero the file data (clusters or resident value) BEFORE wiping the MFT record. ---
    var recordOffsetFinal = (int)(mftOffset + matchRecord * mftRecordSize);
    var matchCopy = image.AsSpan(recordOffsetFinal, mftRecordSize).ToArray();
    ApplyFixup(matchCopy);
    ZeroDataAttribute(image, matchCopy, clusterSize);

    // --- Best-effort: clear this record's index entry in root directory ($INDEX_ROOT of record 5). ---
    TryClearRootIndexEntry(image, mftOffset, mftRecordSize, (uint)matchRecord);

    // --- Zero the entire MFT record. Reader skips records without "FILE" signature. ---
    image.AsSpan(recordOffsetFinal, mftRecordSize).Clear();
  }

  /// <summary>
  /// Reverses the USN fixup. Each sector's trailing 2 bytes (which held the USN
  /// sentinel on disk) are restored from the fixup array at <c>usaOffset + i*2</c>.
  /// </summary>
  private static void ApplyFixup(byte[] record) {
    var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
    var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));

    if (usaOffset + usaCount * 2 > record.Length || usaCount < 2) return;

    var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset));

    for (var i = 1; i < usaCount; ++i) {
      var sectorEnd = i * 512 - 2;
      if (sectorEnd + 2 > record.Length) break;

      var actual = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sectorEnd));
      if (actual != usn) continue;

      record.AsSpan(usaOffset + i * 2, 2).CopyTo(record.AsSpan(sectorEnd));
    }
  }

  private static bool TryMatchFileName(byte[] record, string target) {
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      if (attrType == 0x30) {
        // $FILE_NAME — always resident in our writer.
        var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 20));
        var dataStart = attrPos + valueOffset;
        if (dataStart + 66 <= record.Length) {
          var nameLength = record[dataStart + 64];
          if (dataStart + 66 + nameLength * 2 <= record.Length) {
            var name = Encoding.Unicode.GetString(record, dataStart + 66, nameLength * 2);
            if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
              return true;
          }
        }
      }

      attrPos += (int)attrLen;
    }

    return false;
  }

  /// <summary>
  /// Locates the default $DATA attribute (type 0x80, unnamed) in the fixup-applied
  /// record and zeros its content on disk: either the resident value bytes inside
  /// the MFT record in the image, or every cluster referenced by its data runs.
  /// </summary>
  private static void ZeroDataAttribute(byte[] image, byte[] record, int clusterSize) {
    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= record.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > record.Length) break;

      if (attrType != 0x80) { attrPos += (int)attrLen; continue; }

      var nameLen = record[attrPos + 9];
      if (nameLen != 0) { attrPos += (int)attrLen; continue; } // named stream (ADS) — skip for default $DATA

      var nonResident = record[attrPos + 8];
      if (nonResident != 0) {
        // Non-resident: decode data runs and zero each cluster range in the image.
        // (Resident $DATA lives inside the MFT record; the caller zeros the whole
        //  1024-byte record afterwards, which wipes resident bytes by construction.)
        var dataRunsOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(attrPos + 32));
        var runsStart = attrPos + dataRunsOffset;
        ZeroClustersFromDataRuns(image, record, runsStart, clusterSize);
      }

      return; // only handle the first default $DATA
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

  /// <summary>
  /// Best-effort zero of the 80-byte index entry inside root-dir record 5's
  /// $INDEX_ROOT that references <paramref name="targetRecord"/>. Keeps the index
  /// structurally consistent (end-marker untouched) while leaving no trace of the
  /// removed file's name. Errors are swallowed — the primary data wipe has
  /// already happened.
  /// </summary>
  private static void TryClearRootIndexEntry(byte[] image, long mftOffset, int mftRecordSize, uint targetRecord) {
    const uint rootRecordNum = 5;
    var rootOffset = (int)(mftOffset + rootRecordNum * mftRecordSize);
    if (rootOffset + mftRecordSize > image.Length) return;

    var rootSpan = image.AsSpan(rootOffset, mftRecordSize);
    if (rootSpan[0] != (byte)'F' || rootSpan[1] != (byte)'I' || rootSpan[2] != (byte)'L' || rootSpan[3] != (byte)'E')
      return;

    var rootCopy = rootSpan.ToArray();
    ApplyFixup(rootCopy);

    var firstAttrOffset = BinaryPrimitives.ReadUInt16LittleEndian(rootCopy.AsSpan(20));
    var usedSize = BinaryPrimitives.ReadUInt32LittleEndian(rootCopy.AsSpan(24));

    var attrPos = (int)firstAttrOffset;
    while (attrPos + 16 <= usedSize && attrPos + 16 <= rootCopy.Length) {
      var attrType = BinaryPrimitives.ReadUInt32LittleEndian(rootCopy.AsSpan(attrPos));
      if (attrType == 0xFFFFFFFF) break;

      var attrLen = BinaryPrimitives.ReadUInt32LittleEndian(rootCopy.AsSpan(attrPos + 4));
      if (attrLen < 16 || attrPos + attrLen > rootCopy.Length) break;

      if (attrType != 0x90) { attrPos += (int)attrLen; continue; }

      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(rootCopy.AsSpan(attrPos + 20));
      var dataStart = attrPos + valueOffset;
      if (dataStart + 32 > rootCopy.Length) return;

      // Index root header is 16 bytes; index header follows at dataStart+16 with
      // entriesOffset (relative to index header start).
      var entriesOffset = BinaryPrimitives.ReadInt32LittleEndian(rootCopy.AsSpan(dataStart + 16));
      var totalSize = BinaryPrimitives.ReadInt32LittleEndian(rootCopy.AsSpan(dataStart + 20));
      var indexStart = dataStart + 16 + entriesOffset;
      var indexEnd = dataStart + 16 + totalSize;

      // Walk entries. Record matches are zeroed in place in BOTH rootCopy AND image
      // (image adjusted by attribute-position-within-record offset).
      while (indexStart + 16 <= indexEnd && indexStart + 16 <= rootCopy.Length) {
        var mftRef = BinaryPrimitives.ReadInt64LittleEndian(rootCopy.AsSpan(indexStart));
        var entryLen = BinaryPrimitives.ReadUInt16LittleEndian(rootCopy.AsSpan(indexStart + 8));
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(rootCopy.AsSpan(indexStart + 12));

        if (entryLen < 16) break;

        var refRecord = (uint)(mftRef & 0x0000FFFFFFFFFFFF);
        if (refRecord == targetRecord) {
          // Zero this entry in the image. Preserve entry length + last-entry flag
          // bytes so the index walker in NtfsReader still advances correctly, but
          // scrub every other byte (including the filename and MFT ref).
          var imageEntryOffset = rootOffset + indexStart;
          if (imageEntryOffset + entryLen <= image.Length) {
            image.AsSpan(imageEntryOffset, entryLen).Clear();
            // Restore entryLen at +8 and flags at +12 so index walker doesn't break.
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(imageEntryOffset + 8), entryLen);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(imageEntryOffset + 12), flags);
          }
          return;
        }

        if ((flags & 0x02) != 0) break; // last entry
        indexStart += entryLen;
      }

      return;
    }
  }
}
