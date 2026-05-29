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

  public static readonly byte[] Magic = "TUX3SUPR"u8.ToArray();
  private const int SuperblockOffset = 4096;

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

    var meta = BuildMetadata();
    var sbBytes = sb.Slice(0, Math.Min(512, _data.Length - SuperblockOffset)).ToArray();

    _entries.Add(new Tux3Entry { Name = "FULL.tux3", Size = _data.Length, Data = _data });
    _entries.Add(new Tux3Entry { Name = "metadata.ini", Size = meta.Length, Data = meta });
    _entries.Add(new Tux3Entry { Name = "superblock.bin", Size = sbBytes.Length, Data = sbBytes });
  }

  private byte[] BuildMetadata() {
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
    bldr.Append("note=itable/otable/atable B-tree traversal not implemented (research read-only).\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  public byte[] Extract(Tux3Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    return entry.Data;
  }

  public void Dispose() { }
}
