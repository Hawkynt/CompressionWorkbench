#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.Nrg;

/// <summary>
/// Low-level fixed-LBA sector editor for single-track NRG images whose ISO data
/// track begins at file offset zero. This is intentionally narrower than the
/// descriptor's public file-level R/W path: ordinary file CRUD uses a verified
/// extract/edit/re-create rebuild so ISO directory metadata stays coherent.
/// </summary>
public static class NrgInPlaceModifier {
  private const string Label = "NRG";
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;
  private const int Ner5FooterSize = 12;
  private const int NeroFooterSize = 8;

  private static ReadOnlySpan<byte> Sync => [
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
  ];

  /// <summary>
  /// Detected sector geometry. <see cref="DataOffset"/> is the byte offset of
  /// the 2,048-byte user-data region. <see cref="DataAreaLength"/> is the byte
  /// offset at which the NRG descriptor begins, not merely EOF minus the footer.
  /// </summary>
  public readonly record struct SectorGeometry(int SectorSize, int DataOffset, long DataAreaLength);

  private readonly record struct Trailer(long Offset, bool HasNrgFooter);

  /// <summary>
  /// Detects the single-track geometry from the ISO PVD at LBA 16. The NERO/
  /// NER5 footer's trailer pointer is honoured, so chunk metadata is never
  /// mistaken for sector data.
  /// </summary>
  public static SectorGeometry DetectGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("NRG sector editing requires a readable, seekable stream.", nameof(image));

    var trailer = ReadTrailer(image);
    var dataLength = trailer.Offset;
    if (TryProbe(image, RawSectorSize, Mode1DataOffset, dataLength))
      return new(RawSectorSize, Mode1DataOffset, dataLength);
    if (TryProbe(image, RawSectorSize, Mode2Form1DataOffset, dataLength))
      return new(RawSectorSize, Mode2Form1DataOffset, dataLength);
    if (TryProbe(image, SectorSize2336, 8, dataLength))
      return new(SectorSize2336, 8, dataLength);
    if (TryProbe(image, Iso9660SectorSize, 0, dataLength))
      return new(Iso9660SectorSize, 0, dataLength);

