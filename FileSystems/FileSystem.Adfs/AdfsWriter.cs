#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Adfs;

/// <summary>
/// Builds a fresh Acorn ADFS "old-map" disk image (Write-Once-Read-Many).
/// </summary>
/// <remarks>
/// <para>
/// Targets the ADFS-L variant (640 KiB, 80-track double-sided floppy, 256-byte
/// sectors, 2 560 sectors total). The on-disk layout is:
/// </para>
/// <list type="bullet">
///   <item><description>Sector 0 — Free Space Map (FSM): list of 3-byte start-sector numbers, an end-pointer, disc identifier and check byte.</description></item>
///   <item><description>Sector 1 — FSM continuation: list of 3-byte fragment lengths, plus boot option, end-pointer and check byte.</description></item>
///   <item><description>Sectors 2..6 — Root directory (1280 bytes = 5 sectors), "Hugo"-bracketed.</description></item>
///   <item><description>Sectors 7..N — File data, contiguously allocated.</description></item>
/// </list>
/// <para>
/// Layout reference: BBC Master Reference Manual, Section "ADFS Disc Format"; also
/// https://mdfs.net/Docs/Comp/Disk/Format/ADFS. We emit the Acorn-canonical
/// check byte (rotate-and-add over bytes 0..0xFE) so the Linux ADFS kernel driver
/// accepts the image when mounted read-only. ADFS-D/E/F (new-map, 1024-byte
/// sectors) are out of scope for this writer.
/// </para>
/// </remarks>
public sealed class AdfsWriter {

  /// <summary>Sector size for ADFS old-map variants.</summary>
  public const int SectorSize = 256;

  /// <summary>ADFS-L canonical size: 80 tracks × 16 sectors × 2 sides × 256 bytes.</summary>
  public const int DiskSizeL = 80 * 16 * 2 * SectorSize;  // 655 360

  /// <summary>ADFS-M canonical size: 80 tracks × 16 sectors × 1 side × 256 bytes.</summary>
  public const int DiskSizeM = 80 * 16 * SectorSize;      // 327 680

  /// <summary>ADFS-S canonical size: 40 tracks × 16 sectors × 1 side × 256 bytes.</summary>
  public const int DiskSizeS = 40 * 16 * SectorSize;      // 163 840

  private const int RootDirSector = 2;
  private const int RootDirOffset = RootDirSector * SectorSize;  // 0x200
  private const int DirectorySize = 1280;  // 5 sectors
  private const int DirectorySectors = DirectorySize / SectorSize;  // 5
  private const int FirstDataSector = RootDirSector + DirectorySectors;  // 7
  private const int MaxDirEntries = 47;
  private const int DirEntrySize = 26;
  private const int DirEntriesOffset = 5;  // After "Hugo" magic

  private readonly List<(string Name, byte[] Data, uint LoadAddr, uint ExecAddr, byte Attrs)> _files = [];

  /// <summary>
  /// Disc identifier (16-bit) — stored at sector 0 byte 0xFB-0xFC and again
  /// at sector 1 byte 0xFB-0xFC. Used by ADFS to disambiguate physical media.
  /// </summary>
  public ushort DiscId { get; set; } = 0x1234;

  /// <summary>
  /// Boot option (0..7) stored at sector 1 byte 0xFD. 0=no boot, 1=*LOAD,
  /// 2=*RUN, 3=*EXEC are the canonical values.
  /// </summary>
  public byte BootOption { get; set; } = 0;

  /// <summary>Disc title — 19-byte ASCII string stored in the root directory tail.</summary>
  public string DiscTitle { get; set; } = "CWB-ADFS";

  /// <summary>Target disc size — defaults to ADFS-L (640 KiB).</summary>
  public int DiscSize { get; set; } = DiskSizeL;

  /// <summary>Adds a file with default load/exec addresses and no attributes.</summary>
  public void AddFile(string name, byte[] data) => this.AddFile(name, data, 0x0000FFFFu, 0x0000FFFFu, attrs: 0);

  /// <summary>Adds a file with specified load/exec/attributes.</summary>
  public void AddFile(string name, byte[] data, uint loadAddr, uint execAddr, byte attrs) {
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    this._files.Add((name, data, loadAddr, execAddr, attrs));
  }

