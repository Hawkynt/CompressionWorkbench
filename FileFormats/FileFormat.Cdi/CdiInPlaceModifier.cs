#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.Cdi;

/// <summary>
/// Low-level sector-rewrite helper retained for legacy CompressionWorkbench CDI
/// images whose body is one flat sector geometry followed by an 8-byte
/// version/zero trailer. Real DiscJuggler descriptors may describe multiple
/// sessions, pregaps and tracks with different stored sector sizes, so this API
/// refuses those images instead of pretending one global LBA-to-byte formula
/// exists.
/// </summary>
public static class CdiInPlaceModifier {
  private const string Label = "CDI";
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;

  private static readonly byte[] Sync = [
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
  ];

  /// <summary>
  /// Detected on-disk sector geometry for a legacy footer-only image.
  /// <see cref="DataAreaLength"/> excludes the old 8-byte trailer.
  /// </summary>
  public readonly record struct SectorGeometry(int SectorSize, int DataOffset, long DataAreaLength);

  /// <summary>
  /// Detects the flat sector geometry of a legacy footer-only CDI image.
  /// Descriptor-bearing DiscJuggler images are rejected because their geometry
  /// is per track; use <see cref="CdiReader.Tracks"/> and
  /// <see cref="CdiReader.ReadTrackSector"/> for those.
  /// </summary>
  public static SectorGeometry DetectGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureLegacyFlatLayout(image);

    var dataLen = DetectDataAreaLength(image);
    if (TryProbe(image, RawSectorSize, Mode1DataOffset, dataLen)) return new(RawSectorSize, Mode1DataOffset, dataLen);
    if (TryProbe(image, RawSectorSize, Mode2Form1DataOffset, dataLen)) return new(RawSectorSize, Mode2Form1DataOffset, dataLen);
    if (TryProbe(image, SectorSize2336, 8, dataLen)) return new(SectorSize2336, 8, dataLen);
    if (TryProbe(image, Iso9660SectorSize, 0, dataLen)) return new(Iso9660SectorSize, 0, dataLen);
    return new(RawSectorSize, Mode1DataOffset, dataLen);
  }

  private static void EnsureLegacyFlatLayout(Stream image) {
    if (CdiDescriptor.TryReadFooter(image, out var footer) && !footer.IsLegacyFooterOnly)
      throw new NotSupportedException(
        "CDI: flat-LBA sector editing is only valid for the legacy footer-only profile. " +
        "A real DiscJuggler descriptor can assign different stored sector sizes and pregaps per track.");
  }

  private static long DetectDataAreaLength(Stream image)
    => CdiDescriptor.TryReadFooter(image, out var footer) ? footer.DescriptorOffset : image.Length;

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

  /// <summary>Rewrites one 2,048-byte user-data sector in a legacy flat-layout image.</summary>
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

  /// <summary>Rewrites one sector using an already detected legacy geometry.</summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureLegacyFlatLayout(image);
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

  /// <summary>Extends a legacy footer-only CDI so that <paramref name="lba"/> exists.</summary>
  public static void AppendSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureLegacyFlatLayout(image);
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
        var read = image.Read(footer, got, footer.Length - got);
        if (read == 0) break;
        got += read;
      }
    }

    var firstMissingLba = checked((int)((geom.DataAreaLength + geom.SectorSize - 1) / geom.SectorSize));
    for (var i = firstMissingLba; i < lba; i++) {
      image.Position = (long)i * geom.SectorSize;
      WriteFramedSector(image, geom, ReadOnlySpan<byte>.Empty);
    }

    image.Position = (long)lba * geom.SectorSize;
    WriteFramedSector(image, geom, userData);

    var newDataEnd = (long)(lba + 1) * geom.SectorSize;
    if (footer != null) {
      image.SetLength(newDataEnd + footer.Length);
      image.Position = newDataEnd;
      image.Write(footer);
    } else {
      image.SetLength(newDataEnd);
    }
  }

  private static void WriteFramedSector(Stream image, SectorGeometry geom, ReadOnlySpan<byte> userData) {
    var sector = new byte[geom.SectorSize];
    if (geom.SectorSize == RawSectorSize) {
      Sync.AsSpan().CopyTo(sector.AsSpan(0, 12));
      sector[15] = (byte)(geom.DataOffset == Mode2Form1DataOffset ? 0x02 : 0x01);
    }
    if (!userData.IsEmpty)
      userData.CopyTo(sector.AsSpan(geom.DataOffset, Iso9660SectorSize));
    image.Write(sector);
  }

  /// <summary>Zeros one existing sector's 2,048-byte user-data region in a legacy flat image.</summary>
  public static bool ZeroSector(Stream image, int lba) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) return false;
    var geom = DetectGeometry(image);
    return ZeroSector(image, lba, geom);
  }

  /// <summary>Zeros one existing sector using an already detected legacy geometry.</summary>
  public static bool ZeroSector(Stream image, int lba, SectorGeometry geom) {
    ArgumentNullException.ThrowIfNull(image);
    EnsureLegacyFlatLayout(image);
    if (lba < 0) return false;
    var endOfSector = (long)lba * geom.SectorSize + geom.SectorSize;
    if (endOfSector > geom.DataAreaLength) return false;
    Span<byte> zeros = stackalloc byte[Iso9660SectorSize];
    zeros.Clear();
    image.Position = (long)lba * geom.SectorSize + geom.DataOffset;
    image.Write(zeros);
    return true;
  }

  /// <summary>Parses a synthetic <c>sector-NNNNNN.bin</c> low-level sector name.</summary>
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

  /// <summary>Formats a sector LBA as the low-level synthetic sector name.</summary>
  public static string FormatSectorEntryName(int lba)
    => string.Create(CultureInfo.InvariantCulture, $"sector-{lba:D6}.bin");

  /// <summary>Applies a sequence of low-level sector replacements to a legacy flat image.</summary>
  public static void AddOrReplaceSectors(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    var geom = DetectGeometry(image);
    foreach (var (name, data) in inputs) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a low-level sector address. Use 'sector-NNNNNN.bin' or the descriptor's file-level rebuild editor.");
      if (data.Length != Iso9660SectorSize)
        throw new ArgumentException(
          $"Sector entry '{name}' must carry exactly {Iso9660SectorSize} bytes; got {data.Length}.",
          nameof(inputs));
      WriteSector(image, lba, data, geom);
      geom = DetectGeometry(image);
    }
  }

  /// <summary>Zeros a sequence of low-level synthetic sector addresses in a legacy flat image.</summary>
  public static void RemoveSectors(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    var geom = DetectGeometry(image);
    foreach (var name in entryNames) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a low-level sector address. Use 'sector-NNNNNN.bin' or the descriptor's file-level rebuild editor.");
      ZeroSector(image, lba, geom);
    }
  }
}