    throw new NotSupportedException(
      $"{Label}: the fixed-LBA editor supports only a single ISO data track beginning at file offset zero.");
  }

  private static Trailer ReadTrailer(Stream image) {
    if (image.Length >= Ner5FooterSize) {
      image.Position = image.Length - Ner5FooterSize;
      Span<byte> footer = stackalloc byte[Ner5FooterSize];
      if (ReadExactly(image, footer) && footer[..4].SequenceEqual("NER5"u8)) {
        var offset = BinaryPrimitives.ReadUInt64BigEndian(footer[4..]);
        if (offset <= (ulong)(image.Length - Ner5FooterSize))
          return new(checked((long)offset), true);
      }
    }

    if (image.Length >= NeroFooterSize) {
      image.Position = image.Length - NeroFooterSize;
      Span<byte> footer = stackalloc byte[NeroFooterSize];
      if (ReadExactly(image, footer) && footer[..4].SequenceEqual("NERO"u8)) {
        var offset = BinaryPrimitives.ReadUInt32BigEndian(footer[4..]);
        if (offset <= image.Length - NeroFooterSize)
          return new(offset, true);
      }
    }

    return new(image.Length, false);
  }

  private static bool TryProbe(Stream image, int sectorSize, int dataOffset, long dataAreaLength) {
    var pvdAt = (long)PvdLba * sectorSize + dataOffset;
    if (pvdAt > dataAreaLength - 6)
      return false;

    image.Position = pvdAt;
    Span<byte> signature = stackalloc byte[6];
    return ReadExactly(image, signature) &&
           signature[0] == 1 && signature[1..].SequenceEqual("CD001"u8);
  }

  /// <summary>
  /// Rewrites one existing 2,048-byte user-data sector. If the requested LBA
  /// would extend a real NRG image, the operation is refused because moving the
  /// descriptor requires rewriting ETN/DAO offsets and sizes as well.
  /// </summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData) {
    var geometry = DetectGeometry(image);
    WriteSector(image, lba, userData, geometry);
  }

  /// <summary>Rewrites a sector using an already detected geometry.</summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite || !image.CanSeek)
      throw new ArgumentException("NRG sector editing requires a writable, seekable stream.", nameof(image));
    if (lba < 0)
      throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException($"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.", nameof(userData));

    var sectorStart = checked((long)lba * geometry.SectorSize);
    var sectorEnd = checked(sectorStart + geometry.SectorSize);
    if (sectorEnd <= geometry.DataAreaLength) {
      image.Position = checked(sectorStart + geometry.DataOffset);
      image.Write(userData);
      return;
    }

    AppendSector(image, lba, userData, geometry);
  }

  /// <summary>
  /// Appends sectors only to a footer-less raw image. Extending an NRG image is
  /// not a byte-local operation because its descriptor records track offsets and
  /// sizes; callers wanting growth must use the descriptor's rebuild editor.
  /// </summary>
  public static void AppendSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0)
      throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException($"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.", nameof(userData));

    if (ReadTrailer(image).HasNrgFooter)
      throw new NotSupportedException(
        $"{Label}: extending a chunked image requires relocating and rewriting its track descriptor; use file-level rebuild modification instead.");

    var firstMissingLba = checked((int)((geometry.DataAreaLength + geometry.SectorSize - 1) / geometry.SectorSize));
    for (var current = firstMissingLba; current < lba; ++current) {
      image.Position = checked((long)current * geometry.SectorSize);
      WriteFramedSector(image, geometry, ReadOnlySpan<byte>.Empty);
    }

    image.Position = checked((long)lba * geometry.SectorSize);
    WriteFramedSector(image, geometry, userData);
    image.SetLength(checked((long)(lba + 1) * geometry.SectorSize));
  }

  private static void WriteFramedSector(Stream image, SectorGeometry geometry, ReadOnlySpan<byte> userData) {
    var sector = new byte[geometry.SectorSize];
    if (geometry.SectorSize == RawSectorSize) {
      Sync.CopyTo(sector);
      sector[15] = geometry.DataOffset == Mode2Form1DataOffset ? (byte)0x02 : (byte)0x01;
    }
    if (!userData.IsEmpty)
      userData.CopyTo(sector.AsSpan(geometry.DataOffset, Iso9660SectorSize));
    image.Write(sector);
  }

  /// <summary>Zeros one existing sector's 2,048-byte user-data region.</summary>
  public static bool ZeroSector(Stream image, int lba) {
    if (lba < 0)
      return false;
    var geometry = DetectGeometry(image);
    return ZeroSector(image, lba, geometry);
  }

  /// <summary>Zeros one existing sector using an already detected geometry.</summary>
  public static bool ZeroSector(Stream image, int lba, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0)
      return false;
    var sectorEnd = checked((long)lba * geometry.SectorSize + geometry.SectorSize);
    if (sectorEnd > geometry.DataAreaLength)
      return false;

    Span<byte> zeros = stackalloc byte[Iso9660SectorSize];
    image.Position = checked((long)lba * geometry.SectorSize + geometry.DataOffset);
    image.Write(zeros);
    return true;
  }

  /// <summary>Parses a synthetic <c>sector-NNNNNN.bin</c> LBA name.</summary>
  public static bool TryParseSectorEntryName(string entryName, out int lba) {
    lba = -1;
    if (string.IsNullOrEmpty(entryName))
      return false;

    var leaf = Path.GetFileName(entryName);
    const string prefix = "sector-";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
      return false;

    var numeric = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out lba) && lba >= 0;
  }

  /// <summary>Formats an LBA using the synthetic low-level sector namespace.</summary>
  public static string FormatSectorEntryName(int lba)
    => string.Create(CultureInfo.InvariantCulture, $"sector-{lba:D6}.bin");

  /// <summary>Rewrites sectors named through the synthetic low-level namespace.</summary>
  public static void AddOrReplaceSectors(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    var geometry = DetectGeometry(image);
    foreach (var (name, data) in inputs) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a fixed-LBA sector name. Use the descriptor's file-level modification path for ISO entries.");
      WriteSector(image, lba, data, geometry);
    }
  }

  /// <summary>Zeros sectors named through the synthetic low-level namespace.</summary>
  public static void RemoveSectors(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    var geometry = DetectGeometry(image);
    foreach (var name in entryNames) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a fixed-LBA sector name. Use the descriptor's file-level modification path for ISO entries.");
      _ = ZeroSector(image, lba, geometry);
    }
  }

  private static bool ReadExactly(Stream stream, Span<byte> buffer) {
    var offset = 0;
    while (offset < buffer.Length) {
      var read = stream.Read(buffer[offset..]);
      if (read == 0)
        return false;
      offset += read;
    }
    return true;
  }
}
