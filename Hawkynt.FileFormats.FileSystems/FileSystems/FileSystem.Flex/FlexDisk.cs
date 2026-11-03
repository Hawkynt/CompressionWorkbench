using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Flex;

/// <summary>One directory entry of a FLEX disk.</summary>
/// <param name="Name">File name (up to 8 characters).</param>
/// <param name="Extension">Extension (up to 3 characters).</param>
/// <param name="Attributes">Protection bits: 0x80 write, 0x40 delete, 0x20 read, 0x10 catalog protect.</param>
/// <param name="StartTrack">Track of the first sector.</param>
/// <param name="StartSector">Sector of the first sector.</param>
/// <param name="EndTrack">Track of the last sector.</param>
/// <param name="EndSector">Sector of the last sector.</param>
/// <param name="Sectors">Sectors in the file.</param>
/// <param name="IsRandom">File Sector Map indicator: a random file whose first two sectors are its sector map.</param>
/// <param name="Month">Creation month.</param>
/// <param name="Day">Creation day.</param>
/// <param name="Year">Creation year, tens and units only.</param>
/// <param name="EntryOffset">Byte offset of the entry in the image.</param>
public sealed record FlexEntry(string Name, string Extension, byte Attributes, byte StartTrack, byte StartSector,
  byte EndTrack, byte EndSector, ushort Sectors, bool IsRandom, byte Month, byte Day, byte Year, long EntryOffset) {
  /// <summary>NAME.EXT as FLEX shows it.</summary>
  public string FullName => this.Extension.Length == 0 ? this.Name : this.Name + "." + this.Extension;

  /// <summary>Bytes the file holds: every sector's 252 data bytes.</summary>
  public long Size => (long)this.Sectors * FlexDisk.DataBytes;

  /// <summary>Creation date, when it is a valid one (years 00-74 read as 20xx, 75-99 as 19xx).</summary>
  public DateTime? Created {
    get {
      if (this.Month is < 1 or > 12 || this.Day is < 1 or > 31 || this.Year > 99) return null;
      var year = this.Year < 75 ? 2000 + this.Year : 1900 + this.Year;
      return this.Day <= DateTime.DaysInMonth(year, this.Month) ? new DateTime(year, this.Month, this.Day, 0, 0, 0, DateTimeKind.Utc) : null;
    }
  }
}

/// <summary>
/// The FLEX (Technical Systems Consultants, 6800/6809) disk layout over a raw <c>.dsk</c>
/// image: 256-byte sectors, every track holding the same number of sector slots, sector
/// numbers from 1. Track 0 holds the boot sectors (1, 2), the System Information Record
/// (3), a reserved sector (4) and the directory chain from sector 5; all other sectors are
/// file chains or the free chain. See <c>docs/FLEX-ON-DISK.md</c>.
/// </summary>
public sealed class FlexDisk {
  /// <summary>Bytes per sector.</summary>
  public const int SectorSize = 256;

  /// <summary>Data bytes per sector after the link (2) and record number (2).</summary>
  public const int DataBytes = 252;

  /// <summary>Directory entries per directory sector, after its 16-byte header.</summary>
  public const int EntriesPerSector = 10;

  /// <summary>Bytes per directory entry.</summary>
  public const int EntrySize = 24;

  /// <summary>Byte offset of the System Information Record (track 0, sector 3).</summary>
  public const int SirOffset = 2 * SectorSize;

  private readonly Stream _stream;

  /// <summary>Sectors per track (SIR "highest sector number").</summary>
  public int SectorsPerTrack { get; }

  /// <summary>Tracks (SIR "highest track number" + 1).</summary>
  public int Tracks { get; }

  /// <summary>Volume label (11 characters).</summary>
  public string Label { get; }

  /// <summary>Volume number.</summary>
  public ushort VolumeNumber { get; }

  /// <summary>Head of the free chain.</summary>
  public (byte Track, byte Sector) FirstFree { get; }

  /// <summary>Tail of the free chain.</summary>
  public (byte Track, byte Sector) LastFree { get; }

  /// <summary>Sectors in the free chain, as the SIR records it.</summary>
  public ushort FreeSectors { get; }

  /// <summary>Every live directory entry, in directory order.</summary>
  public IReadOnlyList<FlexEntry> Entries { get; }

  /// <summary>Directory sectors in chain order.</summary>
  public IReadOnlyList<(byte Track, byte Sector)> DirectorySectors { get; }

