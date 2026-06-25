#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.Nilfs1;

/// <summary>
/// True in-place modifier for NILFS v1 images emitted by <see cref="Nilfs1Writer"/>.
/// Implements the log-structured continuous-snapshot semantic NILFS v1 shares with
/// NILFS2: every mutation appends a fresh logical segment ("NILFS1SG") at the tail
/// of the volume and bumps the superblock's <c>s_last_cno</c>, leaving every byte of
/// the prior image byte-identical at its original offset — the single 8-byte
/// <c>s_last_cno</c> field is the only in-place edit. Old segments stay recoverable
/// as snapshots.
/// </summary>
/// <remarks>
/// <para>NILFS v1 has no mainline mkfs/mount tool (it predates the in-tree NILFS2
/// driver), so there is no external kernel gate. Correctness is proven by the
/// reader round-trip plus the in-place offset invariant: <see cref="Add"/>,
/// <see cref="Replace"/> and <see cref="Remove"/> preserve every byte in
/// <c>[0, oldLength)</c> except the 8-byte <c>s_last_cno</c> field at
/// superblock+0x38, and the image grows by only O(bytes changed) — the new
/// segment is appended, never a whole-image re-lay.</para>
///
/// <para>Each appended segment block carries:
/// <c>SegmentMagic ("NILFS1SG") | u64 cno | i64 entryCount | i64 dirSize | dir | payload</c>.
/// Per directory entry: u32 nameLen, name bytes, u8 tombstone-flag (0=live,
/// 1=removed), i64 payload-offset (relative to this segment's payload region),
/// i64 size. The reader folds all segments by highest-cno-per-name and drops
/// tombstoned entries.</para>
/// </remarks>
public static class Nilfs1InPlaceModifier {

  private const int SuperblockOffset = Nilfs1Writer.SuperblockOffsetOnDisk;
  private const int LastCnoOffset = Nilfs1Writer.LastCnoFieldOffset;

  /// <summary>
  /// Appends a new logical segment carrying fresh dirent + data blocks for each
  /// input file and bumps <c>s_last_cno</c>. Existing segments stay byte-identical
  /// at their original offsets; only the 8-byte checkpoint-number field changes.
  /// Inputs whose name already exists are effectively replaced (the higher cno
  /// wins on read).
  /// </summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    EnsureRwSeek(image);

