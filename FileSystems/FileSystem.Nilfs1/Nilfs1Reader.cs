#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Globalization;
using System.Text;

namespace FileSystem.Nilfs1;

/// <summary>
/// Reads NILFS v1 superblock metadata — the original (pre-mainline)
/// New Implementation of a Log-structured File System. NILFS v1 was the
/// out-of-tree precursor to NILFS2 (mainline since 2.6.30); it shares the
/// same 0x3434 superblock magic but uses <c>s_rev_level == 1</c>.
/// <para>
/// Full DAT-tree / cpfile-driven root-dir enumeration of an arbitrary external
/// NILFS v1 image is a multi-week effort (the cpfile inode walk and segment
/// usage table are sparsely documented for v1 specifically) — so this reader
/// surfaces metadata for unknown images, and reads our own writer's compact
/// directory index when the image carries the <see cref="Nilfs1Writer.WriterMagic"/>
/// marker right after the superblock.
/// </para>
/// <para>
/// Superblock layout (selected, little-endian, sits at file offset 1024):
///   0x00 u32 s_rev_level         (== 1 for NILFS v1)
///   0x04 u16 s_minor_rev_level
///   0x06 u16 s_magic             (must be 0x3434 = NILFS_SUPER_MAGIC)
///   0x08 u16 s_bytes
///   0x0A u16 s_flags
///   0x14 u32 s_log_block_size
///   0x18 u64 s_nsegments
///   0x20 u64 s_dev_size
///   0x30 u32 s_blocks_per_segment
///   0x38 u64 s_last_cno          (last checkpoint number)
///   0xA8 byte[80] volume label   (s_volume_name; written by Nilfs1Writer)
///   ...
/// </para>
/// </summary>
public sealed class Nilfs1Reader : IDisposable {
  private readonly ImageAccessor _img;
  private readonly long _len;
  private readonly List<Nilfs1Entry> _entries = [];

  public IReadOnlyList<Nilfs1Entry> Entries => _entries;

  public uint RevLevel { get; private set; }
  public ushort Magic { get; private set; }
  public uint LogBlockSize { get; private set; }
  public ulong NumSegments { get; private set; }
  public ulong DevSize { get; private set; }
  public uint BlocksPerSegment { get; private set; }
  public ulong LastCheckpoint { get; private set; }
  public bool ValidSuperblock { get; private set; }

  public const ushort SuperMagic = 0x3434;
  public const uint NilfsV1RevLevel = 1;
  private const int SuperblockOffset = 1024;
  private const int SuperblockSize = 1024;

