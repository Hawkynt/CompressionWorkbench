#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.Mdf;

/// <summary>
/// In-place sector-rewrite modifier for an Alcohol 120% MDF disc image.
/// Operates at the raw 2 048-byte user-data region of each CD sector at the
/// fixed byte offset <c>lba * sectorSize + dataOffset</c>, where
/// <c>sectorSize</c> and <c>dataOffset</c> are the geometry detected from the
/// stream (raw 2 352 Mode 1, raw 2 352 Mode 2 Form 1, 2 336-byte sectors, or
/// flat 2 048-byte cooked sectors).
///
/// <para><b>MDF framing.</b> Alcohol 120% pairs an <c>.mdf</c> sector-stream
/// with an <c>.mds</c> metadata sidecar that describes the track layout. The
/// MDF itself has <i>no</i> internal header or footer — it is a flat byte
/// stream of sectors at LBA × sectorSize. The MDS sidecar is not touched by
/// this modifier (the in-place surface only mutates the MDF data); the
/// reader's geometry detection survives any sector rewrite.</para>
///
/// <para><b>Scope.</b> Rewrites only the user-data bytes inside an existing
/// sector or appends a brand-new sector at the end of the stream. It does
/// <i>not</i> understand the inner ISO 9660 directory structure — that is the
/// job of <see cref="FileSystem.Iso.IsoWriter"/> / its reader. Synthetic entry
/// names of the form <c>sector-NNNNNN.bin</c> address a single sector LBA.
/// Multi-track layouts (described in the companion <c>.mds</c>) are not
/// parsed — the modifier treats the stream as a single track of sectors at
/// flat LBA offsets. Sync pattern (12 B), 3-byte address, 1-byte mode, and
/// the EDC/ECC tail of raw sectors are preserved on rewrite and synthesised
/// on append.</para>
///
/// <para><b>True in-place.</b> Writes touch only the 2 048-byte user-data
/// region of the targeted sector. Bytes outside that region — header bytes of
/// the same sector, every untouched sector, the system area (LBA 0-15), the
/// PVD at LBA 16, and the ISO root directory — stay byte-identical at their
/// original byte offsets.</para>
/// </summary>
public static class MdfInPlaceModifier {

  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  /// <summary>The 12-byte CD-ROM sync pattern (00 FF*10 00) prefixed to every
  /// raw 2 352-byte sector.</summary>
  private static readonly byte[] Sync = [
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
  ];

  /// <summary>
  /// Detected on-disk sector geometry for an MDF image. <see cref="DataOffset"/>
  /// is the byte offset within a sector where the 2 048 B of ISO user data
  /// begins.
  /// </summary>
  public readonly record struct SectorGeometry(int SectorSize, int DataOffset);

