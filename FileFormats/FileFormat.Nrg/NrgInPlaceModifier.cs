#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.Nrg;

/// <summary>
/// In-place sector-rewrite modifier for a Nero Burning ROM NRG disc image.
/// Operates at the raw 2 048-byte user-data region of each CD sector at the
/// fixed byte offset <c>lba * sectorSize + dataOffset</c>, where
/// <c>sectorSize</c> and <c>dataOffset</c> are the geometry detected from the
/// data area (raw 2 352 Mode 1, raw 2 352 Mode 2 Form 1, 2 336-byte sectors,
/// or flat 2 048-byte cooked sectors).
///
/// <para><b>NRG framing.</b> An NRG image is a stream of CD sectors followed
/// by a footer at EOF identifying the format version:
/// <list type="bullet">
///   <item>NRG v2: last 12 bytes — "NER5" + uint64 BE chunk-table offset.</item>
///   <item>NRG v1: last 8 bytes — "NERO" + uint32 BE chunk-table offset.</item>
/// </list>
/// The footer is preserved byte-identical across in-place rewrites and is
/// relocated past the new EOF whenever the data area grows.</para>
///
/// <para><b>Scope.</b> Rewrites only the user-data bytes inside an existing
/// sector or appends a brand-new sector at the end of the data area. It does
/// <i>not</i> understand the inner ISO 9660 directory structure — that is the
/// job of <see cref="FileSystem.Iso.IsoWriter"/> / its reader. Synthetic entry
/// names of the form <c>sector-NNNNNN.bin</c> address a single sector LBA.
/// Multi-track DAOI/CUEX layouts are not parsed — the modifier treats the
/// stream as a single track of sectors at flat LBA offsets. Sync pattern
/// (12 B), 3-byte address, 1-byte mode, and the EDC/ECC tail of raw sectors
/// are preserved on rewrite and synthesised on append.</para>
///
/// <para><b>True in-place.</b> Writes touch only the 2 048-byte user-data
/// region of the targeted sector. Bytes outside that region — header bytes of
/// the same sector, every untouched sector, the system area (LBA 0-15), the
/// PVD at LBA 16, the ISO root directory, and the trailing NRG footer — stay
/// byte-identical at their original byte offsets (the footer migrates to
/// follow the new EOF when the data area grows).</para>
/// </summary>
public static class NrgInPlaceModifier {

  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  private const int Ner5FooterSize = 12;
  private const int NeroFooterSize = 8;

  private static readonly byte[] MagicNer5 = [(byte)'N', (byte)'E', (byte)'R', (byte)'5'];
  private static readonly byte[] MagicNero = [(byte)'N', (byte)'E', (byte)'R', (byte)'O'];

  /// <summary>The 12-byte CD-ROM sync pattern (00 FF*10 00) prefixed to every
  /// raw 2 352-byte sector.</summary>
  private static readonly byte[] Sync = [
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
  ];

  /// <summary>
  /// Detected on-disk sector geometry for an NRG image. <see cref="DataOffset"/>
  /// is the byte offset within a sector where the 2 048 B of ISO user data
  /// begins. <see cref="DataAreaLength"/> excludes the trailing NRG footer
  /// (12 bytes for v2, 8 bytes for v1) when present.
  /// </summary>
  public readonly record struct SectorGeometry(int SectorSize, int DataOffset, long DataAreaLength);

