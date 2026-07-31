#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Adfs;

/// <summary>
/// Builds an Acorn ADFS <em>new-map</em> image — the E/F-style layout, as
/// opposed to the S/M/L free-space-list layout <see cref="AdfsWriter" /> emits.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Linux's <c>adfs</c> driver only mounts new-map
/// discs: it looks for a disc record either in the boot block at 0xC00 + 0x1C0
/// or at sector 0 + 4 with a single zone, and its allocation walk expects the
/// zone bitmap described below. An old-map ADFS-L image has neither, so the
/// driver cannot read one at all — which is what this writer fixes.</para>
///
/// <para><b>Layout.</b> One 1024-byte sector per block, one map zone, one map
/// bit per sector:</para>
/// <list type="bullet">
///   <item><description>sector 0 — the map zone: check byte, free-space link,
///   cross-check byte, the 60-byte disc record at +4, then the fragment bitmap
///   from bit 512 onward (one bit per sector of the disc).</description></item>
///   <item><description>the bitmap tiles the whole disc with fragments. A
///   fragment is its id in the low <c>idlen</c> bits at its first bit, zeros,
///   then a set bit at its last — so the shortest fragment is
///   <c>idlen + 1</c> bits.</description></item>
///   <item><description>fragment 3 covers the map sector, fragment 2 the root
///   directory (a 2048-byte "Hugo" directory), and one fragment per file after
///   that. Free space at the tail is a fragment too, chained from the zone's
///   free-space link.</description></item>
/// </list>
///
/// <para><b>Bounds.</b> A single zone's bitmap is one sector — 8192 bits, of
/// which 512 are the header and disc record — so a volume holds at most
/// <see cref="MaxSectors" /> sectors (7.5 MB), and every fragment costs at
/// least <c>idlen + 1</c> sectors. Multi-zone maps, share offsets for small
/// files, and F+ big directories are out of scope.</para>
///
/// <para>Cross-checked against the kernel's <c>fs/adfs</c>:
/// <c>adfs_checkdiscrecord</c>, <c>adfs_validate_dr0</c>, <c>adfs_map_layout</c>,
/// <c>lookup_zone</c>, <c>scan_free_map</c>, <c>adfs_calczonecheck</c> and
/// <c>adfs_dir_checkbyte</c>.</para>
/// </remarks>
public sealed class AdfsNewMapWriter {

  /// <summary>Sector size: the driver reads new-map discs a block at a time.</summary>
  public const int SectorSize = 1024;

  /// <summary>Bits of fragment id. Must be at least log2(sector size) + 3.</summary>
  public const int IdLen = 13;

  /// <summary>Shortest fragment the map can express, in sectors.</summary>
  public const int MinFragmentSectors = IdLen + 1;

  /// <summary>Bits in one zone: 8 per byte of the map sector.</summary>
  private const int ZoneBits = 8 * SectorSize;

  /// <summary>Bits the zone header and the disc record occupy before the bitmap.</summary>
  private const int BitmapStart = 32 + 60 * 8;   // 512

  /// <summary>Sectors one zone can describe.</summary>
  public const int MaxSectors = ZoneBits - BitmapStart;   // 7680 → 7.5 MB

  /// <summary>Size of a "Hugo" directory.</summary>
  public const int DirectorySize = 2048;

  private const int DirEntrySize = 26;
  private const int DirEntriesOffset = 5;
  private const int DirTailOffset = 2007;
  private const int MaxDirEntries = 77;
  private const int NameLength = 10;

  private const int MapFragment = 3;
  private const int RootFragment = 2;
  private const int FirstFileFragment = 4;

  private readonly List<(string Name, byte[] Data)> _files = [];

  /// <summary>Disc title, kept in the root directory's tail (19 bytes).</summary>
  public string DiscTitle { get; set; } = "CWB-ADFS";

  /// <summary>Disc identifier, stored in the disc record.</summary>
  public ushort DiscId { get; set; } = 0x1234;

