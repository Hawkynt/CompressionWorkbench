#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Bkf;

/// <summary>
/// Writes a complete Microsoft NTBackup (<c>.bkf</c>) container in the Microsoft
/// Tape Format (MTF) v1.0 from a set of files and directories. Emits the full
/// DBLK chain the spec prescribes — and that <see cref="BkfReader"/> parses —
/// in order:
/// <code>
///   TAPE → SSET → VOLB → (DIRB → FILE*)* → ESET → EOTM
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Every Descriptor Block (DBLK) begins on a Format Logical Block (FLB)
/// boundary (1024 bytes by default, declared in the TAPE DBLK at offset 52).
/// Each DBLK carries a 52-byte Common Block Header (CBH) followed by attached
/// MTF streams: a directory's path name lives in a <c>PNAM</c> stream, a file's
/// name in an <c>FNAM</c> stream, and a file's payload in a <c>STAN</c>
/// (Standard) data stream. Stream payloads are padded to 4-byte alignment, and
/// each DBLK as a whole is padded out to the next FLB boundary.
/// </para>
/// <para>
/// Files are grouped by their parent directory. The root directory's files are
/// emitted directly under the VOLB; files in sub-directories are preceded by a
/// DIRB whose PNAM holds the directory path (back-slash separated, matching
/// ntbackup.exe convention). The reader reconstructs each file's full relative
/// path by combining the most recent DIRB path with the FILE name.
/// </para>
/// <para>
/// Output is uncompressed (<c>STAN</c> stored, compression algorithm field 0) —
/// the MTF spec does not name a compression algorithm, and this is what
/// ntbackup.exe wrote in practice. The result round-trips byte-identically
/// through <see cref="BkfReader"/>.
/// </para>
/// </remarks>
public static class BkfWriter {

  private const int CommonBlockHeaderSize = 52;
  private const int StreamHeaderSize = 22;
  private const int DefaultLogicalBlockSize = 1024;
  private const ushort StringTypeAnsi = 1;
  private const ushort StringTypeNone = 0;
  private const byte OsIdNt = 14;
  private const byte OsVerNt = 1;

  /// <summary>One file or directory destined for the backup, with archive-relative path.</summary>
  /// <param name="Path">Archive-relative path using <c>/</c> or <c>\</c> separators.</param>
  /// <param name="Data">File payload. Ignored when <paramref name="IsDirectory"/> is true.</param>
  /// <param name="IsDirectory">True for an explicit directory entry.</param>
  public readonly record struct Item(string Path, byte[] Data, bool IsDirectory);

  /// <summary>
  /// Builds a full MTF backup from <paramref name="items"/> and returns the raw
  /// bytes. Files are bucketed by their parent directory; a DIRB precedes every
  /// non-root group. The default 1024-byte FLB is used.
  /// </summary>
  public static byte[] Build(IEnumerable<Item> items)
    => Build(items, DefaultLogicalBlockSize);

