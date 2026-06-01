#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Cromemco;

/// <summary>
/// Builds a fresh Cromemco RDOS disk image from scratch (Write-Once,
/// Read-Many). The format is CP/M-derived: a boot block at sector 0
/// (with the 0xC3 JP-instruction prefix and an embedded "CROMEMCO" tag
/// at offset 0x0B), a flat directory area starting at sector 2 with
/// 32-byte entries, and data blocks immediately following.
/// </summary>
/// <remarks>
/// <para>Geometry knobs:
/// <list type="bullet">
/// <item><b>Single density</b>: 128-byte sectors, ~35 tracks × 18 spt
/// (default for the original Z2 floppies).</item>
/// <item><b>Double density</b>: 256-byte sectors, 77 tracks × 26 spt
/// (System Three quad-density variants).</item>
/// </list>
/// </para>
/// <para>The reader hard-codes <see cref="CromemcoReader.SectorSize"/> at
/// 128 bytes, so this writer always emits 128-byte sectors. Track count
/// drives the total image size. Maximum entries per disk is <see
/// cref="CromemcoReader.MaxEntries"/> (64); attempting to add more throws.
/// </para>
/// </remarks>
public sealed class CromemcoWriter {

  private const int SectorSize = CromemcoReader.SectorSize;       // 128
  private const int DirectoryOffset = CromemcoReader.DirectoryOffset; // 0x100
  private const int EntrySize = CromemcoReader.EntrySize;         // 32
  private const int MaxEntries = CromemcoReader.MaxEntries;       // 64

  /// <summary>Directory sector count = ceil(MaxEntries * EntrySize / SectorSize).</summary>
  internal const int DirectorySectors = (MaxEntries * EntrySize + SectorSize - 1) / SectorSize; // 16
  /// <summary>First data sector index = 2 (boot) + 16 (directory) = 18.</summary>
  internal const int FirstDataSector = 2 + DirectorySectors;      // 18

  private readonly List<(string Name, byte[] Data)> _files = [];
  private int _tracks = 77;
  private int _sectorsPerTrack = 18;

  /// <summary>Sets disk geometry. Single density = 35 tracks / 18 spt;
  /// double density = 77 tracks / 26 spt.</summary>
  public void SetGeometry(int tracks, int sectorsPerTrack) {
    if (tracks <= 0) throw new ArgumentOutOfRangeException(nameof(tracks));
    if (sectorsPerTrack <= 0) throw new ArgumentOutOfRangeException(nameof(sectorsPerTrack));
    this._tracks = tracks;
    this._sectorsPerTrack = sectorsPerTrack;
  }

  /// <summary>Adds one flat file. Names are CP/M-style 8.3, ASCII, upper-case.</summary>
  public void AddFile(string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data));
  }

  /// <summary>Total image size = tracks × sectorsPerTrack × SectorSize.</summary>
  public int TotalSize => this._tracks * this._sectorsPerTrack * SectorSize;

  /// <summary>Builds the complete disk image.</summary>
  public byte[] Build() {
    if (this._files.Count > MaxEntries)
      throw new InvalidOperationException(
        $"Cromemco RDOS: directory holds at most {MaxEntries} entries; tried {this._files.Count}.");

    var totalSectors = this.TotalSize / SectorSize;
    var usableDataSectors = totalSectors - FirstDataSector;
    var sectorsNeeded = 0;
    foreach (var f in this._files)
      sectorsNeeded += (f.Data.Length + SectorSize - 1) / SectorSize;
    if (sectorsNeeded > usableDataSectors)
      throw new InvalidOperationException(
        $"Cromemco RDOS: combined file size needs {sectorsNeeded} sectors but only {usableDataSectors} are available " +
        $"(geometry {this._tracks}x{this._sectorsPerTrack}x{SectorSize}).");

    var image = new byte[this.TotalSize];

    // Bootblock: 0xC3 JP + dummy address + "CROMEMCO" tag at 0x0B.
    image[0] = 0xC3;
    image[1] = 0x00;
    image[2] = 0x01;
    Encoding.ASCII.GetBytes("CROMEMCO").CopyTo(image.AsSpan(0x0B));

    // Walk files: pack each consecutively starting at FirstDataSector.
    var nextBlock = FirstDataSector;
    for (var i = 0; i < this._files.Count; i++) {
      var (rawName, data) = this._files[i];
      var (name, ext) = SplitName(rawName);
      var sectorsUsed = (data.Length + SectorSize - 1) / SectorSize;
      // Data layout: copy the file's bytes directly at the start block.
      Array.Copy(data, 0, image, nextBlock * SectorSize, data.Length);

      // Directory entry.
      var entryOff = DirectoryOffset + i * EntrySize;
      image[entryOff] = 0x00; // user code (live entry)
      WriteCpmField(image.AsSpan(entryOff + 1, 8), name);
      WriteCpmField(image.AsSpan(entryOff + 9, 3), ext);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 12, 2), (ushort)nextBlock);
      BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(entryOff + 14, 2), (ushort)sectorsUsed);
      // 0x10: bytes-in-last-sector (0 == sector full). Reader uses this to
      // recover exact byte length when the file size is not a multiple of
      // SectorSize. The original CP/M-derived spec leaves 0x10..0x1F as
      // reserved space; we co-opt one byte rather than rounding the file
      // size up to the next sector boundary on round-trip.
      var tail = data.Length % SectorSize;
      image[entryOff + 16] = (byte)tail;
      // 0x11..0x1F reserved (zero).

      nextBlock += sectorsUsed;
    }

    return image;
  }

  private static (string Name, string Ext) SplitName(string raw) {
    var safe = raw.Replace('\\', '/');
    var slash = safe.LastIndexOf('/');
    if (slash >= 0) safe = safe[(slash + 1)..];
    safe = safe.ToUpperInvariant();
    var dot = safe.LastIndexOf('.');
    string name;
    string ext;
    if (dot > 0) {
      name = safe[..dot];
      ext = safe[(dot + 1)..];
    } else {
      name = safe;
      ext = "";
    }
    if (name.Length > 8) name = name[..8];
    if (ext.Length > 3) ext = ext[..3];
    return (name, ext);
  }

  private static void WriteCpmField(Span<byte> dst, string value) {
    dst.Fill(0x20);
    var n = Math.Min(value.Length, dst.Length);
    for (var i = 0; i < n; i++) {
      var c = value[i];
      // Pure ASCII; ignore any high-bit chars.
      dst[i] = c < 0x80 ? (byte)c : (byte)'?';
    }
  }
}
