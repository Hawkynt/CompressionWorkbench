#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace FileSystem.Tux3;

/// <summary>
/// Detection / metadata-surface reader for TUX3 — Daniel Phillips's
/// successor to TUX2, a version-tree based filesystem with copy-on-write
/// metadata and atomic commit semantics. The Tux3 prototype lives in
/// the linux-tux3 tree on kernel.org and uses a superblock magic of
/// "TUX3SUPR" (8 ASCII bytes). Full B-tree traversal of itable / atable
/// is multi-week work; this reader surfaces the parsed superblock as
/// structured metadata plus the raw image.
///
/// Superblock layout (the documented prefix; little-endian; sits at
/// file offset 4096 == one 4KiB block):
///   0x00 8 bytes  Magic = "TUX3SUPR"
///   0x08 u64      birthday
///   0x10 u64      flags
///   0x18 u64      iroot     (root of itable B-tree)
///   0x20 u64      oroot     (root of otable B-tree)
///   0x28 u64      aroot     (root of atable B-tree)
///   0x30 u64      blockbits
///   0x38 u64      volblocks
///   0x40 u64      freeblocks
///   0x48 u64      nextalloc
///   0x50 u32      atomgen
///   0x54 u32      freeatom
///   ...
///
/// <para>
/// On top of the documented superblock surface, this reader also recognises
/// an optional <em>WORM file table</em> emitted by <see cref="Tux3Writer"/>:
/// a sentinel header "TUX3WORM" placed at offset <c>8192</c> (block 2,
/// immediately after the superblock block) followed by a u32 file count and
/// per-file records (u16 name length, UTF-8 name, u32 data length, raw
/// bytes). Single-version WORM images created by <see cref="Tux3Writer"/>
/// round-trip through this reader; B-tree-formatted prototype images
/// continue to surface only as <c>FULL.tux3</c> + <c>metadata.ini</c> +
/// <c>superblock.bin</c>.
/// </para>
/// </summary>
public sealed class Tux3Reader : IDisposable {
  private readonly byte[] _data;
  private readonly List<Tux3Entry> _entries = [];

  public IReadOnlyList<Tux3Entry> Entries => _entries;

  public ulong Birthday { get; private set; }
  public ulong Flags { get; private set; }
  public ulong IRoot { get; private set; }
  public ulong ORoot { get; private set; }
  public ulong ARoot { get; private set; }
  public ulong BlockBits { get; private set; }
  public ulong VolBlocks { get; private set; }
  public ulong FreeBlocks { get; private set; }
  public bool ValidSuperblock { get; private set; }
  public bool HasWormTable { get; private set; }
  public uint WormFileCount { get; private set; }

  public static readonly byte[] Magic = "TUX3SUPR"u8.ToArray();

  /// <summary>
  /// Sentinel marker for the optional WORM file table appended after the
  /// superblock at <see cref="WormTableOffset"/>.
  /// </summary>
  public static readonly byte[] WormTableMagic = "TUX3WORM"u8.ToArray();

  public const int SuperblockOffset = 4096;
  public const int WormTableOffset = 8192;

  public Tux3Reader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private void Parse() {
    if (_data.Length < SuperblockOffset + 0x60)
      throw new InvalidDataException("Tux3: image too small for superblock.");

    var sb = _data.AsSpan(SuperblockOffset);
    if (!sb.Slice(0, 8).SequenceEqual(Magic))
      throw new InvalidDataException("Tux3: missing TUX3SUPR magic at superblock offset 4096.");

    this.ValidSuperblock = true;
    this.Birthday   = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x08));
    this.Flags      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x10));
    this.IRoot      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x18));
    this.ORoot      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x20));
    this.ARoot      = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x28));
    this.BlockBits  = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x30));
    this.VolBlocks  = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x38));
    this.FreeBlocks = BinaryPrimitives.ReadUInt64LittleEndian(sb.Slice(0x40));

    var sbBytes = sb.Slice(0, Math.Min(512, _data.Length - SuperblockOffset)).ToArray();

    _entries.Add(new Tux3Entry { Name = "FULL.tux3", Size = _data.Length, Data = _data });

    // Probe for the optional WORM table. We add the placeholder for metadata
    // first so it stays at index 1, then walk WORM entries, then finalise
    // metadata once we know the walk count.
    _entries.Add(new Tux3Entry { Name = "metadata.ini", Data = [] });
    _entries.Add(new Tux3Entry { Name = "superblock.bin", Size = sbBytes.Length, Data = sbBytes });

    var walked = TryWalkWormTable();

    var meta = BuildMetadata(walked);
    _entries[1] = new Tux3Entry { Name = "metadata.ini", Size = meta.Length, Data = meta };
  }

  /// <summary>
  /// Probes for and walks the optional WORM file table at offset 8192.
  /// Returns the number of records successfully decoded, or null if no WORM
  /// table is present.
  /// </summary>
  private uint? TryWalkWormTable() {
    // Need at least sentinel (8) + u32 count.
    if (_data.Length < WormTableOffset + 12) return null;
    var tbl = _data.AsSpan(WormTableOffset);
    if (!tbl.Slice(0, 8).SequenceEqual(WormTableMagic)) return null;

    this.HasWormTable = true;
    var declared = BinaryPrimitives.ReadUInt32LittleEndian(tbl.Slice(8, 4));
    this.WormFileCount = declared;

    var pos = WormTableOffset + 12;
    var count = 0u;
    while (count < declared && pos + 2 <= _data.Length) {
      var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(pos));
      pos += 2;
      if (pos + nameLen + 4 > _data.Length) break;
      var name = Encoding.UTF8.GetString(_data, pos, nameLen);
      pos += nameLen;
      var dataLen = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(pos));
      pos += 4;
      if (dataLen > int.MaxValue || pos + dataLen > _data.Length) break;
      var data = _data.AsSpan(pos, (int)dataLen).ToArray();
      pos += (int)dataLen;

      _entries.Add(new Tux3Entry { Name = name, Size = data.Length, Data = data });
      count++;
    }
    return count;
  }

  private byte[] BuildMetadata(uint? walked) {
    var bldr = new StringBuilder();
    bldr.Append("parse_status=ok\n");
    bldr.Append("format=TUX3 (linux-tux3 prototype)\n");
    bldr.Append(CultureInfo.InvariantCulture, $"superblock_offset={SuperblockOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"birthday=0x{this.Birthday:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"flags=0x{this.Flags:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"iroot=0x{this.IRoot:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"oroot=0x{this.ORoot:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"aroot=0x{this.ARoot:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"blockbits={this.BlockBits}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"block_size={(this.BlockBits == 0 ? 0 : 1ul << (int)this.BlockBits)}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"vol_blocks={this.VolBlocks}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"free_blocks={this.FreeBlocks}\n");
    if (this.HasWormTable) {
      bldr.Append(CultureInfo.InvariantCulture, $"worm_table=present (offset={WormTableOffset})\n");
      bldr.Append(CultureInfo.InvariantCulture, $"worm_file_count={this.WormFileCount}\n");
      if (walked.HasValue)
        bldr.Append(CultureInfo.InvariantCulture, $"worm_files_walked={walked.Value}\n");
    } else {
      bldr.Append("worm_table=absent\n");
      bldr.Append("note=itable/otable/atable B-tree traversal not implemented (research read-only).\n");
    }
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(Tux3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