  /// <summary>Opens a FLEX image; throws <see cref="InvalidDataException"/> when it is not one.</summary>
  public FlexDisk(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    if (stream.Length < 5 * SectorSize) throw new InvalidDataException("Image too short for a FLEX disk (needs track 0 sectors 1-5).");
    var sir = this.ReadAt(SirOffset, SectorSize);
    if (sir.AsSpan(0, 16).IndexOfAnyExcept((byte)0) >= 0)
      throw new InvalidDataException("Not a FLEX disk: the System Information Record's first 16 bytes are not zero.");
    this.Tracks = sir[0x26] + 1;
    this.SectorsPerTrack = sir[0x27];
    if (this.SectorsPerTrack < 5 || this.Tracks < 2)
      throw new InvalidDataException($"Not a FLEX disk: geometry {this.Tracks} tracks × {this.SectorsPerTrack} sectors from the SIR.");
    this.Label = Encoding.ASCII.GetString(sir, 0x10, 11).TrimEnd('\0', ' ');
    this.VolumeNumber = BinaryPrimitives.ReadUInt16BigEndian(sir.AsSpan(0x1B));
    this.FirstFree = (sir[0x1D], sir[0x1E]);
    this.LastFree = (sir[0x1F], sir[0x20]);
    this.FreeSectors = BinaryPrimitives.ReadUInt16BigEndian(sir.AsSpan(0x21));

    var dirs = new List<(byte, byte)>();
    var entries = new List<FlexEntry>();
    foreach (var (t, s, sector) in this.Chain(0, 5, limit: this.Tracks * this.SectorsPerTrack)) {
      dirs.Add((t, s));
      var at = this.Offset(t, s);
      for (var i = 0; i < EntriesPerSector; ++i) {
        var e = sector.AsSpan(16 + i * EntrySize, EntrySize);
        if (e[0] == 0 || (e[0] & 0x80) != 0) continue;   // never used / deleted
        entries.Add(new FlexEntry(
          Text(e[..8]), Text(e.Slice(8, 3)), e[11], e[13], e[14], e[15], e[16],
          BinaryPrimitives.ReadUInt16BigEndian(e[17..]), e[19] != 0, e[21], e[22], e[23],
          at + 16 + i * EntrySize));
      }
    }
    this.DirectorySectors = dirs;
    this.Entries = entries;
  }

  private static string Text(ReadOnlySpan<byte> field) {
    var end = field.IndexOf((byte)0);
    return Encoding.ASCII.GetString(end < 0 ? field : field[..end]).TrimEnd(' ');
  }

  /// <summary>Byte offset of a sector; throws when it is outside the geometry or the image.</summary>
  public long Offset(int track, int sector) {
    if (track >= this.Tracks || sector < 1 || sector > this.SectorsPerTrack)
      throw new InvalidDataException($"FLEX sector {track},{sector} lies outside the disk's {this.Tracks}×{this.SectorsPerTrack} geometry.");
    var at = ((long)track * this.SectorsPerTrack + sector - 1) * SectorSize;
    if (at + SectorSize > this._stream.Length)
      throw new InvalidDataException($"FLEX sector {track},{sector} lies past the end of the image.");
    return at;
  }

  /// <summary>Reads one sector.</summary>
  public byte[] ReadSector(int track, int sector) => this.ReadAt(this.Offset(track, sector), SectorSize);

  /// <summary>Follows a link chain; throws on a loop or on more than <paramref name="limit"/> sectors.</summary>
  public IEnumerable<(byte Track, byte Sector, byte[] Data)> Chain(byte track, byte sector, int limit) {
    var seen = new HashSet<int>();
    while (track != 0 || sector != 0) {
      if (!seen.Add(track << 8 | sector) || seen.Count > limit)
        throw new InvalidDataException($"FLEX sector chain loops or overruns at {track},{sector}.");
      var data = this.ReadSector(track, sector);
      yield return (track, sector, data);
      (track, sector) = (data[0], data[1]);
    }
  }

  /// <summary>
  /// The file's bytes: the 252 data bytes of every sector in its chain — a random file's
  /// two File Sector Map sectors included, exactly as they are stored. Refuses a chain that
  /// does not end at the directory's end address after the directory's sector count.
  /// </summary>
  public byte[] Extract(FlexEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var result = new byte[entry.Size];
    var n = 0;
    (byte T, byte S) last = (0, 0);
    foreach (var (t, s, data) in this.Chain(entry.StartTrack, entry.StartSector, entry.Sectors)) {
      data.AsSpan(4, DataBytes).CopyTo(result.AsSpan(n * DataBytes));
      ++n;
      last = (t, s);
    }
    if (n != entry.Sectors || last != (entry.EndTrack, entry.EndSector))
      throw new InvalidDataException($"FLEX file {entry.FullName}: its chain holds {n} sectors ending at {last.T},{last.S}, the directory says {entry.Sectors} ending at {entry.EndTrack},{entry.EndSector}.");
    return result;
  }

  private byte[] ReadAt(long offset, int count) {
    var buf = new byte[count];
    this._stream.Position = offset;
    this._stream.ReadExactly(buf);
    return buf;
  }
}