  /// <summary>
  /// Builds a full MTF backup with an explicit Format Logical Block size
  /// (must be a power of two between 512 and 65536).
  /// </summary>
  public static byte[] Build(IEnumerable<Item> items, int logicalBlockSize) {
    ArgumentNullException.ThrowIfNull(items);
    if (logicalBlockSize < 512 || logicalBlockSize > 65536 ||
        (logicalBlockSize & (logicalBlockSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(
        nameof(logicalBlockSize), "FLB size must be a power of two in [512, 65536].");

    // Bucket files by their normalised parent directory, preserving first-seen
    // order of both directories and the files within each. Explicit directory
    // items create (possibly empty) buckets so empty dirs still emit a DIRB.
    var order = new List<string>();
    var buckets = new Dictionary<string, List<(string Name, byte[] Data)>>(StringComparer.Ordinal);

    void EnsureBucket(string dir) {
      if (buckets.ContainsKey(dir)) return;
      buckets[dir] = [];
      order.Add(dir);
    }

    foreach (var item in items) {
      var norm = NormalizeDir(item.Path);
      if (item.IsDirectory) {
        if (norm.Length > 0) EnsureBucket(norm);
        continue;
      }

      var (dir, leaf) = SplitDirAndLeaf(item.Path);
      if (string.IsNullOrEmpty(leaf)) continue;
      EnsureBucket(dir);
      buckets[dir].Add((leaf, item.Data ?? []));
    }

    using var ms = new MemoryStream();
    WriteFlbBlock(ms, BuildTapeBlock(logicalBlockSize), logicalBlockSize);
    WriteFlbBlock(ms, BuildContainerBlock("SSET"), logicalBlockSize);
    WriteFlbBlock(ms, BuildContainerBlock("VOLB"), logicalBlockSize);

    foreach (var dir in order) {
      if (dir.Length > 0)
        WriteFlbBlock(ms, BuildDirbBlock(dir), logicalBlockSize);
      foreach (var (name, data) in buckets[dir])
        WriteFlbBlock(ms, BuildFileBlock(name, data), logicalBlockSize);
    }

    WriteFlbBlock(ms, BuildContainerBlock("ESET"), logicalBlockSize);
    WriteFlbBlock(ms, BuildContainerBlock("EOTM"), logicalBlockSize);
    return ms.ToArray();
  }

  // ── DBLK builders ─────────────────────────────────────────────────────

  private static byte[] BuildTapeBlock(int flb) {
    var block = new byte[CommonBlockHeaderSize + 4];
    WriteCbh(block, "TAPE", StringTypeAnsi);
    // Format Logical Block Size at offset 52 (uint32 LE) — read back by BkfReader.
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(CommonBlockHeaderSize), (uint)flb);
    return block;
  }

  private static byte[] BuildContainerBlock(string type) {
    var stringType = type == "EOTM" ? StringTypeNone : StringTypeAnsi;
    var block = new byte[CommonBlockHeaderSize];
    WriteCbh(block, type, stringType);
    return block;
  }

  private static byte[] BuildDirbBlock(string dirPath) {
    // ntbackup.exe stores DIRB path names with back-slash separators.
    var nameBytes = Encoding.Latin1.GetBytes(dirPath.Replace('/', '\\'));
    var size = CommonBlockHeaderSize + StreamFootprint(nameBytes.Length);
    var block = new byte[size];
    WriteCbh(block, "DIRB", StringTypeAnsi);
    WriteStream(block, CommonBlockHeaderSize, "PNAM", nameBytes);
    return block;
  }

  private static byte[] BuildFileBlock(string fileName, byte[] data) {
    var nameBytes = Encoding.Latin1.GetBytes(fileName);
    var size = CommonBlockHeaderSize + StreamFootprint(nameBytes.Length) + StreamFootprint(data.Length);
    var block = new byte[size];
    WriteCbh(block, "FILE", StringTypeAnsi);
    var afterFnam = WriteStream(block, CommonBlockHeaderSize, "FNAM", nameBytes);
    WriteStream(block, afterFnam, "STAN", data);
    return block;
  }

  // ── Low-level emit helpers ────────────────────────────────────────────

  /// <summary>Writes <paramref name="block"/> followed by zero-padding out to the next FLB boundary.</summary>
  private static void WriteFlbBlock(Stream output, byte[] block, int flb) {
    output.Write(block, 0, block.Length);
    var rounded = RoundUpToFlb(block.Length, flb);
    var pad = rounded - block.Length;
    if (pad > 0) output.Write(new byte[pad], 0, pad);
  }

  private static void WriteCbh(byte[] block, string blockType, ushort stringType) {
    Encoding.ASCII.GetBytes(blockType).CopyTo(block, 0);
    // Block attributes [4..8] left zero.
    // OffsetToFirstEvent (CBH size) at [8..10].
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(8), CommonBlockHeaderSize);
    block[10] = OsIdNt;   // OS_ID = Windows NT
    block[11] = OsVerNt;  // OS_Ver
    // String type at offset 46 (1 = ANSI, 0 = none).
    BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(46), stringType);
    // Header checksum at [50..52] left zero — BkfReader does not verify it.
  }

  /// <summary>
  /// Writes one MTF stream header + payload + 4-byte alignment padding at
  /// <paramref name="offset"/>. Returns the offset of the next stream slot.
  /// </summary>
  private static int WriteStream(byte[] block, int offset, string streamId, byte[] payload) {
    Encoding.ASCII.GetBytes(streamId).CopyTo(block, offset);
    // FS attributes / media attributes [4..8] left zero.
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(offset + 8), (ulong)payload.Length);
    // Encryption [16], compression [18], checksum [20] left zero (stored, no encryption).
    var dataStart = offset + StreamHeaderSize;
    if (payload.Length > 0) Buffer.BlockCopy(payload, 0, block, dataStart, payload.Length);
    var end = dataStart + payload.Length;
    return (end + 3) & ~3;
  }

  private static int StreamFootprint(int payloadLength)
    => (StreamHeaderSize + payloadLength + 3) & ~3;

  private static int RoundUpToFlb(int value, int flb)
    => ((value + flb - 1) / flb) * flb;

  // ── Path handling ─────────────────────────────────────────────────────

  /// <summary>Normalises a directory path to forward slashes with no leading/trailing slash.</summary>
  private static string NormalizeDir(string raw)
    => (raw ?? "").Replace('\\', '/').Trim('/');

  /// <summary>Splits an archive-relative file path into (normalised directory, leaf file name).</summary>
  private static (string Dir, string Leaf) SplitDirAndLeaf(string path) {
    var norm = (path ?? "").Replace('\\', '/').TrimStart('/');
    var slash = norm.LastIndexOf('/');
    return slash < 0
      ? ("", norm)
      : (norm[..slash].Trim('/'), norm[(slash + 1)..]);
  }
}
