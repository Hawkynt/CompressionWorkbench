#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// In-place modifier for Microsoft NTBackup (.bkf) Microsoft Tape Format (MTF)
/// containers. Implements two byte-preserving mutation primitives against the
/// FLB-aligned DBLK chain:
///
/// <list type="bullet">
///   <item><description><b>Append</b>: writes a fresh FILE DBLK (CBH + FNAM
///   stream + STAN payload) at the position of the existing EOTM (End-Of-Tape
///   Marker) block — or at EOF when EOTM is absent — then re-emits an EOTM block
///   at the new end. All pre-existing DBLKs (TAPE, SSET, VOLB, prior FILE/DIRB,
///   ESET) stay byte-identical at their original offsets.</description></item>
///   <item><description><b>Tombstone</b>: locates the FILE DBLK whose FNAM
///   matches the requested name, overwrites its 4-byte type field with the
///   <c>XXXX</c> sentinel, then zero-wipes the rest of that FLB so the FNAM /
///   STAN streams leave no forensic trace. The reader's parse loop hits an
///   unknown DBLK type and advances to the next FLB boundary, so the tombstoned
///   entry simply disappears from <see cref="BkfReader.Entries"/>. Surrounding
///   DBLKs remain byte-identical at their original offsets.</description></item>
/// </list>
///
/// <para>
/// The strategy avoids the legacy 1990s ntbackup "tape link offset" mechanism
/// — MTF stores cumulative tape addresses in CBH fields that any post-hoc
/// insertion would have to renumber across every DBLK that follows. The
/// reader doesn't honour those fields (it walks by FLB boundaries instead),
/// so the simpler append + tombstone scheme is forensically clean and
/// round-trips through <see cref="BkfReader"/>.
/// </para>
/// </summary>
public static class BkfInPlaceModifier {

  private const int CommonBlockHeaderSize = 52;
  private const int StreamHeaderSize = 22;
  private const ushort StringTypeAnsi = 1;

  /// <summary>
  /// Appends a new FILE DBLK carrying <paramref name="data"/> at the position
  /// where the current EOTM block lives (or at EOF when EOTM is absent), then
  /// re-emits a fresh EOTM block at the new end. The TAPE / SSET / VOLB and any
  /// pre-existing FILE/DIRB blocks remain byte-identical at their original
  /// offsets.
  /// </summary>
  /// <param name="archive">Seekable, writable BKF stream.</param>
  /// <param name="fileName">File name written into the FILE DBLK's FNAM stream.</param>
  /// <param name="data">Payload written as the FILE's STAN data stream.</param>
  public static void AddFile(Stream archive, string fileName, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var raw = ReadAll(archive);
    var (eotmPos, flb) = LocateEotmAndFlb(raw);

    var insertPos = eotmPos >= 0 ? eotmPos : raw.Length;
    insertPos = RoundUpToFlb(insertPos, flb);

    var fileBlock = BuildFileBlock(fileName, data, flb);
    var eotmBlock = BuildEotmBlock(flb);

    // New layout: head bytes (0..insertPos) + new FILE DBLK + new EOTM.
    var newLen = insertPos + fileBlock.Length + eotmBlock.Length;
    var output = new byte[newLen];
    Buffer.BlockCopy(raw, 0, output, 0, insertPos);
    Buffer.BlockCopy(fileBlock, 0, output, insertPos, fileBlock.Length);
    Buffer.BlockCopy(eotmBlock, 0, output, insertPos + fileBlock.Length, eotmBlock.Length);

    archive.SetLength(0);
    archive.Position = 0;
    archive.Write(output, 0, output.Length);
  }