  /// <summary>
  /// Builds the complete disc image. Throws <see cref="InvalidOperationException"/>
  /// if total file data would not fit in <see cref="DiscSize"/>.
  /// </summary>
  public byte[] Build() {
    if (this._files.Count > MaxDirEntries)
      throw new InvalidOperationException(
        $"ADFS old map: root directory holds at most {MaxDirEntries} entries (got {this._files.Count}).");

    var disc = new byte[this.DiscSize];
    var totalSectors = this.DiscSize / SectorSize;

    // Allocate data sectors contiguously from FirstDataSector onward.
    var nextSector = FirstDataSector;
    var dirEntries = new List<(string Name, byte[] Data, uint LoadAddr, uint ExecAddr, byte Attrs, uint StartSector, uint Length)>();
    foreach (var (rawName, data, loadAddr, execAddr, attrs) in this._files) {
      var name = SanitizeName(rawName);
      var sectorsNeeded = data.Length == 0 ? 0 : (data.Length + SectorSize - 1) / SectorSize;
      if (nextSector + sectorsNeeded > totalSectors)
        throw new InvalidOperationException(
          $"ADFS old map: file '{name}' ({data.Length} bytes) overflows disc capacity ({this.DiscSize} bytes).");
      if (data.Length > 0)
        Buffer.BlockCopy(data, 0, disc, nextSector * SectorSize, data.Length);
      dirEntries.Add((name, data, loadAddr, execAddr, attrs, (uint)nextSector, (uint)data.Length));
      nextSector += sectorsNeeded;
    }

    WriteRootDirectory(disc, dirEntries, this.DiscTitle);
    WriteFreeSpaceMap(disc, nextSector, totalSectors, this.DiscId, this.BootOption);
    return disc;
  }

  // ── Free Space Map (sectors 0 and 1) ───────────────────────────────────

  /// <summary>
  /// Emits the Acorn old-map FSM. Sector 0 holds a list of 3-byte free-fragment
  /// starts; sector 1 holds the matching 3-byte lengths. Trailing reserved
  /// bytes carry disc-id, boot option, end pointer and check byte.
  /// </summary>
  private static void WriteFreeSpaceMap(byte[] disc, int firstFreeSector, int totalSectors, ushort discId, byte bootOpt) {
    // After WriteRootDirectory + data, there's exactly one free fragment from
    // firstFreeSector to totalSectors-1 (length = totalSectors - firstFreeSector).
    var freeStart = (uint)firstFreeSector;
    var freeLen = (uint)(totalSectors - firstFreeSector);

    var s0 = disc.AsSpan(0, SectorSize);
    var s1 = disc.AsSpan(SectorSize, SectorSize);
    s0.Clear();
    s1.Clear();

    if (freeLen > 0) {
      // One free-list entry: 3 bytes start in sector 0, 3 bytes length in sector 1.
      WriteUInt24LittleEndian(s0[..3], freeStart);
      WriteUInt24LittleEndian(s1[..3], freeLen);
    }

    // End-of-FSM pointer: total bytes consumed by free-list entries (always 3 here).
    // Stored at byte 0xFE of sector 0 (FreeEnd0) and byte 0xFE of sector 1 (FreeEnd1).
    // For zero free entries we emit 0 (=> empty disc impossible since we always have data after dir).
    var endPointer = freeLen > 0 ? (byte)3 : (byte)0;

    // Sector 0 trailer:
    //   0xFB-0xFC : DiscId (16-bit LE)
    //   0xFD      : reserved (0)
    //   0xFE      : FreeEnd0 (end pointer)
    //   0xFF      : Check byte (recomputed below)
    BinaryPrimitives.WriteUInt16LittleEndian(s0.Slice(0xFB, 2), discId);
    s0[0xFD] = 0;
    s0[0xFE] = endPointer;
    s0[0xFF] = ComputeOldMapCheckByte(s0);

    // Sector 1 trailer:
    //   0xFB-0xFC : DiscId (16-bit LE) — must match sector 0
    //   0xFD      : Boot option (low 3 bits)
    //   0xFE      : FreeEnd1 (end pointer) — must match sector 0
    //   0xFF      : Check byte (recomputed below)
    BinaryPrimitives.WriteUInt16LittleEndian(s1.Slice(0xFB, 2), discId);
    s1[0xFD] = (byte)(bootOpt & 0x07);
    s1[0xFE] = endPointer;
    s1[0xFF] = ComputeOldMapCheckByte(s1);
  }

  /// <summary>
  /// Acorn old-map check byte: classic rotate-and-add (with carry) over bytes
  /// 0..0xFE, with the final byte placed at offset 0xFF. The algorithm walks
  /// the sector backward, adding each byte to a running sum and folding the
  /// carry back in — matches the Linux kernel <c>fs/adfs/super.c</c>
  /// <c>adfs_checkbyte()</c> implementation for old-map FSM sectors.
  /// </summary>
  private static byte ComputeOldMapCheckByte(ReadOnlySpan<byte> sector) {
    uint sum = 0;
    // Walk bytes 0..254. The Acorn formula is: sum = sum + buf[i] + carry, with
    // each iteration folding bit 8 back into bit 0. Equivalent expression:
    //   sum = (sum + buf[i]) ; if (sum > 0xFF) sum = (sum + 1) & 0xFF
    for (var i = 0; i < 255; i++) {
      sum += sector[i];
      if (sum > 0xFF)
        sum = (sum + 1) & 0xFF;
    }
    return (byte)sum;
  }

  // ── Root directory (sectors 2..6) ──────────────────────────────────────

