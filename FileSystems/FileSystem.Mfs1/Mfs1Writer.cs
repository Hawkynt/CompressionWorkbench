#pragma warning disable CS1591
using System.Text;

namespace FileSystem.Mfs1;

/// <summary>
/// WORM writer for Acorn MFS-1 (Master File System v1) disk images. MFS-1
/// inherits the on-disk DFS catalog layout verbatim — 256-byte sectors, the
/// two-sector catalog at sectors 0-1, up to 31 entries of (7-char filename +
/// 1-char directory letter) packed in sector 0 with the matching metadata in
/// sector 1 (load/exec/length plus the packed high-bits byte and start sector).
/// </summary>
/// <remarks>
/// <para>
/// The writer reproduces the documented spec exactly so the existing
/// <see cref="Mfs1Reader"/> can round-trip every emitted image: same title
/// layout, same per-entry packed-high-bits encoding, same sector-2 file-data
/// origin. File data is laid out as contiguous extents starting at sector 2
/// in catalog order; the image is sized to the smallest 10-sector-aligned
/// length that covers the highest data sector.
/// </para>
/// <para>
/// Limits enforced (from the format itself):
/// <list type="bullet">
///   <item>Up to 31 files in the catalog (per DFS).</item>
///   <item>Filename: up to 7 printable ASCII characters, uppercased.</item>
///   <item>Directory letter: single printable ASCII (defaults to <c>$</c>).</item>
///   <item>Per-file length: 18 bits (256 KiB) by virtue of the packed-high-bits encoding.</item>
///   <item>Start sector: 10 bits (1024) — covers a single-density Acorn disc.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class Mfs1Writer {

  private const int SectorSize = Mfs1Reader.SectorSize;
  private const int SectorsPerTrack = Mfs1Reader.SectorsPerTrack;
  private const int MaxEntries = Mfs1Reader.MaxEntries;
  private const int DataOriginSector = 2;

  private readonly List<(string Name, char Directory, byte[] Data)> _files = [];
  private string _title = "MFS1DISK";

  /// <summary>Sets the 12-character disc title (excess is truncated, fewer chars are space-padded).</summary>
  public Mfs1Writer SetTitle(string title) {
    ArgumentNullException.ThrowIfNull(title);
    this._title = title;
    return this;
  }

  /// <summary>
  /// Adds a file to the catalog. <paramref name="archiveName"/> may carry a
  /// directory prefix as <c>"DIR.NAME"</c> (DFS convention) — the part before
  /// the first <c>.</c> becomes the directory letter; otherwise the directory
  /// defaults to <c>$</c>. The filename is uppercased and padded to 7 chars.
  /// </summary>
  public Mfs1Writer AddFile(string archiveName, byte[] data) {
    ArgumentNullException.ThrowIfNull(archiveName);
    ArgumentNullException.ThrowIfNull(data);

    var (dir, name) = SplitDfsName(archiveName);
    if (name.Length == 0) throw new ArgumentException("MFS-1: empty filename.", nameof(archiveName));
    if (name.Length > 7) throw new ArgumentException($"MFS-1: filename '{name}' exceeds 7 characters.", nameof(archiveName));
    foreach (var c in name)
      if (c is < (char)0x20 or > (char)0x7E)
        throw new ArgumentException($"MFS-1: filename '{name}' contains non-printable ASCII.", nameof(archiveName));
    if (dir is < (char)0x20 or > (char)0x7E)
      throw new ArgumentException($"MFS-1: directory letter '{dir}' is not printable ASCII.", nameof(archiveName));
    if (data.Length > 0x3FFFF)
      throw new ArgumentException(
        $"MFS-1: file '{name}' is {data.Length} bytes; DFS packed-high-bits length cap is 256 KiB.", nameof(data));

    if (this._files.Count >= MaxEntries)
      throw new InvalidOperationException($"MFS-1: catalog full ({MaxEntries} entries).");

    this._files.Add((name.ToUpperInvariant(), char.ToUpperInvariant(dir), data));
    return this;
  }

  /// <summary>Builds the full MFS-1 image bytes.</summary>
  public byte[] Build() {
    // Allocate space for catalog (2 sectors) + each file contiguously from sector 2.
    var nextSector = DataOriginSector;
    var layout = new int[this._files.Count];
    for (var i = 0; i < this._files.Count; i++) {
      layout[i] = nextSector;
      var len = this._files[i].Data.Length;
      var sectors = (len + SectorSize - 1) / SectorSize;
      if (sectors == 0) sectors = 0; // zero-length still gets a recorded start sector but no data sectors
      nextSector += sectors;
    }

    // Sanity bound — single-density Acorn discs cap at sector 0x3FF (1024) by
    // virtue of the packed-high-bits encoding.
    if (nextSector > 0x3FF)
      throw new InvalidOperationException(
        $"MFS-1: layout requires sector {nextSector}, exceeds 10-bit start-sector encoding (max 1023).");

    // Round image length up to a full track so it matches a real Acorn disc.
    var totalSectors = Math.Max(DataOriginSector, nextSector);
    if (totalSectors % SectorsPerTrack != 0)
      totalSectors += SectorsPerTrack - totalSectors % SectorsPerTrack;
    var image = new byte[totalSectors * SectorSize];

    // ── Title (12 chars: 8 in sector 0, 4 in sector 1, space-padded) ──────
    Span<byte> title = stackalloc byte[12];
    title.Fill((byte)' ');
    var titleBytes = Encoding.ASCII.GetBytes(this._title);
    var n = Math.Min(titleBytes.Length, 12);
    titleBytes.AsSpan(0, n).CopyTo(title);
    title[..8].CopyTo(image.AsSpan(0, 8));
    title.Slice(8, 4).CopyTo(image.AsSpan(SectorSize, 4));

    // ── Sector 1 byte 5: entry-count * 8 ──────────────────────────────────
    image[SectorSize + 5] = (byte)(this._files.Count * 8);

    // Sector 1 bytes 6-7: cycle number + (option, sector-count-high). We leave
    // option=0; sector count low at byte 7, high two bits at byte 6 bits 0-1.
    var totalLogicalSectors = (uint)totalSectors;
    image[SectorSize + 6] = (byte)((totalLogicalSectors >> 8) & 0x03);
    image[SectorSize + 7] = (byte)(totalLogicalSectors & 0xFF);

    // ── Catalog entries ───────────────────────────────────────────────────
    Span<byte> nameSpan = stackalloc byte[7];
    for (var i = 0; i < this._files.Count; i++) {
      var (name, dirLetter, data) = this._files[i];
      var nameOff = 8 + i * 8;
      var metaOff = SectorSize + 8 + i * 8;

      // 7-char filename, space-padded.
      nameSpan.Fill((byte)' ');
      Encoding.ASCII.GetBytes(name).AsSpan(0, Math.Min(7, name.Length)).CopyTo(nameSpan);
      nameSpan.CopyTo(image.AsSpan(nameOff, 7));
      // Directory letter (high bit cleared — unlocked by default).
      image[nameOff + 7] = (byte)(dirLetter & 0x7F);

      var loadAddr = 0u;
      var execAddr = 0u;
      var length = (uint)data.Length;
      var startSector = (uint)layout[i];

      image[metaOff + 0] = (byte)(loadAddr & 0xFF);
      image[metaOff + 1] = (byte)((loadAddr >> 8) & 0xFF);
      image[metaOff + 2] = (byte)(execAddr & 0xFF);
      image[metaOff + 3] = (byte)((execAddr >> 8) & 0xFF);
      image[metaOff + 4] = (byte)(length & 0xFF);
      image[metaOff + 5] = (byte)((length >> 8) & 0xFF);

      var startHi = (startSector >> 8) & 0x03;
      var loadHi = (loadAddr >> 16) & 0x03;
      var lenHi = (length >> 16) & 0x03;
      var execHi = (execAddr >> 16) & 0x03;
      image[metaOff + 6] = (byte)(startHi | (loadHi << 2) | (lenHi << 4) | (execHi << 6));
      image[metaOff + 7] = (byte)(startSector & 0xFF);

      // File data.
      if (data.Length > 0)
        Buffer.BlockCopy(data, 0, image, layout[i] * SectorSize, data.Length);
    }

    return image;
  }

  /// <summary>
  /// Splits an archive name like <c>"$.HELLO"</c> or <c>"A.PROG"</c> into a
  /// directory letter and bare filename. Names without a leading
  /// <c>letter.</c> prefix default to directory <c>$</c>.
  /// </summary>
  private static (char Dir, string Name) SplitDfsName(string archiveName) {
    var bare = Path.GetFileName(archiveName);
    if (bare.Length >= 2 && bare[1] == '.') return (bare[0], bare[2..]);
    return ('$', bare);
  }
}
