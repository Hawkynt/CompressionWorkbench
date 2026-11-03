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
  /// <remarks>
  /// A path with '/' puts the file into that folder: a FILE belongs to the folder of the
  /// DIRB before it, so when the chain does not already end in that folder a DIRB naming it
  /// (or the root, <c>\</c>) is appended first. Appending only the FILE put every new file
  /// into whichever folder the backup happened to end with.
  /// </remarks>
  public static void AddFile(Stream archive, string fileName, byte[] data) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);

    var path = NormalizePath(fileName);
    var slash = path.LastIndexOf('/');
    var folder = slash < 0 ? "" : path[..slash];
    var leaf = slash < 0 ? path : path[(slash + 1)..];
    if (leaf.Length == 0) throw new ArgumentException("BKF: a file needs a name.", nameof(fileName));

    var raw = ReadAll(archive);
    var (eotmPos, flb) = LocateEotmAndFlb(raw);
    var insertPos = RoundUpToFlb(eotmPos >= 0 ? eotmPos : raw.Length, flb);

    var blocks = new List<byte[]>();
    if (FolderAt(raw, flb, insertPos) != folder)
      blocks.Add(BuildDirbBlock(folder, flb));
    blocks.Add(BuildFileBlock(leaf, data, flb));
    Append(archive, raw, insertPos, blocks, flb);
  }

  /// <summary>
  /// Adds a folder: a DIRB naming it is appended (an empty folder is a DIRB with no FILE
  /// after it), unless the backup already has one for that path.
  /// </summary>
  public static void AddDirectory(Stream archive, string folderName) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(folderName);
    var folder = NormalizePath(folderName);
    if (folder.Length == 0) return;

    var raw = ReadAll(archive);
    var (eotmPos, flb) = LocateEotmAndFlb(raw);
    foreach (var block in Walk(raw, flb))
      if (block.Type == "DIRB" && block.Folder == folder) return;
    var insertPos = RoundUpToFlb(eotmPos >= 0 ? eotmPos : raw.Length, flb);
    Append(archive, raw, insertPos, [BuildDirbBlock(folder, flb)], flb);
  }

  // Writes head bytes (0..insertPos) + the new blocks + a fresh EOTM.
  private static void Append(Stream archive, byte[] raw, int insertPos, List<byte[]> blocks, int flb) {
    var eotmBlock = BuildEotmBlock(flb);
    var newLen = insertPos + blocks.Sum(b => b.Length) + eotmBlock.Length;
    var output = new byte[newLen];
    Buffer.BlockCopy(raw, 0, output, 0, Math.Min(insertPos, raw.Length));
    var at = insertPos;
    foreach (var b in blocks) {
      Buffer.BlockCopy(b, 0, output, at, b.Length);
      at += b.Length;
    }
    Buffer.BlockCopy(eotmBlock, 0, output, at, eotmBlock.Length);

    archive.SetLength(0);
    archive.Position = 0;
    archive.Write(output, 0, output.Length);
  }

  /// <summary>
  /// Tombstones the entry at exactly <paramref name="fileName"/> — a path as the reader lists
  /// it, the folder taken from the DIRB before each FILE — or, for a folder, its DIRBs and
  /// every FILE beneath it: the 4-byte type field becomes the <c>XXXX</c> sentinel and the
  /// rest of that FLB-aligned block (CBH tail + every attached stream payload) is zero-wiped.
  /// Returns <c>true</c> when a tombstone was applied.
  /// </summary>
  /// <remarks>
  /// Names used to match the FILE name alone, so removing "a.txt" also removed "d/a.txt".
  /// </remarks>
  public static bool RemoveFile(Stream archive, string fileName) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(fileName);
    var target = NormalizePath(fileName);
    if (target.Length == 0) return false;

    var raw = ReadAll(archive);
    var (_, flb) = LocateEotmAndFlb(raw);
    var removed = false;
    foreach (var block in Walk(raw, flb)) {
      var path = block.Type switch {
        "FILE" => block.Folder.Length == 0 ? block.Name : block.Folder + "/" + block.Name,
        "DIRB" => block.Folder,
        _ => null,
      };
      if (path == null || path.Length == 0) continue;
      if (path != target && !path.StartsWith(target + "/", StringComparison.Ordinal)) continue;
      // Tombstone: type → XXXX sentinel, zero the remainder of the block.
      var tomb = new byte[block.Size];
      tomb[0] = (byte)'X';
      tomb[1] = (byte)'X';
      tomb[2] = (byte)'X';
      tomb[3] = (byte)'X';
      archive.Position = block.Position;
      archive.Write(tomb, 0, tomb.Length);
      removed = true;
    }
    return removed;
  }

  private readonly record struct Dblk(int Position, int Size, string Type, string Folder, string Name);

  /// <summary>
  /// Every DBLK up to EOTM with the folder it belongs to, as the reader derives it: a DIRB
  /// sets the folder for what follows, an SSET or VOLB resets it to the root.
  /// </summary>
  private static IEnumerable<Dblk> Walk(byte[] raw, int flb) {
    var folder = "";
    var pos = 0;
    while (pos + CommonBlockHeaderSize <= raw.Length) {
      var type = ReadAscii4(raw, pos);
      if (type == "EOTM") yield break;
      var size = type is "FILE" or "DIRB" ? MeasureDblkSize(raw, pos, flb) : flb;
      if (size <= 0) yield break;
      var name = "";
      if (type is "SSET" or "VOLB") folder = "";
      if (type == "DIRB") {
        var pnam = ExtractStream(raw, pos, size, "PNAM");
        if (pnam.Length > 0) folder = NormalizePath(pnam);
      } else if (type == "FILE") {
        name = ExtractStream(raw, pos, size, "FNAM");
      }
      yield return new Dblk(pos, size, type, folder, name);
      pos = RoundUpToFlb(pos + size, flb);
    }
  }

  /// <summary>The folder a FILE appended at <paramref name="endPos"/> would belong to.</summary>
  private static string FolderAt(byte[] raw, int flb, int endPos) {
    var folder = "";
    foreach (var block in Walk(raw, flb)) {
      if (block.Position >= endPos) break;
      folder = block.Type is "SSET" or "VOLB" ? "" : block.Folder;
    }
    return folder;
  }

  private static string NormalizePath(string raw) => raw.Replace('\\', '/').Trim('/');

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
  private static string ExtractStream(byte[] raw, int dblkPos, int blockSize, string streamId) {
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

      if (id == streamId && streamLen > 0) {
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

  /// <summary>
  /// A DIRB naming <paramref name="folder"/> ('/'-separated; empty for the root) with
  /// back-slashes as ntbackup writes them, FLB-rounded.
  /// </summary>
  private static byte[] BuildDirbBlock(string folder, int flb) {
    var nameBytes = Encoding.Latin1.GetBytes(folder.Length == 0 ? "\\" : folder.Replace('/', '\\'));
    var block = new byte[RoundUpToFlb(CommonBlockHeaderSize + StreamFootprint(nameBytes.Length), flb)];
    WriteCbh(block, "DIRB", StringTypeAnsi);
    WriteStream(block, CommonBlockHeaderSize, "PNAM", nameBytes);
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