  /// <summary>
  /// Emits the 1280-byte root directory bracketed by "Hugo" markers. Each
  /// entry occupies 26 bytes starting at offset 5; up to 47 entries fit
  /// before the tail-block (parent ref, disc title, reserved area, end-check
  /// byte) at offset 0x4CB.
  /// </summary>
  private static void WriteRootDirectory(byte[] disc, List<(string Name, byte[] Data, uint LoadAddr, uint ExecAddr, byte Attrs, uint StartSector, uint Length)> entries, string discTitle) {
    var dir = disc.AsSpan(RootDirOffset, DirectorySize);

    // Start magic: "Hugo" at offset 0.
    Encoding.ASCII.GetBytes("Hugo").CopyTo(dir);

    // Entries at offset 5; each entry is 26 bytes.
    for (var i = 0; i < entries.Count; i++) {
      var (name, _, loadAddr, execAddr, attrs, startSector, length) = entries[i];
      var entryOff = DirEntriesOffset + i * DirEntrySize;
      var entry = dir.Slice(entryOff, DirEntrySize);
      EncodeNameAndAttrs(entry[..10], name, attrs);
      BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x0A, 4), loadAddr);
      BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x0E, 4), execAddr);
      BinaryPrimitives.WriteUInt32LittleEndian(entry.Slice(0x12, 4), length);
      entry[0x16] = (byte)(startSector & 0xFF);
      entry[0x17] = (byte)((startSector >> 8) & 0xFF);
      entry[0x18] = (byte)((startSector >> 16) & 0xFF);
      entry[0x19] = 0;  // CycleCount / sequence number
    }
    // End-of-directory sentinel: next entry's first byte must be 0. The buffer
    // is already zero-initialised so no further action needed.

    // End magic at offset 0x4CB: "Hugo" again.
    Encoding.ASCII.GetBytes("Hugo").CopyTo(dir[0x4CB..]);

    // DirName (10 bytes) at offset 0x4CF: convention "$" for root, padded with 0x0D.
    dir[0x4CF] = (byte)'$';
    for (var i = 1; i < 10; i++) dir[0x4CF + i] = 0x0D;

    // ParentInd (3 bytes) at offset 0x4D9: root is self-referencing.
    dir[0x4D9] = RootDirSector;
    dir[0x4DA] = 0;
    dir[0x4DB] = 0;

    // DirTitle (19 bytes ASCII) at offset 0x4DC, padded with 0x0D.
    var titleBytes = Encoding.ASCII.GetBytes(discTitle);
    var titleLen = Math.Min(titleBytes.Length, 19);
    titleBytes.AsSpan(0, titleLen).CopyTo(dir.Slice(0x4DC, 19));
    for (var i = titleLen; i < 19; i++) dir[0x4DC + i] = 0x0D;

    // Reserved 14 bytes at offset 0x4EF — left zero.

    // EndCheckByte at offset 0x4FD: XOR of all preceding bytes in the directory.
    byte check = 0;
    for (var i = 0; i < 0x4FD; i++) check ^= dir[i];
    dir[0x4FD] = check;

    // The trailing bytes 0x4FE..0x4FF are part of the 1280-byte block; leave zero.
  }

  /// <summary>
  /// Encodes a 10-byte name + attribute block. The high bit of each of the
  /// first 9 characters carries an attribute flag (R/W/L/D/E/r/w/e/P); the
  /// 10th byte is unused (and we write 0x0D as the conventional terminator).
  /// </summary>
  private static void EncodeNameAndAttrs(Span<byte> dst, string name, byte attrs) {
    dst.Clear();
    var len = Math.Min(name.Length, 9);
    for (var i = 0; i < len; i++) {
      var c = (byte)(name[i] & 0x7F);
      if (i < 9 && (attrs & (1 << i)) != 0) c |= 0x80;
      dst[i] = c;
    }
    if (len < 9) dst[len] = 0x0D;  // terminator
  }

  /// <summary>
  /// Sanitises a raw input filename for ADFS: 10 chars max (with attribute
  /// byte 10 unused), uppercase ASCII, '.' for unrepresentable chars. The
  /// tail is preserved when truncating (matches AppleDOS-style convention).
  /// </summary>
  private static string SanitizeName(string raw) {
    if (string.IsNullOrEmpty(raw)) return "UNNAMED";
    var s = Path.GetFileName(raw).ToUpperInvariant();
    var chars = new char[s.Length];
    for (var i = 0; i < s.Length; i++) {
      var c = s[i];
      // ADFS allows printable ASCII except '.', '$', ':', '#', '*' which are
      // path-separator/wildcard metacharacters.
      chars[i] = (c >= 0x20 && c < 0x7F && c != '.' && c != '$' && c != ':' && c != '#' && c != '*') ? c : '_';
    }
    var clean = new string(chars).TrimEnd('_');
    if (clean.Length == 0) clean = "UNNAMED";
    // ADFS root-directory name slot is 10 bytes, but byte 0 carries the 'R'
    // attribute high bit, so 9 usable characters.
    if (clean.Length > 9) clean = clean[^9..];
    return clean;
  }

  // ── Helpers ────────────────────────────────────────────────────────────

  private static void WriteUInt24LittleEndian(Span<byte> dst, uint v) {
    dst[0] = (byte)(v & 0xFF);
    dst[1] = (byte)((v >> 8) & 0xFF);
    dst[2] = (byte)((v >> 16) & 0xFF);
  }
}
