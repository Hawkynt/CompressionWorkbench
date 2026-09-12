#pragma warning disable CS1591
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.Spark;

/// <summary>
/// Random-access in-place modifier for RISC OS Spark / ARC archives.
/// Spark archives are a chain of variable-size entry blocks terminated by
/// an end-of-archive marker (0x1A 0x00). Add appends a new Stored
/// (method 0x02) entry just before the EOA marker; Remove walks the
/// top-level entry chain, locates the target, and shifts trailing bytes
/// forward to compact (no central directory).
/// </summary>
/// <remarks>
/// This modifier operates on top-level entries only. Directory entries
/// (method 0x82, terminated by an end-of-directory marker 0x80) are
/// skipped over but not descended into — to remove a file inside a
/// directory, target its full path with the directory entry.
/// </remarks>
public static class SparkModifier {

  /// <summary>
  /// Appends a Stored (method 0x02) entry to the archive. Walks the
  /// existing entry chain to find the EOA marker, writes a new entry block
  /// in its place, then re-writes the EOA marker. I/O cost is one full
  /// sequential entry walk plus the new entry's bytes.
  /// </summary>
  public static void AddFile(Stream spark, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(spark);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);

    var eoaOffset = FindEoaOffset(spark);
    spark.Position = eoaOffset;
    WriteStoredEntryBlock(spark, name, data);
    WriteEoaMarker(spark);
    spark.SetLength(spark.Position);
  }

  /// <summary>
  /// Removes the named top-level entry. Returns true if found. Walks the
  /// chain to locate the entry, then shifts trailing bytes forward to
  /// compact.
  /// </summary>
  public static bool RemoveFile(Stream spark, string name, bool wipeData = true) {
    ArgumentNullException.ThrowIfNull(spark);
    ArgumentNullException.ThrowIfNull(name);

    var locator = LocateTopLevelEntry(spark, name);
    if (!locator.Found) return false;

    if (wipeData)
      ZeroRange(spark, locator.BlockOffset, locator.BlockSize);

    var afterEntry = locator.BlockOffset + locator.BlockSize;
    var bytesToShift = spark.Length - afterEntry;
    if (bytesToShift > 0) {
      var buf = new byte[64 * 1024];
      var src = afterEntry;
      var dst = locator.BlockOffset;
      while (bytesToShift > 0) {
        var chunk = (int)Math.Min(buf.Length, bytesToShift);
        spark.Position = src;
        var read = 0;
        while (read < chunk) {
          var n = spark.Read(buf, read, chunk - read);
          if (n <= 0) break;
          read += n;
        }
        spark.Position = dst;
        spark.Write(buf, 0, read);
        src += read;
        dst += read;
        bytesToShift -= read;
      }
    }
    spark.SetLength(spark.Length - locator.BlockSize);
    return true;
  }

  // ── Block walking ─────────────────────────────────────────────────────
  // Spark/ARC entry block layout:
  //   byte 0:        marker 0x1A
  //   byte 1:        method (0x00=EOA, 0x01=stored-old, 0x02..=stored/compressed,
  //                  0x80=end-of-directory, 0x82=directory, bit 7 set = RISC OS extended)
  //   bytes 2..14:   filename (13 bytes, NUL-terminated ASCII)
  //   bytes 15..18:  compressed size (uint32 LE)
  //   bytes 19..20:  DOS date
  //   bytes 21..22:  DOS time
  //   bytes 23..24:  CRC-16 of uncompressed data (uint16 LE)
  //   bytes 25..28:  original size (uint32 LE) — absent for method 0x01
  //   bytes 29..40:  RISC OS extension (load + exec + attributes, 12 bytes) — only if method bit 7 set
  //   data:          compressedSize bytes (0 for directories — sub-entries follow inline)
  //
  // EOA marker: 2 bytes (0x1A 0x00) — no further fields.

  private const int FixedHeadPart = 2 + SparkConstants.FileNameLength + 4 + 2 + 2 + 2; // 25 bytes

  private static long FindEoaOffset(Stream spark) {
    spark.Position = 0;
    while (spark.Position + 2 <= spark.Length) {
      var blockStart = spark.Position;
      var marker = spark.ReadByte();
      if (marker != SparkConstants.EntryMarker) return blockStart;
      var method = spark.ReadByte();
      if (method < 0) return blockStart;
      if (method == SparkConstants.MethodEndOfArchive) return blockStart;

      if (!SkipEntryBlock(spark, blockStart, (byte)method))
        return blockStart;
    }
    return spark.Length;
  }

  private readonly record struct EntryLocator(bool Found, long BlockOffset, long BlockSize);

  private static EntryLocator LocateTopLevelEntry(Stream spark, string targetName) {
    spark.Position = 0;
    while (spark.Position + 2 <= spark.Length) {
      var blockStart = spark.Position;
      var marker = spark.ReadByte();
      if (marker != SparkConstants.EntryMarker) break;
      var method = spark.ReadByte();
      if (method < 0) break;
      if (method == SparkConstants.MethodEndOfArchive) break;

      var methodByte = (byte)method;
      var isDirectory = methodByte == SparkConstants.MethodDirectory;

      // Read fixed head portion to extract name and sizes.
      var headBuf = new byte[FixedHeadPart - 2];
      var read = ReadFully(spark, headBuf, 0, headBuf.Length);
      if (read < headBuf.Length) break;

      var fileName = ReadNullTerminatedAscii(headBuf, 0, SparkConstants.FileNameLength);

      var baseMethod = SparkConstants.GetBaseMethod(methodByte);
      var hasOriginalSize = baseMethod >= SparkConstants.GetBaseMethod(SparkConstants.MethodStored);
      if (hasOriginalSize) {
        // Skip 4 bytes original size.
        if (!SkipBytes(spark, 4)) break;
      }
      if (SparkConstants.IsSparkExtended(methodByte)) {
        if (!SkipBytes(spark, SparkConstants.RiscOsExtensionSize)) break;
      }

      var dataStart = spark.Position;

      if (isDirectory) {
        // Directory: nested entries follow until end-of-directory marker (0x80).
        if (!SkipDirectoryContents(spark)) break;
      } else {
        var compressedSize = ReadUInt32LeFromHeadBuf(headBuf);
        spark.Position = dataStart + compressedSize;
      }

      var blockEnd = spark.Position;
      var blockSize = blockEnd - blockStart;

      if (!isDirectory && string.Equals(fileName, targetName, StringComparison.OrdinalIgnoreCase))
        return new EntryLocator(true, blockStart, blockSize);
    }
    return new EntryLocator(false, 0, 0);
  }

  /// <summary>
  /// Skips past a single entry block (header + data), positioning the
  /// stream at the start of the next entry. Returns false on truncation.
  /// </summary>
  private static bool SkipEntryBlock(Stream spark, long blockStart, byte method) {
    if (method == SparkConstants.MethodEndOfDirectory)
      return true; // end-of-directory marker has no body; caller handles

    var headBuf = new byte[FixedHeadPart - 2];
    var read = ReadFully(spark, headBuf, 0, headBuf.Length);
    if (read < headBuf.Length) return false;

    var compressedSize = ReadUInt32LeFromHeadBuf(headBuf);

    var baseMethod = SparkConstants.GetBaseMethod(method);
    var hasOriginalSize = baseMethod >= SparkConstants.GetBaseMethod(SparkConstants.MethodStored);
    if (hasOriginalSize && !SkipBytes(spark, 4)) return false;
    if (SparkConstants.IsSparkExtended(method) && !SkipBytes(spark, SparkConstants.RiscOsExtensionSize))
      return false;

    if (method == SparkConstants.MethodDirectory)
      return SkipDirectoryContents(spark);

    if (compressedSize > 0) {
      var newPos = spark.Position + compressedSize;
      if (newPos > spark.Length) return false;
      spark.Position = newPos;
    }
    return true;
  }

  /// <summary>
  /// Skips through nested directory entries until an end-of-directory
  /// marker (0x80) is consumed. Returns false on truncation/malformed data.
  /// </summary>
  private static bool SkipDirectoryContents(Stream spark) {
    while (spark.Position + 2 <= spark.Length) {
      var marker = spark.ReadByte();
      if (marker != SparkConstants.EntryMarker) return false;
      var method = spark.ReadByte();
      if (method < 0) return false;
      if (method == SparkConstants.MethodEndOfDirectory) return true;
      if (method == SparkConstants.MethodEndOfArchive) return false; // unexpected EOA inside dir
      if (!SkipEntryBlock(spark, spark.Position - 2, (byte)method)) return false;
    }
    return false;
  }

  // headBuf layout: [0..12]=name, [13..16]=compressedSize, [17..18]=date, [19..20]=time, [21..22]=crc16
  private static uint ReadUInt32LeFromHeadBuf(byte[] headBuf) =>
    (uint)(headBuf[13] | headBuf[14] << 8 | headBuf[15] << 16 | headBuf[16] << 24);

  // ── Block writing ─────────────────────────────────────────────────────

  private static void WriteStoredEntryBlock(Stream spark, string name, byte[] data) {
    var truncatedName = name.Length > 12 ? name[..12] : name;
    var nameBytes = Encoding.ASCII.GetBytes(truncatedName);
    var nameLen = Math.Min(nameBytes.Length, SparkConstants.FileNameLength - 1);
    var crc = data.Length > 0 ? Crc16.Compute(data) : (ushort)0;
    var (dosDate, dosTime) = SparkEntry.DateTimeToDosDateTime(DateTime.Now);

    // 29-byte new-format header (method 0x02 = Stored, no RISC OS extension).
    var header = new byte[FixedHeadPart + 4]; // 25 + 4 origSize = 29
    header[0] = SparkConstants.EntryMarker;
    header[1] = SparkConstants.MethodStored;
    Array.Copy(nameBytes, 0, header, 2, nameLen);
    // Remaining filename bytes stay 0 (NUL-terminated, padded).
    WriteUInt32Le(header, 15, (uint)data.Length); // compressedSize
    WriteUInt16Le(header, 19, dosDate);
    WriteUInt16Le(header, 21, dosTime);
    WriteUInt16Le(header, 23, crc);
    WriteUInt32Le(header, 25, (uint)data.Length); // originalSize

    spark.Write(header, 0, header.Length);
    if (data.Length > 0) spark.Write(data, 0, data.Length);
  }

  private static void WriteEoaMarker(Stream spark) {
    spark.WriteByte(SparkConstants.EntryMarker);
    spark.WriteByte(SparkConstants.MethodEndOfArchive);
  }

  // ── Low-level helpers ─────────────────────────────────────────────────

  private static int ReadFully(Stream s, byte[] buffer, int offset, int count) {
    var total = 0;
    while (total < count) {
      var n = s.Read(buffer, offset + total, count - total);
      if (n <= 0) break;
      total += n;
    }
    return total;
  }

  private static bool SkipBytes(Stream s, long count) {
    var newPos = s.Position + count;
    if (newPos > s.Length) return false;
    s.Position = newPos;
    return true;
  }

  private static void WriteUInt16Le(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)(value >> 8);
  }

  private static void WriteUInt32Le(byte[] buffer, int offset, uint value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    buffer[offset + 3] = (byte)(value >> 24);
  }

  private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int maxLength) {
    var end = offset;
    while (end < offset + maxLength && buffer[end] != 0) ++end;
    return Encoding.ASCII.GetString(buffer, offset, end - offset);
  }

  private static void ZeroRange(Stream s, long offset, long length) {
    var buf = new byte[(int)Math.Min(length, 8192)];
    s.Position = offset;
    var remaining = length;
    while (remaining > 0) {
      var chunk = (int)Math.Min(buf.Length, remaining);
      s.Write(buf, 0, chunk);
      remaining -= chunk;
    }
  }
}