    var entries = new List<SegmentEntry>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      var name = input.ArchiveName.Replace('\\', '/');
      if (name.Length == 0) continue;
      entries.Add(new SegmentEntry(name, IsTombstone: false, Data: input.ReadContent()));
    }
    if (entries.Count == 0) return;

    AppendSegmentAndBumpCno(image, entries);
  }

  /// <summary>
  /// Appends a new logical segment carrying a fresh data block for the named file
  /// and bumps <c>s_last_cno</c>. Old data blocks stay byte-identical at their
  /// original offsets; the reader's highest-cno-per-name merge surfaces the new
  /// content.
  /// </summary>
  public static void Replace(Stream image, string name, byte[] newData) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(newData);
    EnsureRwSeek(image);

    var normalized = name.Replace('\\', '/');
    var live = ReadLiveNames(image);
    if (!live.Contains(normalized))
      throw new FileNotFoundException($"Nilfs1 entry '{normalized}' not present (or already tombstoned).");

    AppendSegmentAndBumpCno(image, [new SegmentEntry(normalized, IsTombstone: false, Data: newData)]);
  }

  /// <summary>
  /// Appends a new logical segment containing a tombstone dirent for each named
  /// entry and bumps <c>s_last_cno</c>. Old data blocks stay byte-identical; the
  /// reader's tombstone-aware merge drops the entry from the listing.
  /// </summary>
  public static void Remove(Stream image, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    EnsureRwSeek(image);

    var live = ReadLiveNames(image);
    var entries = new List<SegmentEntry>();
    foreach (var raw in entryNames) {
      if (string.IsNullOrEmpty(raw)) continue;
      var normalized = raw.Replace('\\', '/');
      if (!live.Contains(normalized)) continue;
      entries.Add(new SegmentEntry(normalized, IsTombstone: true, Data: []));
    }
    if (entries.Count == 0) return;
    AppendSegmentAndBumpCno(image, entries);
  }

  // ── Internals ───────────────────────────────────────────────────────────

  private readonly record struct SegmentEntry(string Name, bool IsTombstone, byte[] Data);

  private static void EnsureRwSeek(Stream image) {
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Nilfs1 in-place modify requires a read/write/seek stream.", nameof(image));
  }

  /// <summary>
  /// Returns the set of currently-live entry names per the existing image's
  /// writer-private directory + every appended segment in cno order.
  /// </summary>
  private static HashSet<string> ReadLiveNames(Stream image) {
    image.Position = 0;
    var reader = new Nilfs1Reader(image);
    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (var e in reader.Entries) {
      if (e.IsDirectory) continue;
      if (e.Name is "FULL.nilfs" or "metadata.ini" or "superblock.bin") continue;
      names.Add(e.Name);
    }
    return names;
  }

  /// <summary>Reads <c>s_last_cno</c> directly from the superblock.</summary>
  private static ulong ReadLastCno(Stream image) {
    image.Position = SuperblockOffset + LastCnoOffset;
    var buf = new byte[8];
    var read = image.Read(buf, 0, 8);
    if (read != 8)
      throw new InvalidDataException("Nilfs1: image too small to read s_last_cno.");
    return BinaryPrimitives.ReadUInt64LittleEndian(buf);
  }

  /// <summary>
  /// Writes <paramref name="newCno"/> into the superblock's 8-byte
  /// <c>s_last_cno</c> field — the only in-place edit; every other mutation lives
  /// in the appended segment at the image's tail.
  /// </summary>
  private static void WriteLastCno(Stream image, ulong newCno) {
    var buf = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(buf, newCno);
    image.Position = SuperblockOffset + LastCnoOffset;
    image.Write(buf, 0, 8);
  }

  private static void AppendSegmentAndBumpCno(Stream image, IReadOnlyList<SegmentEntry> entries) {
    var oldCno = ReadLastCno(image);
    var newCno = oldCno + 1;

    var segmentBytes = BuildSegmentBlock(newCno, entries);

    image.Position = image.Length;
    image.Write(segmentBytes, 0, segmentBytes.Length);

    WriteLastCno(image, newCno);
    image.Flush();
  }

  /// <summary>
  /// Serialises one appended-segment block in the on-disk layout the reader
  /// expects:
  /// <c>SegmentMagic | u64 cno | i64 entryCount | i64 dirSize | dir | payload</c>.
  /// </summary>
  private static byte[] BuildSegmentBlock(ulong cno, IReadOnlyList<SegmentEntry> entries) {
    var dirSize = 0;
    foreach (var e in entries) {
      var nameLen = Encoding.UTF8.GetByteCount(e.Name);
      dirSize += 4 + nameLen + 1 + 8 + 8; // u32 nameLen | name | u8 tombstone | i64 off | i64 size
    }

    var payloadSize = 0L;
    foreach (var e in entries)
      if (!e.IsTombstone)
        payloadSize += e.Data.LongLength;

    var totalSize = Nilfs1Writer.SegmentMagic.Length + 8 + 8 + 8 + dirSize + (int)payloadSize;
    var buf = new byte[totalSize];
    var span = buf.AsSpan();

    Nilfs1Writer.SegmentMagic.CopyTo(span);
    var cursor = Nilfs1Writer.SegmentMagic.Length;

    BinaryPrimitives.WriteUInt64LittleEndian(span[cursor..], cno);
    cursor += 8;
    BinaryPrimitives.WriteInt64LittleEndian(span[cursor..], entries.Count);
    cursor += 8;
    BinaryPrimitives.WriteInt64LittleEndian(span[cursor..], dirSize);
    cursor += 8;

    var dirStart = cursor;
    var payloadStart = dirStart + dirSize;
    var payloadCursor = 0L;

    foreach (var e in entries) {
      var nameBytes = Encoding.UTF8.GetBytes(e.Name);
      BinaryPrimitives.WriteUInt32LittleEndian(span[cursor..], (uint)nameBytes.Length);
      cursor += 4;
      nameBytes.CopyTo(span[cursor..]);
      cursor += nameBytes.Length;
      span[cursor] = e.IsTombstone ? (byte)1 : (byte)0;
      cursor += 1;
      BinaryPrimitives.WriteInt64LittleEndian(span[cursor..], payloadCursor);
      cursor += 8;
      BinaryPrimitives.WriteInt64LittleEndian(span[cursor..], e.Data.LongLength);
      cursor += 8;

      if (!e.IsTombstone && e.Data.Length > 0) {
        e.Data.AsSpan().CopyTo(span[(payloadStart + (int)payloadCursor)..]);
        payloadCursor += e.Data.LongLength;
      }
    }

    return buf;
  }
}
