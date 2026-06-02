#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Adfs;

/// <summary>
/// Reader for Acorn Advanced Disc Filing System (ADFS) "old map" image formats
/// (ADFS-S, ADFS-M, ADFS-L). Sector size = 256 bytes. The root directory is at
/// sector 2 (file offset 0x200). Each directory is 1280 bytes (5 sectors) and
/// bracketed by a 4-byte "Hugo" or "Nick" marker at the start (DirHdr) and
/// matching marker just before the directory tail.
///
/// Directory layout (per https://mdfs.net/Docs/Comp/Disk/Format/ADFS, originally
/// published in the BBC Master Reference Manual):
///   +0x000   StartName    1 byte 'H' (=0x48) — start of "Hugo" magic
///   +0x000   "Hugo"       4 bytes (master/L variant) or "Nick" 4 bytes
///   +0x005   DirEntries   47 entries x 26 bytes = 1222 bytes
///   +0x4CB   EndName      "Hugo" again
///   +0x4CF   DirName      10-byte master sequence name (parent ref)
///   +0x4D9   ParentInd    3-byte parent directory sector
///   +0x4DC   DirTitle     19-byte ASCII title
///   +0x4EF   Reserved     14 bytes
///   +0x4FD   EndCheckByte 1 byte
///
/// Each 26-byte directory entry:
///   +0x00  Name        10 bytes (top bit of byte 0 = attribute flag)
///   +0x0A  LoadAddr    4 bytes (LE)
///   +0x0E  ExecAddr    4 bytes (LE)
///   +0x12  Length      4 bytes (LE)
///   +0x16  IndCyl      3 bytes (start sector, LE)
///   +0x19  CycleCount  1 byte (sequence #)
///
/// Attributes are encoded in the high bits of the name characters (R=byte0,
/// W=byte1, L=byte2, D=byte3, E=byte4, r=byte5, w=byte6, e=byte7, P=byte8).
/// D = directory.
///
/// We support the "old map" (S/M/L) variant by default; the newer D/E/F
/// variants use 1024-byte sectors and a different free-space map but the
/// directory layout is similar. Format auto-detected via the "Hugo" marker.
/// </summary>
public sealed class AdfsReader : IDisposable {

  private readonly byte[] _data;
  private readonly List<AdfsEntry> _entries = [];

  public IReadOnlyList<AdfsEntry> Entries => this._entries;

  /// <summary>Sector size — 256 bytes for old map (S/M/L), 1024 for new map (D/E/F).</summary>
  public int SectorSize { get; private set; } = 256;

  /// <summary>Magic word found in the directory header ("Hugo" / "Nick").</summary>
  public string DirectoryMagic { get; private set; } = "";

  private const int DirectorySize = 1280;
  // The 4-byte directory marker at offset 0 + offset 0x4CB.
  // We accept "Hugo" (master variant) and "Nick" (BBC Micro variant).

  public AdfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    // Locate the root directory by probing both old-map (offset 0x200 with
    // 256-byte sectors) and new-map (offset 0x400 with 1024-byte sectors).
    // Old-map root: file offset 0x200 (sector 2 of 256-byte sectors).
    // New-map root: file offset 0x400 (sector 2 of 1024-byte sectors).
    if (this.TryParseAt(0x200, 256, "")) {
      this.SectorSize = 256;
      return;
    }
    if (this.TryParseAt(0x400, 1024, "")) {
      this.SectorSize = 1024;
      return;
    }
    throw new InvalidDataException("ADFS: no directory marker 'Hugo' or 'Nick' at the expected root locations (0x200 or 0x400).");
  }

  private bool TryParseAt(long dirOffset, int sectorSize, string path) {
    if (dirOffset + DirectorySize > this._data.Length) return false;
    var magic = Encoding.ASCII.GetString(this._data, (int)dirOffset, 4);
    if (magic is not ("Hugo" or "Nick")) return false;
    var endMagic = Encoding.ASCII.GetString(this._data, (int)dirOffset + 0x4CB, 4);
    if (endMagic != magic) return false;
    this.DirectoryMagic = magic;
    this.WalkDirectory(dirOffset, sectorSize, path, new HashSet<long>());
    return true;
  }

  private void WalkDirectory(long dirOffset, int sectorSize, string path, HashSet<long> seen) {
    if (!seen.Add(dirOffset)) return;
    // Up to 47 entries starting at +5
    const int firstEntry = 5;
    const int entrySize = 26;
    const int maxEntries = 47;
    for (var i = 0; i < maxEntries; i++) {
      var entryOff = (int)dirOffset + firstEntry + i * entrySize;
      if (entryOff + entrySize > this._data.Length) break;
      var nameSpan = this._data.AsSpan(entryOff, 10);
      // End-of-directory sentinel: name byte 0 = 0
      if ((nameSpan[0] & 0x7F) == 0) break;
      var (name, attrs) = DecodeNameAndAttrs(nameSpan);
      var loadAddr = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff + 0x0A));
      var execAddr = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff + 0x0E));
      var length = BinaryPrimitives.ReadUInt32LittleEndian(this._data.AsSpan(entryOff + 0x12));
      var indCyl = (uint)(this._data[entryOff + 0x16] |
                          (this._data[entryOff + 0x17] << 8) |
                          (this._data[entryOff + 0x18] << 16));
      var isDir = (attrs & 0x08) != 0; // 'D' attribute bit

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      this._entries.Add(new AdfsEntry {
        Name = fullPath,
        Size = isDir ? 0 : length,
        StartSector = indCyl,
        IsDirectory = isDir,
        LoadAddress = loadAddr,
        ExecAddress = execAddr,
        Attributes = attrs,
      });

      if (isDir) {
        var subOffset = (long)indCyl * sectorSize;
        if (subOffset + DirectorySize <= this._data.Length)
          this.WalkDirectory(subOffset, sectorSize, fullPath, seen);
      }
    }
  }

  /// <summary>
  /// Decodes a 10-byte filename + attribute block. The high bit of each of the
  /// first 9 characters carries an attribute flag (R/W/L/D/E/r/w/e/P);
  /// the 10th byte is unused. The name itself is the low 7 bits, terminated
  /// by 0 or 0x0D.
  /// </summary>
  private static (string name, byte attrs) DecodeNameAndAttrs(ReadOnlySpan<byte> raw) {
    byte attrs = 0;
    var nameBytes = new byte[10];
    var n = 0;
    for (var i = 0; i < 10; i++) {
      var c = raw[i];
      if (i < 9 && (c & 0x80) != 0) attrs |= (byte)(1 << i);
      var lo = (byte)(c & 0x7F);
      if (lo == 0 || lo == 0x0D) break;
      nameBytes[n++] = lo;
    }
    return (Encoding.ASCII.GetString(nameBytes, 0, n), attrs);
  }

  public byte[] Extract(AdfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    var offset = (long)entry.StartSector * this.SectorSize;
    if (offset < 0 || offset >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - offset);
    return this._data.AsSpan((int)offset, take).ToArray();
  }

  public void Dispose() { }
}