  public Nilfs1Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // The directory is a few kilobytes however many gigabytes of payload follow
    // it, so the image is read through rather than copied in.
    _img = new ImageAccessor(stream);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private void Parse() {
    if (_len < SuperblockOffset + 0x80)
      throw new InvalidDataException("Nilfs1: image too small for superblock.");

    var sb = _img.Read(SuperblockOffset, (int)Math.Min(SuperblockSize, _len - SuperblockOffset)).AsSpan();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    if (magic != SuperMagic)
      throw new InvalidDataException($"Nilfs1: invalid magic 0x{magic:X4} at superblock+6 (expected 0x{SuperMagic:X4}).");

    var rev = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (rev != NilfsV1RevLevel)
      throw new InvalidDataException($"Nilfs1: s_rev_level={rev} (expected 1; rev>=2 is NILFS2, handled by Nilfs2 descriptor).");

    this.Magic = magic;
    this.ValidSuperblock = true;
    this.RevLevel         = rev;
    this.LogBlockSize     = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x14));
    this.NumSegments      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x18));
    this.DevSize          = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x20));
    this.BlocksPerSegment = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x30));
    this.LastCheckpoint   = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x38));

    // Detect our writer's compact-directory index. When present we enumerate
    // real file entries — folding the base directory (cno=1) and every appended
    // log segment ("NILFS1SG") together, highest-cno-per-name wins and tombstones
    // drop the entry. Otherwise we fall back to the metadata-surface behaviour
    // and the image's directory tree is N/A.
    var versions = new Dictionary<string, Record>(StringComparer.Ordinal);
    var payloadEnd = TryParseWriterDirectory(versions);
    if (payloadEnd >= 0) {
      ParseAppendedSegments(versions, payloadEnd);
      foreach (var (name, record) in versions) {
        if (record.Tombstone) continue;
        _entries.Add(new Nilfs1Entry {
          Name = name,
          Size = record.Size,
          IsDirectory = false,
          Offset = record.Offset,
        });
      }
      return;
    }

    var meta = BuildMetadata();
    var sbBytes = _img.Read(SuperblockOffset, (int)Math.Min(SuperblockSize, _len - SuperblockOffset));

    _entries.Add(new Nilfs1Entry { Name = "FULL.nilfs", Size = _len, IsDirectory = false, Offset = 0 });
    _entries.Add(new Nilfs1Entry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Data = meta });
    _entries.Add(new Nilfs1Entry { Name = "superblock.bin", Size = sbBytes.Length, IsDirectory = false, Data = sbBytes });
  }

  /// <summary>One version of a name: where its bytes are, and at which checkpoint.</summary>
  private readonly record struct Record(ulong Cno, bool Tombstone, long Offset, long Size);

  /// <summary>
  /// Reads the compact directory index our writer lays down at
  /// <see cref="Nilfs1Writer.SegmentStart"/>. Format: 8-byte magic, 8-byte
  /// directory-size, then a sequence of (u32 name_len, name, u64 offset, u64 size).
  /// Returns the end of the payload region — where appended segments start — or
  /// -1 when the image carries no writer directory.
  /// </summary>
  private long TryParseWriterDirectory(Dictionary<string, Record> versions) {
    if (_len < Nilfs1Writer.SegmentStart + Nilfs1Writer.WriterMagic.Length + 8) return -1;
    var head = _img.Read(Nilfs1Writer.SegmentStart, Nilfs1Writer.WriterMagic.Length + 8);
    if (!head.AsSpan(0, Nilfs1Writer.WriterMagic.Length).SequenceEqual(Nilfs1Writer.WriterMagic)) return -1;
    var dirSize = BinaryPrimitives.ReadInt64LittleEndian(head.AsSpan(Nilfs1Writer.WriterMagic.Length));
    if (dirSize < 0 || dirSize > int.MaxValue) return -1;
    var dirStart = (long)Nilfs1Writer.SegmentStart + Nilfs1Writer.WriterMagic.Length + 8;
    if (dirStart + dirSize > _len) return -1;
    var payloadStart = dirStart + dirSize;
    var dir = _img.Read(dirStart, (int)dirSize);

    var cursor = 0;
    var payloadEnd = payloadStart;
    while (cursor + 4 <= dir.Length) {
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(cursor));
      cursor += 4;
      if (nameLen <= 0 || cursor + nameLen + 16 > dir.Length) break;
      var name = Encoding.UTF8.GetString(dir.AsSpan(cursor, nameLen));
      cursor += nameLen;
      var off = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
      cursor += 8;
      var size = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
      cursor += 8;
      if (size < 0 || off < 0 || payloadStart + off + size > _len) break;
      // Base writer directory is the cno=1 checkpoint; appended segments can
      // supersede with a higher cno.
      versions[name] = new Record(Cno: 1ul, Tombstone: false, Offset: payloadStart + off, Size: size);
      payloadEnd = Math.Max(payloadEnd, payloadStart + off + size);
    }
    return payloadEnd;
  }

  /// <summary>
  /// Walks every appended "NILFS1SG" log segment past <paramref name="from"/> and
  /// folds its entries into <paramref name="versions"/>. Higher-cno records
  /// supersede lower-cno ones; tombstone records mark the entry deleted (the
  /// caller drops tombstones from the final listing). This is the
  /// continuous-snapshot replay semantic NILFS v1 shares with NILFS2 — the
  /// in-place modifier only ever appends these blocks at the tail, never
  /// relocating live data.
  /// </summary>
  private void ParseAppendedSegments(Dictionary<string, Record> versions, long from) {
    var magic = Nilfs1Writer.SegmentMagic;
    var p = Math.Max(0, from);
    while (p + magic.Length + 24 <= _len) {
      if (!_img.Read(p, magic.Length).AsSpan().SequenceEqual(magic)) {
        ++p;
        continue;
      }
      var hdr = _img.Read(p + magic.Length, 24);
      var cno = BinaryPrimitives.ReadUInt64LittleEndian(hdr.AsSpan(0));
      var entryCount = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(8));
      var dirSize = BinaryPrimitives.ReadInt64LittleEndian(hdr.AsSpan(16));
      var dirStart = p + magic.Length + 24;
      if (dirSize < 0 || dirSize > int.MaxValue || dirStart + dirSize > _len
          || entryCount < 0 || entryCount > _len) {
        ++p;
        continue;
      }
      var dir = _img.Read(dirStart, (int)dirSize);
      var payloadStart = dirStart + dirSize;
      var cursor = 0;
      var consumedPayload = 0L;
      var parsedOk = true;
      var staged = new List<(string Name, Record Record)>();
      for (var i = 0L; i < entryCount; ++i) {
        if (cursor + 4 > dir.Length) { parsedOk = false; break; }
        var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(cursor));
        cursor += 4;
        if (nameLen <= 0 || cursor + nameLen + 1 + 16 > dir.Length) { parsedOk = false; break; }
        var name = Encoding.UTF8.GetString(dir.AsSpan(cursor, nameLen));
        cursor += nameLen;
        var tombstone = dir[cursor] != 0;
        cursor += 1;
        var off = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
        cursor += 8;
        var size = BinaryPrimitives.ReadInt64LittleEndian(dir.AsSpan(cursor));
        cursor += 8;
        if (size < 0 || off < 0) { parsedOk = false; break; }
        if (tombstone || size == 0) {
          staged.Add((name, new Record(cno, tombstone, -1, 0)));
        } else {
          if (payloadStart + off + size > _len) { parsedOk = false; break; }
          staged.Add((name, new Record(cno, false, payloadStart + off, size)));
          consumedPayload = Math.Max(consumedPayload, off + size);
        }
      }
      if (!parsedOk) {
        ++p;
        continue;
      }
      foreach (var (name, record) in staged)
        if (!versions.TryGetValue(name, out var prev) || record.Cno >= prev.Cno)
          versions[name] = record;
      p = payloadStart + consumedPayload;
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=NILFS v1\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{this.Magic:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"rev_level={this.RevLevel}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"log_block_size={this.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={1u << (int)(this.LogBlockSize + 10)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_segments={this.NumSegments}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dev_size={this.DevSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_per_segment={this.BlocksPerSegment}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"last_checkpoint={this.LastCheckpoint}\n");
    bldr.Append("note=NILFS v1 cpfile-inode + segment usage walk not implemented (research read-only).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(Nilfs1Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0) return entry.Data;
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Nilfs1: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    return _img.Read(entry.Offset, (int)entry.Size);
  }

  /// <summary>Writes <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(Nilfs1Entry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.Offset < 0) {
      destination.Write(entry.Data);
      return entry.Data.Length;
    }
    var take = Math.Min(entry.Size, _len - entry.Offset);
    if (take <= 0) return 0;
    _img.CopyTo(entry.Offset, destination, take);
    return take;
  }

  public void Dispose() => this._img.Dispose();
}