  /// <summary>
  /// Detects the sector geometry of <paramref name="image"/> the same way
  /// <see cref="NrgReader"/> does — by probing for the <c>CD001</c> PVD
  /// signature at LBA 16 inside the data area. Falls back to raw Mode 1
  /// (2 352 / 16) when no probe succeeds. NRG v2 ("NER5") and v1 ("NERO")
  /// footers are excluded from the data area.
  /// </summary>
  public static SectorGeometry DetectGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var dataLen = DetectDataAreaLength(image);
    if (TryProbe(image, RawSectorSize, Mode1DataOffset, dataLen)) return new(RawSectorSize, Mode1DataOffset, dataLen);
    if (TryProbe(image, RawSectorSize, Mode2Form1DataOffset, dataLen)) return new(RawSectorSize, Mode2Form1DataOffset, dataLen);
    if (TryProbe(image, SectorSize2336, 8, dataLen)) return new(SectorSize2336, 8, dataLen);
    if (TryProbe(image, Iso9660SectorSize, 0, dataLen)) return new(Iso9660SectorSize, 0, dataLen);
    return new(RawSectorSize, Mode1DataOffset, dataLen);
  }

  private static long DetectDataAreaLength(Stream image) {
    // Probe NER5 first (longer footer).
    if (image.Length >= Ner5FooterSize) {
      image.Position = image.Length - Ner5FooterSize;
      Span<byte> tail = stackalloc byte[Ner5FooterSize];
      if (image.Read(tail) == Ner5FooterSize &&
          tail[0] == MagicNer5[0] && tail[1] == MagicNer5[1] &&
          tail[2] == MagicNer5[2] && tail[3] == MagicNer5[3])
        return image.Length - Ner5FooterSize;
    }
    if (image.Length >= NeroFooterSize) {
      image.Position = image.Length - NeroFooterSize;
      Span<byte> tail = stackalloc byte[NeroFooterSize];
      if (image.Read(tail) == NeroFooterSize &&
          tail[0] == MagicNero[0] && tail[1] == MagicNero[1] &&
          tail[2] == MagicNero[2] && tail[3] == MagicNero[3])
        return image.Length - NeroFooterSize;
    }
    return image.Length;
  }

  private static bool TryProbe(Stream image, int sectorSize, int dataOffset, long dataAreaLength) {
    var pvdAt = (long)PvdLba * sectorSize + dataOffset;
    if (pvdAt + 6 > dataAreaLength) return false;
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
  /// sector, every other region of the image, and the trailing NRG footer —
  /// are untouched. If <paramref name="lba"/> points past the current
  /// data-area EOF, the image is grown sector-by-sector with appended-sector
  /// framing (<see cref="AppendSector"/>) and the footer is relocated to the
  /// new EOF.
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

    if (endOfSector <= geom.DataAreaLength) {
      image.Position = sectorStart + geom.DataOffset;
      image.Write(userData);
      return;
    }

    AppendSector(image, lba, userData, geom);
  }

  /// <summary>
  /// Extends the data area so that sector <paramref name="lba"/> exists,
  /// writing <paramref name="userData"/> as its 2 048-byte payload.
  /// Intermediate sectors are appended with format-correct framing. The
  /// trailing NRG footer is preserved verbatim and rewritten at the new EOF.
  /// </summary>
  public static void AppendSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.",
        nameof(userData));

    byte[]? footer = null;
    if (geom.DataAreaLength < image.Length) {
      footer = new byte[image.Length - geom.DataAreaLength];
      image.Position = geom.DataAreaLength;
      var got = 0;
      while (got < footer.Length) {
        var r = image.Read(footer, got, footer.Length - got);
        if (r == 0) break;
        got += r;
      }
    }

    var firstMissingLba = (int)((geom.DataAreaLength + geom.SectorSize - 1) / geom.SectorSize);
    var lastLbaToWrite = lba;

    for (var i = firstMissingLba; i < lastLbaToWrite; i++) {
      image.Position = (long)i * geom.SectorSize;
      WriteFramedSector(image, geom, ReadOnlySpan<byte>.Empty);
    }

    image.Position = (long)lba * geom.SectorSize;
    WriteFramedSector(image, geom, userData);

    if (footer != null) {
      var newDataEnd = (long)(lba + 1) * geom.SectorSize;
      image.SetLength(newDataEnd + footer.Length);
      image.Position = newDataEnd;
      image.Write(footer);
    } else {
      image.SetLength((long)(lba + 1) * geom.SectorSize);
    }
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
  /// in place. The sector framing bytes and the trailing NRG footer are
  /// preserved; only the user data is wiped. Returns <c>true</c> if the
  /// sector existed (and was zeroed), <c>false</c> if <paramref name="lba"/>
  /// is past the data-area EOF.
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
    if (endOfSector > geom.DataAreaLength) return false;
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
  /// Formats a sector LBA into the synthetic entry name used by the
  /// in-place modifier.
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
      geom = DetectGeometry(image);
    }
  }

  /// <summary>
  /// Zeros each named <c>sector-NNNNNN.bin</c>. Names that don't match the
  /// schema are silently skipped; sectors past the data-area EOF are likewise
  /// skipped. The framing bytes of an existing sector and the trailing NRG
  /// footer are preserved.
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
