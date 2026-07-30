#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
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
  private readonly ImageAccessor _img;
  private readonly long _len;
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
  public ushort SBytes { get; private set; }
  public uint CrcSeed { get; private set; }
  public uint StoredSum { get; private set; }
  public string Uuid { get; private set; } = "";
  public string VolumeLabel { get; private set; } = "";

  /// <summary>
  /// True when the parsed superblock's stored <c>s_sum</c> matches a freshly
  /// computed Linux <c>crc32_le</c> over its first <c>s_bytes</c> bytes. mkfs and
  /// our own writer both produce CRC-valid superblocks; a false here flags a
  /// corrupt or hand-edited image.
  /// </summary>
  public bool ChecksumValid { get; private set; }

  /// <summary>Which superblock copy was chosen as authoritative.</summary>
  public string SuperblockSource { get; private set; } = "";

  public bool ValidSuperblock { get; private set; }

  public const ushort SuperMagic = 0x3434;
  private const int SuperblockOffset = 1024;
  private const int SuperblockSize = 1024;

  public Nilfs2Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    // Metadata is a handful of blocks however many gigabytes of payload follow
    // it, so the image is read through rather than copied in.
    _img = new ImageAccessor(stream);
    _len = _img.Length;
    Parse();
  }

  /// <summary>Total size of the backing image in bytes.</summary>
  public long Length => this._len;

  private readonly List<(long Offset, long Length)> _metadata = [];

  /// <summary>
  /// Regions holding structure rather than file bytes: the superblocks, the
  /// kernel log, the writer-private directory and the header+directory of every
  /// appended segment. A wipe must leave these alone; everything they and the
  /// live payloads do not cover is dead space.
  /// </summary>
  public IReadOnlyList<(long Offset, long Length)> MetadataRegions => this._metadata;

  private readonly List<(long Offset, long Length, string Name)> _logFiles = [];
  private long _payloadBase = -1;

  /// <summary>
  /// The data blocks of files embedded in the kernel checkpoint, with the name
  /// each belongs to. A file small enough for a direct block map has a copy here
  /// as well as in the payload region, so a wipe has to know which of those
  /// blocks still belong to a live file.
  /// </summary>
  public IReadOnlyList<(long Offset, long Length, string Name)> LogFileRegions => this._logFiles;

  private void Parse() {
    if (_len < SuperblockOffset + 0x80)
      throw new InvalidDataException("Nilfs2: image too small for superblock.");

    // NILFS2 carries two superblocks: primary at offset 1024 and a backup one
    // block (4096 B) before EOF. Read both, validate magic + checksum, and pick
    // the authoritative copy (the valid one with the higher s_last_cno — i.e.
    // the most recently committed). This mirrors the kernel's mount-time choice
    // and is what makes our reader correct against real mkfs.nilfs2 images, not
    // just our own writer's output (reverse gate).
    var primary = TryReadSuperblock(SuperblockOffset);
    var secondaryOffset = _len - Nilfs2Superblock.SecondaryBackOffset;
    var secondary = secondaryOffset >= SuperblockOffset + Nilfs2Superblock.Size
      ? TryReadSuperblock(secondaryOffset)
      : (ParsedSb?)null;

    var chosen = ChooseSuperblock(primary, secondary, out var source)
      ?? throw new InvalidDataException(
        "Nilfs2: no valid superblock (magic 0x3434 / s_rev_level>=2) found at offset 1024 or dev_size-4096.");

    this.Magic = chosen.Magic;
    this.ValidSuperblock = true;
    this.RevLevel         = chosen.RevLevel;
    this.SBytes           = chosen.SBytes;
    this.CrcSeed          = chosen.CrcSeed;
    this.StoredSum        = chosen.StoredSum;
    this.ChecksumValid    = chosen.ChecksumValid;
    this.LogBlockSize     = chosen.LogBlockSize;
    this.NumSegments      = chosen.NumSegments;
    this.DevSize          = chosen.DevSize;
    this.BlocksPerSegment = chosen.BlocksPerSegment;
    this.LastCheckpoint   = chosen.LastCno;
    this.Uuid             = chosen.Uuid;
    this.VolumeLabel      = chosen.VolumeLabel;
    this.SuperblockSource = source;

    // Build the always-present surface entries: full image + parsed metadata + raw superblock
    var meta = BuildMetadata();
    var sbBytes = _img.Read(SuperblockOffset, (int)Math.Min(SuperblockSize, _len - SuperblockOffset));

    _entries.Add(new Nilfs2Entry { Name = "FULL.nilfs2", Size = _len, IsDirectory = false, Offset = 0 });
    _entries.Add(new Nilfs2Entry { Name = "metadata.ini", Size = meta.Length,   IsDirectory = false, Data = meta });
    _entries.Add(new Nilfs2Entry { Name = "superblock.bin", Size = sbBytes.Length, IsDirectory = false, Data = sbBytes });

    // Collect every (cno, name, payload-or-tombstone) record across the
    // writer-private directory (cno = 1 by construction) and every appended
    // log segment ("NILFS2SG" blocks). Highest cno per name wins; tombstones
    // drop the entry from the listing — matches the NILFS2 spec semantic of
    // walking the segment chain and surfacing the latest checkpoint state.
    var versions = new Dictionary<string, Record>(StringComparer.Ordinal);
    var payloadEnd = TryParseWriterDirectory(versions);
    if (payloadEnd >= 0) ParseAppendedSegments(versions, payloadEnd);

    // The kernel checkpoint carries its own copy of every embedded file, so the
    // log is mapped block by block rather than claimed wholesale. If it cannot
    // be walked, the whole prefix is claimed instead — an unreadable log must
    // not be mistaken for dead space.
    if (!MapKernelLog(chosen.LastPseg, 1 << (int)(chosen.LogBlockSize + 10)) && _payloadBase > 0)
      _metadata.Add((0, Math.Min(_payloadBase, _len)));

    // The tail superblock is structure too, wherever the volume happens to end.
    var tailSb = _len - Nilfs2Superblock.SecondaryBackOffset;
    if (tailSb > 0)
      _metadata.Add((tailSb, Math.Min(Nilfs2Superblock.Size, _len - tailSb)));

    foreach (var (name, record) in versions) {
      if (record.Tombstone) continue;
      _entries.Add(new Nilfs2Entry {
        Name = name,
        Size = record.Size,
        IsDirectory = false,
        Offset = record.Offset,
      });
    }
  }

  /// <summary>One version of a name: where its bytes are, and at which checkpoint.</summary>
  private readonly record struct Record(ulong Cno, bool Tombstone, long Offset, long Size);

  /// <summary>
  /// Reads the writer-private compact directory at <see cref="Nilfs2Writer.SegmentStart"/>
  /// when present and folds its entries into <paramref name="versions"/> at
  /// cno=1 (the base checkpoint baked in by the writer). Format: 8-byte
  /// <see cref="Nilfs2Writer.WriterMagic"/>, i64 directory size, i64 payload base,
  /// i64 payload length, then (u32 name_len, name, u64 payload_offset, u64 size)
  /// records. Returns the end of the payload region — where appended segments
  /// start — or -1 when the image carries no writer directory.
  /// </summary>
  private long TryParseWriterDirectory(Dictionary<string, Record> versions) {
    if (_len < Nilfs2Writer.SegmentStart + Nilfs2Writer.PrivateHeaderBytes) return -1;
    var header = _img.Read(Nilfs2Writer.SegmentStart, Nilfs2Writer.PrivateHeaderBytes);
    if (!header.AsSpan(0, Nilfs2Writer.WriterMagic.Length).SequenceEqual(Nilfs2Writer.WriterMagic)) return -1;

    var dirSize = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
    var payloadBase = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
    var payloadBytes = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
    if (dirSize < 0 || dirSize > int.MaxValue) return -1;
    if (payloadBase < 0 || payloadBytes < 0 || payloadBase + payloadBytes > _len) return -1;

    var dirStart = (long)Nilfs2Writer.SegmentStart + Nilfs2Writer.PrivateHeaderBytes;
    if (dirStart + dirSize > _len) return -1;
    var dir = _img.Read(dirStart, (int)dirSize);

    // Boot area, superblock and the private directory are structure. The log
    // between them and the payload is mapped separately, block by block.
    this._payloadBase = payloadBase;
    this._metadata.Add((0, Math.Min(dirStart + dirSize, _len)));

    var cursor = 0;
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
      if (size < 0 || off < 0 || payloadBase + off + size > _len) break;
      // Base writer directory is the cno=1 checkpoint; later appended segments
      // can supersede with higher cno.
      versions[name] = new Record(Cno: 1ul, Tombstone: false, Offset: payloadBase + off, Size: size);
    }
    return payloadBase + payloadBytes;
  }

  /// <summary>
  /// Walks every appended "NILFS2SG" log segment past <paramref name="from"/> and
  /// folds its entries into <paramref name="versions"/>. Higher-cno records
  /// supersede lower-cno ones; tombstone records mark the entry as deleted (the
  /// caller drops tombstones from the final listing). This is the spec-canonical
  /// continuous-snapshot replay semantic NILFS2 uses on mount.
  /// </summary>
  private void ParseAppendedSegments(Dictionary<string, Record> versions, long from) {
    var magic = Nilfs2Writer.SegmentMagic;
    var p = Math.Max(0, from);
    while (p + magic.Length + 24 <= _len) {
      if (!_img.Read(p, magic.Length).AsSpan().SequenceEqual(magic)) {
        ++p;
        continue;
      }
      // Found a segment header. Parse it.
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
      // Higher-cno records win; tombstone is just a flag that survives the merge.
      foreach (var (name, record) in staged)
        if (!versions.TryGetValue(name, out var prev) || record.Cno >= prev.Cno)
          versions[name] = record;
      // This segment's header and directory are structure; its payload bytes
      // are claimed by whichever records are still live.
      this._metadata.Add((p, payloadStart - p));
      // Advance past this segment.
      p = payloadStart + consumedPayload;
    }
  }

  /// <summary>Parsed view of one on-disk superblock copy.</summary>
  private readonly record struct ParsedSb(
    ushort Magic, uint RevLevel, ushort SBytes, uint CrcSeed, uint StoredSum,
    bool ChecksumValid, uint LogBlockSize, ulong NumSegments, ulong DevSize,
    uint BlocksPerSegment, ulong LastCno, ulong LastPseg, ushort State, string Uuid, string VolumeLabel);

  /// <summary>
  /// Decodes the superblock at <paramref name="offset"/>, validating magic and
  /// s_rev_level. Returns <c>null</c> when the copy is absent / not a NILFS2
  /// superblock; a present-but-checksum-invalid copy is still returned (with
  /// <see cref="ParsedSb.ChecksumValid"/> = false) so the caller can prefer the
  /// other copy or surface the integrity flag.
  /// </summary>
  private ParsedSb? TryReadSuperblock(long offset) {
    if (offset < 0 || offset + 0x80 > _len) return null;
    var sb = _img.Read(offset, (int)Math.Min(Nilfs2Superblock.Size, _len - offset)).AsSpan();
    var magic = BinaryPrimitives.ReadUInt16LittleEndian(sb[6..]);
    if (magic != SuperMagic) return null;
    var rev = BinaryPrimitives.ReadUInt32LittleEndian(sb);
    if (rev < 2) return null; // rev==1 is NILFS v1, handled by the Nilfs1 descriptor.

    var sbytes = BinaryPrimitives.ReadUInt16LittleEndian(sb[8..]);
    var crcSeed = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x0C..]);
    var stored = BinaryPrimitives.ReadUInt32LittleEndian(sb[0x10..]);
    var checksumValid = sb.Length >= Nilfs2Superblock.SBytes && Nilfs2Superblock.VerifyChecksum(sb);

    var uuid = "";
    if (sb.Length >= 0xA8)
      uuid = Convert.ToHexString(sb.Slice(0x98, 16)).ToLowerInvariant();
    var label = "";
    if (sb.Length >= 0xA8 + 80) {
      var raw = sb.Slice(0xA8, 80);
      var nul = raw.IndexOf((byte)0);
      label = Encoding.ASCII.GetString(nul < 0 ? raw : raw[..nul]);
    }

    return new ParsedSb(
      magic, rev, sbytes, crcSeed, stored, checksumValid,
      BinaryPrimitives.ReadUInt32LittleEndian(sb[0x14..]),
      BinaryPrimitives.ReadUInt64LittleEndian(sb[0x18..]),
      BinaryPrimitives.ReadUInt64LittleEndian(sb[0x20..]),
      BinaryPrimitives.ReadUInt32LittleEndian(sb[0x30..]),
      BinaryPrimitives.ReadUInt64LittleEndian(sb[0x38..]),
      sb.Length >= 0x48 ? BinaryPrimitives.ReadUInt64LittleEndian(sb[0x40..]) : 0ul,
      sb.Length >= 0x76 ? BinaryPrimitives.ReadUInt16LittleEndian(sb[0x74..]) : (ushort)0,
      uuid, label);
  }

  /// <summary>
  /// Picks the authoritative superblock: prefer a checksum-valid copy; among
  /// equally-valid copies prefer the higher s_last_cno (most recent commit);
  /// fall back to any present copy if neither checksum matches.
  /// </summary>
  private static ParsedSb? ChooseSuperblock(ParsedSb? primary, ParsedSb? secondary, out string source) {
    source = "";
    if (primary is null && secondary is null) return null;
    if (primary is null) { source = "secondary"; return secondary; }
    if (secondary is null) { source = "primary"; return primary; }

    var p = primary.Value;
    var s = secondary.Value;
    // Valid copy wins over invalid.
    if (p.ChecksumValid != s.ChecksumValid) {
      if (p.ChecksumValid) { source = "primary"; return p; }
      source = "secondary"; return s;
    }
    // Both same validity: newer checkpoint wins.
    if (s.LastCno > p.LastCno) { source = "secondary"; return s; }
    source = "primary";
    return p;
  }

  /// <summary>
  /// Walks the committed partial segment: its summary lists one finfo per inode
  /// in physical block order, so the block run behind each embedded user file
  /// can be recovered — and with it, which log blocks are file data rather than
  /// structure. The root directory block supplies the inode-to-name mapping.
  /// </summary>
  private bool MapKernelLog(ulong lastPseg, int blockSize) {
    if (lastPseg == 0 || blockSize <= 0) return false;
    var psegStart = (long)lastPseg;
    var summaryOffset = psegStart * blockSize;
    if (summaryOffset < 0 || summaryOffset + blockSize > _len) return false;

    var summary = _img.Read(summaryOffset, blockSize);
    const uint SegsumMagic = 0x1eaffa11;
    if (BinaryPrimitives.ReadUInt32LittleEndian(summary.AsSpan(8)) != SegsumMagic) return false;

    var headerBytes = BinaryPrimitives.ReadUInt16LittleEndian(summary.AsSpan(12));
    var nfinfo = BinaryPrimitives.ReadUInt32LittleEndian(summary.AsSpan(44));
    var nblocks = BinaryPrimitives.ReadUInt32LittleEndian(summary.AsSpan(40));
    if (headerBytes < 16 || nblocks == 0) return false;

    // Block 0 of the segment is the summary itself; payload blocks follow in the
    // order the finfos are listed.
    var cursor = psegStart + 1;
    var runs = new List<(ulong Ino, long FirstBlock, long Blocks)>();
    var pos = (int)headerBytes;
    for (var i = 0u; i < nfinfo; ++i) {
      if (pos + 24 > summary.Length) return false;
      var ino = BinaryPrimitives.ReadUInt64LittleEndian(summary.AsSpan(pos));
      var fiNblocks = BinaryPrimitives.ReadUInt32LittleEndian(summary.AsSpan(pos + 16));
      var fiNdatablk = BinaryPrimitives.ReadUInt32LittleEndian(summary.AsSpan(pos + 20));
      pos += 24 + (int)fiNdatablk * 16;
      if (fiNblocks == 0) continue;
      runs.Add((ino, cursor, fiNblocks));
      cursor += fiNblocks;
    }
    if (runs.Count == 0) return false;

    // Everything up to the first payload block, and everything from the end of
    // the last one, is structure.
    var firstUser = runs.FindIndex(r => r.Ino >= UserInodeBase);
    var logEnd = (psegStart + nblocks) * blockSize;
    if (firstUser < 0) {
      _metadata.Add((0, Math.Min(logEnd, _len)));
      return true;
    }

    var names = ReadRootDirectory(runs[0].FirstBlock, blockSize);
    var userStart = runs[firstUser].FirstBlock * blockSize;
    var lastUser = runs.FindLastIndex(r => r.Ino >= UserInodeBase);
    var userEnd = (runs[lastUser].FirstBlock + runs[lastUser].Blocks) * blockSize;

    _metadata.Add((0, Math.Min(userStart, _len)));
    if (userEnd < logEnd)
      _metadata.Add((userEnd, Math.Min(logEnd, _len) - userEnd));

    foreach (var run in runs) {
      if (run.Ino < UserInodeBase) continue;
      var offset = run.FirstBlock * blockSize;
      var length = Math.Min(run.Blocks * blockSize, _len - offset);
      if (length <= 0) continue;
      var name = names.TryGetValue(run.Ino, out var n) ? n : "";
      _logFiles.Add((offset, length, name));
    }
    return true;
  }

  /// <summary>First inode number the writer hands to user files (NILFS_USER_INO).</summary>
  private const ulong UserInodeBase = 11;

  /// <summary>Reads the flat root directory block into an inode-to-name map.</summary>
  private Dictionary<ulong, string> ReadRootDirectory(long block, int blockSize) {
    var result = new Dictionary<ulong, string>();
    var offset = block * blockSize;
    if (offset < 0 || offset + blockSize > _len) return result;
    var dir = _img.Read(offset, blockSize);

    var pos = 0;
    while (pos + 12 <= dir.Length) {
      var ino = BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(pos));
      var recLen = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(pos + 8));
      var nameLen = dir[pos + 10];
      if (recLen < 12 || pos + recLen > dir.Length) break;
      if (ino != 0 && nameLen > 0 && pos + 12 + nameLen <= dir.Length) {
        var name = Encoding.UTF8.GetString(dir.AsSpan(pos + 12, nameLen));
        if (name is not ("." or ".."))
          result[ino] = name;
      }
      pos += recLen;
    }
    return result;
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
    bldr.Append(CultureInfo.InvariantCulture, $"s_bytes={this.SBytes}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"crc_seed=0x{this.CrcSeed:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"s_sum=0x{this.StoredSum:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"checksum_valid={(this.ChecksumValid ? "true" : "false")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_source={this.SuperblockSource}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"uuid={this.Uuid}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_label={this.VolumeLabel}\n");
    bldr.Append("note=DAT/segment B-tree traversal not implemented (file listing via writer-private directory only).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(Nilfs2Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.Offset < 0) return entry.Data;
    if (entry.Size > Array.MaxLength)
      throw new IOException(
        $"Nilfs2: '{entry.Name}' is {entry.Size:N0} bytes, past the array limit; use ExtractTo.");
    return _img.Read(entry.Offset, (int)entry.Size);
  }

  /// <summary>Writes <paramref name="entry" />'s bytes into <paramref name="destination" />.</summary>
  public long ExtractTo(Nilfs2Entry entry, Stream destination) {
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
