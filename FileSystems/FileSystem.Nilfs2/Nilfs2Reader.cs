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

    this.Magic = magic;
    this.ValidSuperblock = true;
    this.RevLevel         = BinaryPrimitives.ReadUInt32LittleEndian(sb);
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