  /// <summary>
  /// Detects the sector geometry of <paramref name="image"/> the same way
  /// <see cref="MdfReader"/> does — by probing for the <c>CD001</c> PVD
  /// signature at LBA 16. Falls back to raw Mode 1 (2 352 / 16) when no probe
  /// succeeds.
  /// </summary>
  public static SectorGeometry DetectGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (TryProbe(image, RawSectorSize, Mode1DataOffset)) return new(RawSectorSize, Mode1DataOffset);
    if (TryProbe(image, RawSectorSize, Mode2Form1DataOffset)) return new(RawSectorSize, Mode2Form1DataOffset);
    if (TryProbe(image, SectorSize2336, 8)) return new(SectorSize2336, 8);
    if (TryProbe(image, Iso9660SectorSize, 0)) return new(Iso9660SectorSize, 0);
    return new(RawSectorSize, Mode1DataOffset);
  }

  private static bool TryProbe(Stream image, int sectorSize, int dataOffset) {
    var pvdAt = (long)PvdLba * sectorSize + dataOffset;
    if (pvdAt + 6 > image.Length) return false;
    image.Position = pvdAt;
    Span<byte> sig = stackalloc byte[6];
    var read = image.Read(sig);
    if (read < 6) return false;
    return sig[0] == 1 && sig[1] == (byte)'C' && sig[2] == (byte)'D' &&
           sig[3] == (byte)'0' && sig[4] == (byte)'0' && sig[5] == (byte)'1';
  }

  /// <summary>
  /// Rewrites the 2 048-byte user-data region of sector <paramref name="lba"/>
  /// in place. Other bytes — sync/header/EDC for raw sectors, every other
  /// sector — are untouched. If <paramref name="lba"/> points past current
  /// EOF, the image is grown sector-by-sector with appended-sector framing
  /// (<see cref="AppendSector"/>).
  /// </summary>
  /// <exception cref="ArgumentException">When <paramref name="userData"/>.Length
  /// differs from 2 048.</exception>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.",
        nameof(userData));
    var geom = DetectGeometry(image);
    WriteSector(image, lba, userData, geom);
  }

  /// <summary>
  /// Variant of <see cref="WriteSector(Stream,int,ReadOnlySpan{byte})"/> that
  /// reuses a previously-probed geometry, avoiding a redundant PVD probe per
  /// call when a caller is rewriting several sectors back-to-back.
  /// </summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.",
        nameof(userData));

    var sectorStart = (long)lba * geom.SectorSize;
    var endOfSector = sectorStart + geom.SectorSize;

    if (endOfSector <= image.Length) {
      image.Position = sectorStart + geom.DataOffset;
      image.Write(userData);
      return;
    }

    AppendSector(image, lba, userData, geom);
  }

  /// <summary>
  /// Extends the image so that sector <paramref name="lba"/> exists, writing
  /// <paramref name="userData"/> as its 2 048-byte payload. Intermediate
  /// sectors are appended with format-correct framing.
  /// </summary>
  public static void AppendSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.",
        nameof(userData));

    var firstMissingLba = (int)((image.Length + geom.SectorSize - 1) / geom.SectorSize);
    var lastLbaToWrite = lba;

    for (var i = firstMissingLba; i < lastLbaToWrite; i++) {
      image.Position = (long)i * geom.SectorSize;
      WriteFramedSector(image, geom, ReadOnlySpan<byte>.Empty);
    }

    image.Position = (long)lba * geom.SectorSize;
    WriteFramedSector(image, geom, userData);
  }

  private static void WriteFramedSector(Stream image, SectorGeometry geom, ReadOnlySpan<byte> userData) {
    var sector = new byte[geom.SectorSize];
    switch (geom.SectorSize) {
      case RawSectorSize:
        Sync.AsSpan().CopyTo(sector.AsSpan(0, 12));
        sector[15] = (byte)(geom.DataOffset == Mode2Form1DataOffset ? 0x02 : 0x01);
        break;
      case SectorSize2336:
        break;
    }
    if (!userData.IsEmpty)
      userData.CopyTo(sector.AsSpan(geom.DataOffset, Iso9660SectorSize));
    image.Write(sector);
  }

  /// <summary>
  /// Zeros the 2 048-byte user-data region of sector <paramref name="lba"/>
  /// in place. The sector framing bytes are preserved; only the user data is
  /// wiped. Returns <c>true</c> if the sector existed (and was zeroed),
  /// <c>false</c> if <paramref name="lba"/> is past EOF.
  /// </summary>
  public static bool ZeroSector(Stream image, int lba) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) return false;
    var geom = DetectGeometry(image);
    return ZeroSector(image, lba, geom);
  }

  /// <summary>
  /// Variant of <see cref="ZeroSector(Stream,int)"/> reusing a previously-probed geometry.
  /// </summary>
  public static bool ZeroSector(Stream image, int lba, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) return false;
    var endOfSector = (long)lba * geom.SectorSize + geom.SectorSize;
    if (endOfSector > image.Length) return false;
    var zeros = new byte[Iso9660SectorSize];
    image.Position = (long)lba * geom.SectorSize + geom.DataOffset;
    image.Write(zeros);
    return true;
  }

  /// <summary>
  /// Parses a synthetic <c>sector-NNNNNN.bin</c> entry name and returns the
  /// embedded sector LBA. Names that don't match the schema return
  /// <c>false</c>.
  /// </summary>
  public static bool TryParseSectorEntryName(string entryName, out int lba) {
    lba = -1;
    if (string.IsNullOrEmpty(entryName)) return false;
    var leaf = Path.GetFileName(entryName);
    const string prefix = "sector-";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
    if (!leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
    var numeric = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out lba) && lba >= 0;
  }

  /// <summary>
  /// Formats a sector LBA into the synthetic entry name used by the in-place
  /// modifier.
  /// </summary>
  public static string FormatSectorEntryName(int lba)
    => string.Create(CultureInfo.InvariantCulture, $"sector-{lba:D6}.bin");

  /// <summary>
  /// Routes each input through the sector-rewrite path. Inputs whose
  /// <c>ArchiveName</c> matches <c>sector-NNNNNN.bin</c> are written at the
  /// fixed LBA byte offset. Inputs whose <c>ArchiveName</c> doesn't match the
  /// schema are silently skipped — inner ISO 9660 directory mutation is
  /// delegated to <c>FileSystem.Iso</c>.
  /// </summary>
  public static void AddOrReplaceSectors(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    var geom = DetectGeometry(image);
    foreach (var (name, data) in inputs) {
      if (!TryParseSectorEntryName(name, out var lba)) continue;
      if (data.Length != Iso9660SectorSize)
        throw new ArgumentException(
          $"Sector entry '{name}' must carry exactly {Iso9660SectorSize} bytes; got {data.Length}.",
          nameof(inputs));
      WriteSector(image, lba, data, geom);
    }
  }

  /// <summary>
  /// Zeros each named <c>sector-NNNNNN.bin</c>. Names that don't match the
  /// schema are silently skipped; sectors past EOF are likewise skipped. The
  /// framing bytes of an existing sector are preserved.
  /// </summary>
  public static void RemoveSectors(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    var geom = DetectGeometry(image);
    foreach (var name in entryNames) {
      if (!TryParseSectorEntryName(name, out var lba)) continue;
      ZeroSector(image, lba, geom);
    }
  }
}