  /// <summary>
  /// Tombstones the first FILE DBLK whose FNAM matches <paramref name="fileName"/>:
  /// the 4-byte type field becomes the <c>XXXX</c> sentinel and the rest of that
  /// FLB-aligned FILE block (CBH tail + every attached stream payload) is
  /// zero-wiped. Returns <c>true</c> when a tombstone was applied.
  /// </summary>
  public static bool RemoveFile(Stream archive, string fileName) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);

    var raw = ReadAll(archive);
    var (_, flb) = LocateEotmAndFlb(raw);

    var pos = 0;
    while (pos + CommonBlockHeaderSize <= raw.Length) {
      var blockType = ReadAscii4(raw, pos);
      if (blockType == "EOTM") break;

      if (blockType != "FILE") {
        pos = RoundUpToFlb(pos + flb, flb);
        if (pos <= 0) break;
        continue;
      }

      var blockSize = MeasureDblkSize(raw, pos, flb);
      var name = ExtractFnam(raw, pos, blockSize);
      if (string.Equals(name, fileName, StringComparison.Ordinal)) {
        // Tombstone: type → XXXX sentinel, zero the remainder of the FLB block.
        var tomb = new byte[blockSize];
        tomb[0] = (byte)'X';
        tomb[1] = (byte)'X';
        tomb[2] = (byte)'X';
        tomb[3] = (byte)'X';
        archive.Position = pos;
        archive.Write(tomb, 0, tomb.Length);
        return true;
      }

      pos += blockSize;
    }

    return false;
  }

  // ── Layout helpers ────────────────────────────────────────────────────

  /// <summary>
  /// Scans the BKF stream for the FLB size (from the TAPE DBLK) and the byte
  /// position of the EOTM block, when present. Returns (-1, FLB) when EOTM
  /// can't be located — the caller falls back to EOF append.
  /// </summary>
  private static (int EotmPos, int Flb) LocateEotmAndFlb(byte[] raw) {
    var flb = 1024;
    if (raw.Length >= CommonBlockHeaderSize + 4) {
      var t = ReadAscii4(raw, 0);
      if (t == "TAPE") {
        var candidate = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(52, 4));
        if (candidate is > 0 and <= 65536 && (candidate & (candidate - 1)) == 0)
          flb = candidate;
      }
    }

    var pos = 0;
    while (pos + CommonBlockHeaderSize <= raw.Length) {
      var type = ReadAscii4(raw, pos);
      if (type == "EOTM") return (pos, flb);
      pos = RoundUpToFlb(pos + flb, flb);
      if (pos <= 0) break;
    }
    return (-1, flb);
  }

  /// <summary>
  /// Returns the FLB-rounded byte size of the DBLK starting at
  /// <paramref name="pos"/>. Walks attached streams until SPAD or the next
  /// DBLK boundary, then rounds up.
  /// </summary>
  private static int MeasureDblkSize(byte[] raw, int pos, int flb) {
    var minStart = pos + CommonBlockHeaderSize;
    var maxStart = Math.Min(raw.Length - StreamHeaderSize, pos + flb);
    var streamStart = -1;
    for (var scan = (minStart + 3) & ~3; scan <= maxStart; scan += 4) {
      if (IsKnownStreamId(raw, scan)) { streamStart = scan; break; }
    }
    if (streamStart < 0) return Math.Min(flb, raw.Length - pos);

    var cursor = streamStart;
    while (cursor + StreamHeaderSize <= raw.Length) {
      if (!IsKnownStreamId(raw, cursor)) break;
      var streamLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(cursor + 8, 8));
      if (streamLen < 0) break;
      var payloadEnd = (long)cursor + StreamHeaderSize + streamLen;
      payloadEnd = (payloadEnd + 3) & ~3L;
      if (payloadEnd > raw.Length) { cursor = raw.Length; break; }
      var idIsSpad = ReadAscii4(raw, cursor) == "SPAD";
      cursor = (int)payloadEnd;
      if (idIsSpad) break;
    }
    var rounded = RoundUpToFlb(cursor, flb);
    return Math.Min(rounded - pos, raw.Length - pos);
  }

  /// <summary>
  /// Reads the FNAM stream attached to the FILE DBLK at <paramref name="dblkPos"/>
  /// (limited to <paramref name="blockSize"/> bytes) and returns the decoded
  /// string. Empty string when not present.
  /// </summary>
  private static string ExtractFnam(byte[] raw, int dblkPos, int blockSize) {
    var minStart = dblkPos + CommonBlockHeaderSize;
    var maxScan = dblkPos + blockSize - StreamHeaderSize;
    var cursor = -1;
    for (var scan = (minStart + 3) & ~3; scan <= maxScan; scan += 4) {
      if (IsKnownStreamId(raw, scan)) { cursor = scan; break; }
    }
    if (cursor < 0) return "";

    while (cursor + StreamHeaderSize <= dblkPos + blockSize) {
      if (!IsKnownStreamId(raw, cursor)) break;
      var id = ReadAscii4(raw, cursor);
      var streamLen = (long)BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(cursor + 8, 8));
      if (streamLen < 0) break;
      var dataStart = cursor + StreamHeaderSize;
      var payloadEnd = (long)dataStart + streamLen;
      var alignedEnd = (payloadEnd + 3) & ~3L;
      if (payloadEnd > raw.Length) break;

      if (id == "FNAM" && streamLen > 0) {
        var bytes = raw.AsSpan(dataStart, (int)streamLen);
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        return Encoding.Latin1.GetString(bytes[..end]);
      }

      cursor = (int)alignedEnd;
      if (id == "SPAD") break;
    }
    return "";
  }

  // ── Block builders ────────────────────────────────────────────────────

  /// <summary>
  /// Builds a FLB-rounded FILE DBLK with CBH + FNAM + STAN streams matching
  /// the layout the test fixture in <c>BkfReaderTests.MtfBuilder</c> produces.
  /// </summary>
  private static byte[] BuildFileBlock(string fileName, byte[] data, int flb) {
    var nameBytes = Encoding.Latin1.GetBytes(fileName);
    var fnamFootprint = StreamFootprint(nameBytes.Length);
    var stanFootprint = StreamFootprint(data.Length);
    var rawSize = CommonBlockHeaderSize + fnamFootprint + stanFootprint;
    var padded = RoundUpToFlb(rawSize, flb);
    var block = new byte[padded];

    WriteCbh(block, "FILE", StringTypeAnsi);
    var afterFnam = WriteStream(block, CommonBlockHeaderSize, "FNAM", nameBytes);
    WriteStream(block, afterFnam, "STAN", data);
    return block;
  }

  private static byte[] BuildEotmBlock(int flb) {
    var block = new byte[flb];
    WriteCbh(block, "EOTM", stringType: 0);
    return block;
  }

  private static void WriteCbh(byte[] block, string blockType, ushort stringType) {
    Encoding.ASCII.GetBytes(blockType).CopyTo(block, 0);
    // OffsetToFirstEvent (CbhSize) at [8..10]
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), CommonBlockHeaderSize);
    // OS_ID = 14 (NT), OS_Ver = 1
    block[10] = 14;
    block[11] = 1;
    // String type at offset 46
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(46), stringType);
    // Checksum at [50..52] left zero — reader does not verify.
  }

  /// <summary>
  /// Writes one MTF stream header + payload + 4-byte alignment padding at
  /// <paramref name="offset"/>. Returns the offset of the next stream slot.
  /// </summary>
  private static int WriteStream(byte[] block, int offset, string streamId, byte[] payload) {
    Encoding.ASCII.GetBytes(streamId).CopyTo(block, offset);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset + 8), (ulong)payload.Length);
    var dataStart = offset + StreamHeaderSize;
    if (payload.Length > 0) Buffer.BlockCopy(payload, 0, block, dataStart, payload.Length);
    var end = dataStart + payload.Length;
    return (end + 3) & ~3;
  }

  private static int StreamFootprint(int payloadLength) => ((StreamHeaderSize + payloadLength) + 3) & ~3;

  // ── Low-level helpers ─────────────────────────────────────────────────

  private static byte[] ReadAll(Stream archive) {
    if (!archive.CanSeek)
      throw new ArgumentException("BKF in-place modify requires a seekable stream.", nameof(archive));
    archive.Position = 0;
    var buf = new byte[archive.Length];
    var read = 0;
    while (read < buf.Length) {
      var n = archive.Read(buf, read, buf.Length - read);
      if (n <= 0) break;
      read += n;
    }
    if (read != buf.Length)
      throw new InvalidDataException($"BKF: failed to read full stream ({read}/{buf.Length} bytes).");
    return buf;
  }

  private static string ReadAscii4(byte[] data, int offset) {
    if (offset < 0 || offset + 4 > data.Length) return "";
    return Encoding.ASCII.GetString(data, offset, 4);
  }

  private static bool IsKnownStreamId(byte[] data, int offset) {
    var id = ReadAscii4(data, offset);
    return id is "STAN" or "PNAM" or "FNAM" or "SPAD" or "CSUM" or "TSMP" or "MQCI";
  }

  private static int RoundUpToFlb(int value, int flb) {
    if (flb <= 0) return value;
    return ((value + flb - 1) / flb) * flb;
  }
}