  /// <summary>Adds a file to the root directory.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentException.ThrowIfNullOrEmpty(name);
    ArgumentNullException.ThrowIfNull(data);
    if (this._files.Count >= MaxDirEntries - 1)
      throw new InvalidOperationException(
        $"An ADFS directory holds {MaxDirEntries - 1} entries; this one is full.");
    this._files.Add((name, data));
  }

  /// <summary>Builds the image.</summary>
  public byte[] Build() {
    // ── plan the fragments ────────────────────────────────────────────────
    // Sector 0 is the map. Everything on the disc belongs to some fragment,
    // because the driver walks the bitmap fragment by fragment and a gap would
    // desynchronise that walk.
    var plan = new List<(int Fragment, int FirstSector, int Sectors)>();
    var cursor = 0;

    var mapSectors = MinFragmentSectors;
    plan.Add((MapFragment, cursor, mapSectors));
    cursor += mapSectors;

    var rootSectors = Math.Max(MinFragmentSectors, DirectorySize / SectorSize);
    var rootFirstSector = cursor;
    plan.Add((RootFragment, cursor, rootSectors));
    cursor += rootSectors;

    var fileStarts = new int[this._files.Count];
    for (var i = 0; i < this._files.Count; ++i) {
      var need = (int)((this._files[i].Data.LongLength + SectorSize - 1) / SectorSize);
      var sectors = Math.Max(MinFragmentSectors, need);
      fileStarts[i] = cursor;
      plan.Add((FirstFileFragment + i, cursor, sectors));
      cursor += sectors;
    }

    // Leave room for a free fragment so the disc never ends mid-fragment.
    var totalSectors = cursor + MinFragmentSectors;
    if (totalSectors > MaxSectors)
      throw new InvalidOperationException(
        $"ADFS new-map: the volume needs {totalSectors:N0} sectors, past the {MaxSectors:N0} " +
        $"a single map zone describes ({(long)MaxSectors * SectorSize:N0} bytes).");

    var image = new byte[(long)totalSectors * SectorSize];

    // ── the map zone ──────────────────────────────────────────────────────
    var zone = image.AsSpan(0, SectorSize);
    WriteDiscRecord(zone[4..], totalSectors, this.DiscId, this.DiscTitle);

    foreach (var (fragment, firstSector, sectors) in plan)
      WriteFragment(zone, BitmapStart + firstSector, sectors, (uint)fragment);

    // The tail is one free fragment, and the zone's free-space link points at it.
    var freeStart = cursor;
    var freeSectors = totalSectors - freeStart;
    if (freeSectors >= MinFragmentSectors) {
      WriteFragment(zone, BitmapStart + freeStart, freeSectors, 0);   // id 0 = no next free
      var link = BitmapStart + freeStart - 8;
      BinaryPrimitives.WriteUInt16LittleEndian(zone[1..], (ushort)link);
    }

    zone[3] = 0xFF;                       // cross-check: one zone, so it is the whole XOR
    zone[0] = ZoneCheck(zone);

    // ── the root directory ────────────────────────────────────────────────
    var root = image.AsSpan(rootFirstSector * SectorSize, DirectorySize);
    WriteDirectory(root, this._files, this.DiscTitle);

    // ── file data ─────────────────────────────────────────────────────────
    for (var i = 0; i < this._files.Count; ++i) {
      var data = this._files[i].Data;
      if (data.Length == 0) continue;
      data.CopyTo(image.AsSpan(fileStarts[i] * SectorSize));
    }

    return image;
  }

  /// <summary>Writes the image to a stream.</summary>
  public void WriteTo(Stream output) {
    ArgumentNullException.ThrowIfNull(output);
    var image = this.Build();
    output.Write(image, 0, image.Length);
  }

  // ── disc record ───────────────────────────────────────────────────────────

  private static void WriteDiscRecord(Span<byte> dr, int totalSectors, ushort discId, string title) {
    dr[..60].Clear();
    dr[0] = 10;                       // log2secsize — 1024-byte sectors
    dr[1] = 16;                       // secspertrack (geometry, not validated)
    dr[2] = 2;                        // heads
    dr[3] = 2;                        // density
    dr[4] = IdLen;                    // idlen
    dr[5] = 10;                       // log2bpmb — one map bit per sector
    dr[6] = 0;                        // skew
    dr[7] = 0;                        // bootoption
    dr[8] = 0;                        // lowsector
    dr[9] = 1;                        // nzones — a single zone
    BinaryPrimitives.WriteUInt16LittleEndian(dr[10..], 0);                       // zone_spare
    BinaryPrimitives.WriteUInt32LittleEndian(dr[12..], RootFragment << 8);       // root indaddr
    BinaryPrimitives.WriteUInt32LittleEndian(dr[16..], (uint)((long)totalSectors * SectorSize));
    BinaryPrimitives.WriteUInt16LittleEndian(dr[20..], discId);                  // disc_id
    WriteFixedAscii(dr.Slice(22, 10), title);                                    // disc_name
    BinaryPrimitives.WriteUInt32LittleEndian(dr[32..], 0);                       // disc_type
    BinaryPrimitives.WriteUInt32LittleEndian(dr[36..], 0);                       // disc_size_high
    dr[40] = 0;                       // log2sharesize — no shared fragments
    dr[41] = 0;                       // big_flag
    dr[42] = 0;                       // nzones_high
    dr[43] = 0;                       // reserved
    BinaryPrimitives.WriteUInt32LittleEndian(dr[44..], 0);                       // format_version
    BinaryPrimitives.WriteUInt32LittleEndian(dr[48..], DirectorySize);           // root_size
    // 52..59 must stay zero: the driver rejects a record with anything there.
  }

  // ── fragment bitmap ───────────────────────────────────────────────────────

  /// <summary>
  /// Writes one fragment: its id in the low <see cref="IdLen" /> bits at
  /// <paramref name="startBit" />, then the terminating set bit at its last bit.
  /// </summary>
  private static void WriteFragment(Span<byte> zone, int startBit, int lengthBits, uint id) {
    if (lengthBits < IdLen + 1)
      throw new InvalidOperationException(
        $"ADFS new-map: a fragment spans at least {IdLen + 1} sectors; got {lengthBits}.");

    for (var bit = 0; bit < IdLen; ++bit)
      if (((id >> bit) & 1) != 0)
        SetBit(zone, startBit + bit);
    SetBit(zone, startBit + lengthBits - 1);
  }

  private static void SetBit(Span<byte> data, int bit) => data[bit >> 3] |= (byte)(1 << (bit & 7));

  /// <summary>
  /// The zone check byte, as <c>adfs_calczonecheck</c> computes it: four
  /// interleaved carrying sums over the sector, folded together.
  /// </summary>
  private static byte ZoneCheck(ReadOnlySpan<byte> map) {
    uint v0 = 0, v1 = 0, v2 = 0, v3 = 0;
    for (var i = map.Length - 4; i != 0; i -= 4) {
      v0 += (uint)map[i] + (v3 >> 8);
      v3 &= 0xff;
      v1 += (uint)map[i + 1] + (v0 >> 8);
      v0 &= 0xff;
      v2 += (uint)map[i + 2] + (v1 >> 8);
      v1 &= 0xff;
      v3 += (uint)map[i + 3] + (v2 >> 8);
      v2 &= 0xff;
    }
    v0 += v3 >> 8;
    v1 += (uint)map[1] + (v0 >> 8);
    v2 += (uint)map[2] + (v1 >> 8);
    v3 += (uint)map[3] + (v2 >> 8);
    return (byte)(v0 ^ v1 ^ v2 ^ v3);
  }

  // ── directory ─────────────────────────────────────────────────────────────

  private static void WriteDirectory(Span<byte> dir, List<(string Name, byte[] Data)> files, string title) {
    dir[..DirectorySize].Clear();

    const byte masSeq = 0;
    dir[0] = masSeq;
    "Hugo"u8.CopyTo(dir[1..]);

    for (var i = 0; i < files.Count; ++i) {
      var entry = dir.Slice(DirEntriesOffset + i * DirEntrySize, DirEntrySize);
      WriteFixedAscii(entry[..NameLength], files[i].Name.ToUpperInvariant(), padWith: (byte)0x0D);
      // Load and exec addresses without the 0xFFF filetype prefix, so the
      // driver does not append a ",xyz" suffix to the name.
      BinaryPrimitives.WriteUInt32LittleEndian(entry[10..], 0);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[14..], 0);
      BinaryPrimitives.WriteUInt32LittleEndian(entry[18..], (uint)files[i].Data.Length);
      var indaddr = (uint)((FirstFileFragment + i) << 8);
      entry[22] = (byte)indaddr;
      entry[23] = (byte)(indaddr >> 8);
      entry[24] = (byte)(indaddr >> 16);
      entry[25] = 0x33;   // owner read/write, public read, not a directory
    }

    // A zero first name byte ends the entry list.
    var tail = dir[DirTailOffset..];
    tail[0] = 0;                                   // dirlastmask
    tail[1] = 0; tail[2] = 0;                      // reserved — the driver checks these
    // dirparent is an indirect disc address, not a bare fragment id: the root
    // is its own parent, so that is the root's own indaddr.
    var rootIndaddr = (uint)(RootFragment << 8);
    tail[3] = (byte)rootIndaddr;
    tail[4] = (byte)(rootIndaddr >> 8);
    tail[5] = (byte)(rootIndaddr >> 16);
    WriteFixedAscii(tail.Slice(6, 19), title, padWith: (byte)0x0D);
    WriteFixedAscii(tail.Slice(25, 10), "$", padWith: (byte)0x0D);
    tail[35] = masSeq;                             // endmasseq — must match startmasseq
    "Hugo"u8.CopyTo(tail[36..]);                   // endname
    tail[40] = DirCheckByte(dir);
  }

  /// <summary>
  /// The directory check byte, as <c>adfs_dir_checkbyte</c> computes it: words
  /// from the start through the last whole word of the terminating entry, then
  /// the odd bytes, then the 36 bytes of the tail from 2008.
  /// </summary>
  private static byte DirCheckByte(ReadOnlySpan<byte> dir) {
    uint dircheck = 0;
    var last = DirEntriesOffset - DirEntrySize;
    var i = 0;
    do {
      last += DirEntrySize;
      do {
        dircheck = BinaryPrimitives.ReadUInt32LittleEndian(dir[i..]) ^ Ror13(dircheck);
        i += 4;
      } while (i < (last & ~3));
    } while (dir[last] != 0);

    for (var p = i; p < last; ++p)
      dircheck = dir[p] ^ Ror13(dircheck);

    for (var p = 2008; p < 2008 + 36; p += 4)
      dircheck = BinaryPrimitives.ReadUInt32LittleEndian(dir[p..]) ^ Ror13(dircheck);

    return (byte)((dircheck ^ (dircheck >> 8) ^ (dircheck >> 16) ^ (dircheck >> 24)) & 0xff);
  }

  private static uint Ror13(uint v) => (v >> 13) | (v << 19);

  private static void WriteFixedAscii(Span<byte> destination, string value, byte padWith = 0) {
    destination.Fill(padWith);
    var bytes = Encoding.ASCII.GetBytes(value);
    bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length)).CopyTo(destination);
  }
}
