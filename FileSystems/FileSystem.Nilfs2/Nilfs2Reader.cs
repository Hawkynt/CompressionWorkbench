#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Nilfs2;

/// <summary>
/// Reads NILFS2 superblock metadata (Linux's continuous-snapshot
/// log-structured filesystem, mainline since 2.6.30). Full file
/// traversal would require walking the DAT (Disk Address Translation)
/// B-tree and replaying log segments — multi-week work. This reader
/// surfaces the parsed superblock + checkpoint anchor as a structured
/// metadata bundle plus the raw image, matching the pattern used by
/// other research/proprietary read-only FSes in this project.
///
/// Superblock layout (selected, little-endian, sits at file offset 1024):
///   0x00 u32 s_rev_level
///   0x04 u16 s_minor_rev_level
///   0x06 u16 s_magic       (must be 0x3434 = NILFS_SUPER_MAGIC)
///   0x08 u16 s_bytes
///   0x0A u16 s_flags
///   0x0C u32 s_crc_seed
///   0x10 u32 s_sum
///   0x14 u32 s_log_block_size
///   0x18 u64 s_nsegments
///   0x20 u64 s_dev_size
///   0x28 u64 s_first_data_block
///   0x30 u32 s_blocks_per_segment
///   0x34 u32 s_r_segments_percentage
///   0x38 u64 s_last_cno     (last checkpoint number)
///   0x40 u64 s_last_pseg    (last partial segment)
///   ...
/// </summary>
public sealed class Nilfs2Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<Nilfs2Entry> _entries = [];

  public IReadOnlyList<Nilfs2Entry> Entries => _entries;

  // Parsed superblock fields surfaced for diagnostics / tests
  public uint RevLevel { get; private set; }
  public ushort Magic { get; private set; }
  public uint LogBlockSize { get; private set; }
  public ulong NumSegments { get; private set; }
  public ulong DevSize { get; private set; }
  public uint BlocksPerSegment { get; private set; }
  public ulong LastCheckpoint { get; private set; }

  public bool ValidSuperblock { get; private set; }

  public const ushort SuperMagic = 0x3434;
  private const int SuperblockOffset = 1024;
  private const int SuperblockSize = 1024;

  public Nilfs2Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 0x80)
      throw new InvalidDataException("Nilfs2: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(6));
    if (magic != SuperMagic)
      throw new InvalidDataException($"Nilfs2: invalid magic 0x{magic:X4} at superblock+6 (expected 0x{SuperMagic:X4}).");

    var rev = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (rev < 2)
      throw new InvalidDataException($"Nilfs2: s_rev_level={rev} (NILFS2 requires rev>=2; rev==1 is NILFS v1, handled by Nilfs1 descriptor).");

    this.Magic = magic;
    this.ValidSuperblock = true;
    this.RevLevel         = rev;
    this.LogBlockSize     = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x14));
    this.NumSegments      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x18));
    this.DevSize          = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x20));
    this.BlocksPerSegment = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x30));
    this.LastCheckpoint   = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x38));

    // Build the always-present surface entries: full image + parsed metadata + raw superblock
    var meta = BuildMetadata();
    var sbBytes = _data.AsSpan(SuperblockOffset, Math.Min(SuperblockSize, _data.Length - SuperblockOffset)).ToArray();

    _entries.Add(new Nilfs2Entry { Name = "FULL.nilfs2", Size = _data.Length, IsDirectory = false, Data = _data });
    _entries.Add(new Nilfs2Entry { Name = "metadata.ini", Size = meta.Length,   IsDirectory = false, Data = meta });
    _entries.Add(new Nilfs2Entry { Name = "superblock.bin", Size = sbBytes.Length, IsDirectory = false, Data = sbBytes });

    // Collect every (cno, name, payload-or-tombstone) record across the
    // writer-private directory (cno = 1 by construction) and every appended
    // log segment ("NILFS2SG" blocks). Highest cno per name wins; tombstones
    // drop the entry from the listing — matches the NILFS2 spec semantic of
    // walking the segment chain and surfacing the latest checkpoint state.
    var versions = new Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)>(StringComparer.Ordinal);
    TryParseWriterDirectory(versions);
    ParseAppendedSegments(versions);

    foreach (var (name, record) in versions) {
      if (record.Tombstone) continue;
      _entries.Add(new Nilfs2Entry {
        Name = name,
        Size = record.Data.LongLength,
        IsDirectory = false,
        Data = record.Data,
      });
    }
  }

  /// <summary>
  /// Reads the writer-private compact directory at <see cref="Nilfs2Writer.SegmentStart"/>
  /// when present and folds its entries into <paramref name="versions"/> at
  /// cno=1 (the base checkpoint baked in by the writer). Format: 8-byte
  /// <see cref="Nilfs2Writer.WriterMagic"/>, 8-byte directory size, then
  /// (u32 name_len, name, u64 payload_offset, u64 size) records.
  /// </summary>
  private bool TryParseWriterDirectory(Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)> versions) {
    if (_data.Length < Nilfs2Writer.SegmentStart + Nilfs2Writer.WriterMagic.Length + 8) return false;
    var seg = _data.AsSpan(Nilfs2Writer.SegmentStart);
    if (!seg.Slice(0, Nilfs2Writer.WriterMagic.Length).SequenceEqual(Nilfs2Writer.WriterMagic)) return false;
    var dirSize = BinaryPrimitives.ReadInt64LittleEndian(seg.Slice(Nilfs2Writer.WriterMagic.Length));
    if (dirSize < 0 || dirSize > _data.Length) return false;
    var dirStart = Nilfs2Writer.WriterMagic.Length + 8;
    if (dirStart + dirSize > seg.Length) return false;
    var payloadStart = Nilfs2Writer.SegmentStart + dirStart + (int)dirSize;

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
      // Base writer directory is the cno=1 checkpoint; later appended segments
      // can supersede with higher cno.
      versions[name] = (Cno: 1ul, Tombstone: false, Data: data);
    }
    return true;
  }

  /// <summary>
  /// Walks every appended "NILFS2SG" log segment in the image and folds its
  /// entries into <paramref name="versions"/>. Higher-cno records supersede
  /// lower-cno ones; tombstone records mark the entry as deleted (the caller
  /// drops tombstones from the final listing). This is the spec-canonical
  /// continuous-snapshot replay semantic NILFS2 uses on mount.
  /// </summary>
  private void ParseAppendedSegments(Dictionary<string, (ulong Cno, bool Tombstone, byte[] Data)> versions) {
    if (_data.Length < Nilfs2Writer.SegmentStart) return;
    // The writer's private region starts at SegmentStart; appended segments
    // live past it. Find the start of the first appended segment by scanning
    // forward for the NILFS2SG magic.
    var searchStart = Nilfs2Writer.SegmentStart;
    var magic = Nilfs2Writer.SegmentMagic;
    var p = searchStart;
    while (p + magic.Length + 24 <= _data.Length) {
      if (!_data.AsSpan(p, magic.Length).SequenceEqual(magic)) {
        ++p;
        continue;
      }
      // Found a segment header. Parse it.
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
        // Higher-cno records win; tombstone is just a flag that survives the merge.
        if (!versions.TryGetValue(name, out var prev) || cno >= prev.Cno)
          versions[name] = (Cno: cno, Tombstone: tombstone, Data: data);
      }
      if (!parsedOk) {
        ++p;
        continue;
      }
      // Advance past this segment.
      p = payloadStart + (int)consumedPayload;
    }
  }

  private byte[] BuildMetadata() {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=NILFS2\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"magic=0x{this.Magic:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"rev_level={this.RevLevel}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"log_block_size={this.LogBlockSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={1u << (int)(this.LogBlockSize + 10)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"num_segments={this.NumSegments}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"dev_size={this.DevSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blocks_per_segment={this.BlocksPerSegment}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"last_checkpoint={this.LastCheckpoint}\n");
    bldr.Append("note=DAT/segment traversal not implemented (research read-only).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(Nilfs2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
