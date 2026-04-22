#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Atari8;

/// <summary>
/// Reader for Atari 8-bit AtariDOS 2.x <c>.atr</c> disk images.
/// </summary>
/// <remarks>
/// <para>
/// <b>ATR header</b> (16 bytes, little-endian):
/// </para>
/// <list type="bullet">
///   <item>bytes 0-1: magic 0x96 0x02 (word 0x0296, stored LE).</item>
///   <item>bytes 2-3: paragraphs (16-byte units) — total image size / 16, low word.</item>
///   <item>bytes 4-5: sector size (128 or 256).</item>
///   <item>bytes 6-7: paragraphs-high (for large images).</item>
///   <item>remainder: flags, reserved.</item>
/// </list>
/// <para>
/// AtariDOS 2.x uses sectors 1..720 (sector numbers are 1-based). Sectors 361-368 hold
/// the directory (8 sectors x 8 entries x 16 bytes = 64 slots). Each data sector carries
/// 3 bytes of metadata in its last 3 bytes: next-sector-number (10-bit split across two
/// bytes) + byte-count-in-sector.
/// </para>
/// <para>
/// Scope: AtariDOS 2.0S single-density 128-byte sectors (the dominant case). Higher
/// densities parse but chain-trailer layout is identical.
/// </para>
/// </remarks>
public sealed class Atari8Reader : IDisposable {

  public const int AtrHeaderSize = 16;
  public const int DefaultSectorSize = 128;
  public const int DirectoryStartSector = 361;
  public const int DirectorySectorCount = 8;
  public const int EntriesPerDirectorySector = 8;
  public const int DirectoryEntrySize = 16;

  private readonly byte[] _data;
  private readonly List<Atari8Entry> _entries = [];

  /// <summary>Sector size read from the ATR header (128 or 256).</summary>
  public int SectorSize { get; }

  public IReadOnlyList<Atari8Entry> Entries => _entries;

  public Atari8Reader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();

    if (_data.Length < AtrHeaderSize + DefaultSectorSize * 3)
      throw new InvalidDataException("ATR: file too small.");

    if (_data[0] != 0x96 || _data[1] != 0x02)
      throw new InvalidDataException("ATR: missing 0x0296 magic.");

    var rawSectorSize = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(4));
    SectorSize = rawSectorSize == 0 ? DefaultSectorSize : rawSectorSize;
    if (SectorSize is not (128 or 256))
      throw new InvalidDataException($"ATR: unsupported sector size {SectorSize}.");

    ParseDirectory();
  }

  public Atari8Reader(byte[] data) : this(new MemoryStream(data)) { }

  /// <summary>Returns the byte offset inside the image for a 1-based sector number.</summary>
  private int SectorOffset(int sector1Based) {
    // Atari quirk: sectors 1-3 are always 128 bytes (boot sectors) even in DD images,
    // but AtariDOS 2.x only uses SD or keeps DD sectors 1-3 at 128 bytes.
    if (SectorSize == 256 && sector1Based <= 3)
      return AtrHeaderSize + (sector1Based - 1) * 128;
    var headStart = AtrHeaderSize + (SectorSize == 256 ? 3 * 128 : 0);
    var idx = sector1Based - 1 - (SectorSize == 256 ? 3 : 0);
    return SectorSize == 256
      ? headStart + idx * 256
      : AtrHeaderSize + (sector1Based - 1) * 128;
  }

  private void ParseDirectory() {
    for (var i = 0; i < DirectorySectorCount; i++) {
      var sectorNo = DirectoryStartSector + i;
      var off = SectorOffset(sectorNo);
      if (off + SectorSize > _data.Length) break;

      for (var j = 0; j < EntriesPerDirectorySector; j++) {
        var eo = off + j * DirectoryEntrySize;
        var flags = _data[eo + 0];

        // 0x00 = never used; the directory ends at the first 0x00 entry on most DOSes.
        if (flags == 0x00) return;

        // AtariDOS 2.x flags bit semantics (per Atari DOS 2 manual):
        //   bit 7 = 0x80 deleted
        //   bit 6 = 0x40 in-use
        //   bit 5 = 0x20 locked
        //   bit 1 = 0x02 DOS 2 file
        //   bit 0 = 0x01 open-for-write
        // Skip deleted entries, require in-use.
        if ((flags & 0x80) != 0) continue;  // deleted
        if ((flags & 0x40) == 0) continue;  // not in-use

        var sectorCount = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(eo + 1));
        var startSector = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(eo + 3));

        // Filename: 8 bytes name + 3 bytes extension, space-padded ATASCII.
        var nameBuf = new byte[8];
        Buffer.BlockCopy(_data, eo + 5, nameBuf, 0, 8);
        var extBuf = new byte[3];
        Buffer.BlockCopy(_data, eo + 13, extBuf, 0, 3);

        var nameStr = Encoding.ASCII.GetString(nameBuf).TrimEnd();
        var extStr = Encoding.ASCII.GetString(extBuf).TrimEnd();
        var fullName = extStr.Length > 0 ? nameStr + "." + extStr : nameStr;

        if (string.IsNullOrEmpty(fullName)) continue;

        // Compute size by walking the sector chain and summing per-sector byte-counts.
        var size = ComputeFileSize(startSector);

        _entries.Add(new Atari8Entry {
          Name = fullName,
          Size = size,
          Flags = flags,
          SectorCount = sectorCount,
          StartSector = startSector,
        });
      }
    }
  }

  private int ComputeFileSize(int startSector) {
    var total = 0;
    var visited = new HashSet<int>();
    var current = startSector;
    while (current != 0 && visited.Add(current)) {
      var off = SectorOffset(current);
      if (off + SectorSize > _data.Length) break;
      // Last 3 bytes of sector: [fileNo+nextHi][nextLo][byteCount&0x7F]
      var b0 = _data[off + SectorSize - 3];
      var b1 = _data[off + SectorSize - 2];
      var b2 = _data[off + SectorSize - 1];
      var next = ((b0 & 0x03) << 8) | b1;
      var bytes = b2 & 0x7F;
      total += bytes;
      if (next == 0) break;
      current = next;
    }
    return total;
  }

  public byte[] Extract(Atari8Entry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    var buf = new List<byte>((int)entry.Size);
    var visited = new HashSet<int>();
    var current = entry.StartSector;
    while (current != 0 && visited.Add(current)) {
      var off = SectorOffset(current);
      if (off + SectorSize > _data.Length) break;
      var b0 = _data[off + SectorSize - 3];
      var b1 = _data[off + SectorSize - 2];
      var b2 = _data[off + SectorSize - 1];
      var next = ((b0 & 0x03) << 8) | b1;
      var bytes = b2 & 0x7F;
      if (bytes > SectorSize - 3) bytes = SectorSize - 3;
      for (var k = 0; k < bytes; k++)
        buf.Add(_data[off + k]);
      if (next == 0) break;
      current = next;
    }
    return buf.ToArray();
  }

  public void Dispose() { }
}
