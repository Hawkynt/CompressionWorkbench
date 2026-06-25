#pragma warning disable CS1591
using System.Buffers.Binary;
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
  private readonly byte[] _data;
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
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 0x80)
      throw new InvalidDataException("Nilfs1: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
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
    var versions = new Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)>(StringComparer.Ordinal);
    if (TryParseWriterDirectory(versions)) {
      ParseAppendedSegments(versions);
      foreach (var (name, record) in versions) {
        if (record.Tombstone) continue;
        _entries.Add(new Nilfs1Entry {
          Name = name,
          Size = record.Data.LongLength,
          IsDirectory = false,
          Data = record.Data,
        });
      }
      return;
    }

    var meta = BuildMetadata();
    var sbBytes = _data.AsSpan(SuperblockOffset, Math.Min(SuperblockSize, _data.Length - SuperblockOffset)).ToArray();

    _entries.Add(new Nilfs1Entry { Name = "FULL.nilfs", Size = _data.Length, IsDirectory = false, Data = _data });
    _entries.Add(new Nilfs1Entry { Name = "metadata.ini", Size = meta.Length, IsDirectory = false, Data = meta });
    _entries.Add(new Nilfs1Entry { Name = "superblock.bin", Size = sbBytes.Length, IsDirectory = false, Data = sbBytes });
  }

  /// <summary>
  /// Reads the compact directory index our writer lays down at
  /// <see cref="Nilfs1Writer.SegmentStart"/>. Format: 8-byte magic, 8-byte
  /// directory-size, then a sequence of (u32 name_len, name, u64 offset, u64 size).
  /// </summary>
  private bool TryParseWriterDirectory(Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)> versions) {
    if (_data.Length < Nilfs1Writer.SegmentStart + Nilfs1Writer.WriterMagic.Length + 8) return false;
    var seg = _data.AsSpan(Nilfs1Writer.SegmentStart);
    if (!seg.Slice(0, Nilfs1Writer.WriterMagic.Length).SequenceEqual(Nilfs1Writer.WriterMagic)) return false;
    var dirSize = BinaryPrimitives.ReadInt64LittleEndian(seg.Slice(Nilfs1Writer.WriterMagic.Length));
    if (dirSize < 0 || dirSize > _data.Length) return false;
    var dirStart = Nilfs1Writer.WriterMagic.Length + 8;
    if (dirStart + dirSize > seg.Length) return false;
    var payloadStart = Nilfs1Writer.SegmentStart + dirStart + (int)dirSize;

    var cursor = dirStart;
    var dirEnd = dirStart + (int)dirSize;
    while (cursor + 4 <= dirEnd) {
      var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(seg.Slice(cursor));
      cursor += 4;
      if (nameLen <= 0 || cursor + nameLen + 16 > dirEnd) break;
      var name = Encoding.UTF8.GetString(seg.Slice(cursor, nameLen));
      cursor += nameLen;
      var off = BinaryPrimitives.ReadInt64LittleEndian(seg.Slice(cursor));
      cursor += 8;
      var size = BinaryPrimitives.ReadInt64LittleEndian(seg.Slice(cursor));
      cursor += 8;
      if (size < 0 || off < 0 || payloadStart + off + size > _data.Length) break;
      var data = _data.AsSpan(payloadStart + (int)off, (int)size).ToArray();
      // Base writer directory is the cno=1 checkpoint; appended segments can
      // supersede with a higher cno.
      versions[name] = (Cno: 1ul, Tombstone: false, Data: data);
    }
    return true;
  }

  /// <summary>
  /// Walks every appended "NILFS1SG" log segment in the image and folds its
  /// entries into <paramref name="versions"/>. Higher-cno records supersede
  /// lower-cno ones; tombstone records mark the entry deleted (the caller drops
  /// tombstones from the final listing). This is the continuous-snapshot replay
  /// semantic NILFS v1 shares with NILFS2 — the in-place modifier only ever
  /// appends these blocks at the tail, never relocating live data.
  /// </summary>
  private void ParseAppendedSegments(Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)> versions) {
    var magic = Nilfs1Writer.SegmentMagic;
    var p = Nilfs1Writer.SegmentStart;
    while (p + magic.Length + 24 <= _data.Length) {
      if (!_data.AsSpan(p, magic.Length).SequenceEqual(magic)) {
        ++p;
        continue;
      }
      var hdr = _data.AsSpan(p);
      var cno = BinaryPrimitives.ReadUInt64LittleEndian(hdr[magic.Length..]);
      var entryCount = BinaryPrimitives.ReadInt64LittleEndian(hdr[(magic.Length + 8)..]);
      var dirSize = BinaryPrimitives.ReadInt64LittleEndian(hdr[(magic.Length + 16)..]);
      var dirStart = p + magic.Length + 24;
      if (dirSize < 0 || dirStart + dirSize > _data.Length || entryCount < 0 || entryCount > _data.Length) {
        ++p;
        continue;
      }
      var payloadStart = dirStart + (int)dirSize;
      var cursor = dirStart;
      var dirEnd = dirStart + (int)dirSize;
      var consumedPayload = 0L;
      var parsedOk = true;
      for (var i = 0L; i < entryCount; ++i) {
        if (cursor + 4 > dirEnd) { parsedOk = false; break; }
        var nameLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(cursor));
        cursor += 4;
        if (nameLen <= 0 || cursor + nameLen + 1 + 16 > dirEnd) { parsedOk = false; break; }
        var name = Encoding.UTF8.GetString(_data.AsSpan(cursor, nameLen));
        cursor += nameLen;
        var tombstone = _data[cursor] != 0;
        cursor += 1;
        var off = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(cursor));
        cursor += 8;
        var size = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(cursor));
        cursor += 8;
        if (size < 0 || off < 0) { parsedOk = false; break; }
        byte[] data;
        if (tombstone || size == 0) {
          data = [];
        } else {
          if (payloadStart + off + size > _data.Length) { parsedOk = false; break; }
          data = _data.AsSpan(payloadStart + (int)off, (int)size).ToArray();
          consumedPayload = Math.Max(consumedPayload, off + size);
        }
        if (!versions.TryGetValue(name, out var prev) || cno >= prev.Cno)
          versions[name] = (Cno: cno, Tombstone: tombstone, Data: data);
      }
      if (!parsedOk) {
        ++p;
        continue;
      }
      p = payloadStart + (int)consumedPayload;
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
    return entry.Data;
  }

  public void Dispose() { }
}
