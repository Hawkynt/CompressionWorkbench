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

  /// <summary>
  /// Gets the entries.
  /// </summary>
  public IReadOnlyList<AdfsEntry> Entries => this._entries;

  /// <summary>Sector size — 256 bytes for old map (S/M/L), 1024 for new map (D/E/F).</summary>
  public int SectorSize { get; private set; } = 256;

  /// <summary>Magic word found in the directory header ("Hugo" / "Nick").</summary>
  public string DirectoryMagic { get; private set; } = "";

  private const int DirectorySize = 1280;

  /// <summary>True when the volume carries a new-map (D/E/F) disc record.</summary>
  public bool IsNewMap { get; private set; }

  // New-map state, read from the disc record at sector 0 + 4.
  private int _idLen;
  private int _map2Blk;
  private int _mapStartBit;
  private int _mapEndBit;
  // The 4-byte directory marker at offset 0 + offset 0x4CB.
  // We accept "Hugo" (master variant) and "Nick" (BBC Micro variant).

  /// <summary>
  /// Initializes a new instance of <see cref="AdfsReader"/>.
  /// </summary>
  public AdfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var ms = new MemoryStream();
    if (stream.CanSeek) stream.Position = 0;
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    this.Parse();
  }

  private void Parse() {
    // A new-map volume states its own geometry in the disc record, so it is
    // probed first — its root lives wherever the map says, not at a fixed
    // offset.
    if (this.TryParseNewMap()) {
      this.IsNewMap = true;
      return;
    }

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

  /// <summary>
  /// Reads a new-map volume: the disc record at sector 0 + 4 gives the geometry
  /// and the root's indirect address, and the zone bitmap that follows resolves
  /// a fragment to the sectors holding it. Only single-zone maps are read —
  /// which is what <see cref="AdfsNewMapWriter" /> emits.
  /// </summary>
  private bool TryParseNewMap() {
    if (this._data.Length < 1024) return false;
    var dr = this._data.AsSpan(4, 60);

    var log2SecSize = dr[0];
    if (log2SecSize is < 8 or > 10) return false;
    var idLen = dr[4];
    var log2Bpmb = dr[5];
    var nZones = dr[9] | (dr[42] << 8);
    if (nZones != 1) return false;
    if (idLen < log2SecSize + 3 || idLen > 19) return false;

    var zoneSpare = BinaryPrimitives.ReadUInt16LittleEndian(dr[10..]);
    var rootIndaddr = BinaryPrimitives.ReadUInt32LittleEndian(dr[12..]);
    var discSize = BinaryPrimitives.ReadUInt32LittleEndian(dr[16..])
                 | ((long)BinaryPrimitives.ReadUInt32LittleEndian(dr[36..]) << 32);
    var rootSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(dr[48..]);
    if (rootIndaddr == 0 || discSize <= 0) return false;

    var sectorSize = 1 << log2SecSize;
    var zoneSize = (8 << log2SecSize) - zoneSpare;
    if (zoneSize <= 0 || zoneSize > 8 * sectorSize) return false;

    // Single zone: the bitmap starts past the header and the disc record, and
    // ends where the volume does.
    this._idLen = idLen;
    this._map2Blk = log2Bpmb - log2SecSize;
    this._mapStartBit = 32 + NewMapDiscRecordBits;
    this._mapEndBit = (int)(32 + (discSize >> log2Bpmb) + NewMapDiscRecordBits);
    if (this._mapEndBit > 8 * sectorSize) return false;

    this.SectorSize = sectorSize;
    var rootSector = this.MapLookup(rootIndaddr >> 8, 0);
    if (rootSector < 0) return false;

    var rootOffset = (long)rootSector * sectorSize;
    if (rootOffset + NewDirectorySize > this._data.Length) return false;
    // A new-map directory header is a master-sequence byte followed by the
    // name, so the marker sits one byte in.
    var magic = Encoding.ASCII.GetString(this._data, (int)rootOffset + 1, 4);
    if (magic is not ("Hugo" or "Nick")) return false;
    this.DirectoryMagic = magic;

    this.WalkNewDirectory(rootIndaddr, rootSize == 0 ? NewDirectorySize : rootSize, "",
      new HashSet<uint>());
    return true;
  }

  /// <summary>Bits the disc record occupies at the head of zone 0.</summary>
  private const int NewMapDiscRecordBits = 60 * 8;

  /// <summary>Size of a new-map "Hugo" directory.</summary>
  private const int NewDirectorySize = 2048;

  /// <summary>
  /// Resolves block <paramref name="offset" /> of fragment
  /// <paramref name="fragId" /> to a sector, walking the zone bitmap the way the
  /// filesystem itself does: each fragment is its id in the low idlen bits,
  /// then zeros, then a set bit at its last bit.
  /// </summary>
  private int MapLookup(uint fragId, int offset) {
    var map = this._data.AsSpan(0, this.SectorSize);
    var idMask = (uint)((1 << this._idLen) - 1);

    // The free-space chain is threaded through the same bitmap, so its
    // fragments are skipped rather than matched.
    var link = ReadBits(map, 8, idMask & 0x7fff);
    var freeLink = link != 0 ? (int)(8 + link) : 0;

    var start = this._mapStartBit;
    var remaining = offset;
    while (start < this._mapEndBit) {
      var frag = ReadBits(map, start, idMask);
      var fragEnd = FindNextSetBit(map, this._mapEndBit, start + this._idLen);
      if (fragEnd >= this._mapEndBit) return -1;

      if (start == freeLink) {
        freeLink += (int)(frag & 0x7fff);
      } else if (frag == fragId) {
        var length = fragEnd + 1 - start;
        if (remaining < length) {
          var result = start + remaining - this._mapStartBit;
          return this._map2Blk >= 0 ? result << this._map2Blk : result >> -this._map2Blk;
        }
        remaining -= length;
      }

      start = fragEnd + 1;
    }
    return -1;
  }

  private static uint ReadBits(ReadOnlySpan<byte> map, int startBit, uint mask) {
    var byteIndex = startBit >> 3;
    if (byteIndex + 4 > map.Length) return 0;
    var word = BinaryPrimitives.ReadUInt32LittleEndian(map[byteIndex..]);
    return (word >> (startBit & 7)) & mask;
  }

  private static int FindNextSetBit(ReadOnlySpan<byte> map, int endBit, int startBit) {
    for (var bit = startBit; bit < endBit; ++bit)
      if ((map[bit >> 3] & (1 << (bit & 7))) != 0)
        return bit;
    return endBit;
  }

  private void WalkNewDirectory(uint indaddr, int size, string path, HashSet<uint> seen) {
    if (!seen.Add(indaddr)) return;
    var dir = this.ReadObject(indaddr, size);
    if (dir.Length < NewDirectorySize) return;

    const int firstEntry = 5;
    const int entrySize = 26;
    const int maxEntries = 77;
    for (var i = 0; i < maxEntries; ++i) {
      var off = firstEntry + i * entrySize;
      if (off + entrySize > dir.Length) break;
      if (dir[off] == 0) break;   // the terminating entry

      var nameLength = 0;
      while (nameLength < 10 && dir[off + nameLength] >= 0x20) ++nameLength;
      var name = Encoding.ASCII.GetString(dir, off, nameLength);

      var loadAddr = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 10));
      var execAddr = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 14));
      var length = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 18));
      var childIndaddr = (uint)(dir[off + 22] | (dir[off + 23] << 8) | (dir[off + 24] << 16));
      var attrs = dir[off + 25];
      var isDirectory = (attrs & 0x08) != 0;

      var fullPath = string.IsNullOrEmpty(path) ? name : $"{path}/{name}";
      this._entries.Add(new AdfsEntry {
        Name = fullPath,
        Size = isDirectory ? 0 : length,
        StartSector = 0,
        IsDirectory = isDirectory,
        LoadAddress = loadAddr,
        ExecAddress = execAddr,
        Attributes = attrs,
        IndirectAddress = childIndaddr,
      });

      if (isDirectory)
        this.WalkNewDirectory(childIndaddr, NewDirectorySize, fullPath, seen);
    }
  }

  /// <summary>Reads <paramref name="size" /> bytes of the object at an indirect address.</summary>
  private byte[] ReadObject(uint indaddr, int size) {
    if (size <= 0) return [];
    var result = new byte[size];
    var blocks = (size + this.SectorSize - 1) / this.SectorSize;
    for (var block = 0; block < blocks; ++block) {
      var sector = this.MapLookup(indaddr >> 8, block);
      if (sector < 0) break;
      var source = (long)sector * this.SectorSize;
      if (source < 0 || source >= this._data.Length) break;
      var take = (int)Math.Min(Math.Min(this.SectorSize, size - block * this.SectorSize),
        this._data.Length - source);
      if (take <= 0) break;
      this._data.AsSpan((int)source, take).CopyTo(result.AsSpan(block * this.SectorSize));
    }
    return result;
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public byte[] Extract(AdfsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDirectory) return [];
    if (this.IsNewMap) return this.ReadObject(entry.IndirectAddress, (int)entry.Size);
    var offset = (long)entry.StartSector * this.SectorSize;
    if (offset < 0 || offset >= this._data.Length) return [];
    var take = (int)Math.Min(entry.Size, this._data.Length - offset);
    return this._data.AsSpan((int)offset, take).ToArray();
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
  public void Dispose() { }
}
