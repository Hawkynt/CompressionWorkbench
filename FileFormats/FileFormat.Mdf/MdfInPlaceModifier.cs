#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;

namespace FileFormat.Mdf;

/// <summary>
/// Low-level sector access for Alcohol 120% MDF data streams.
/// Existing raw sectors are rewritten as complete sectors so their ECMA-130
/// EDC/ECC remains consistent with the changed 2 048-byte user-data field.
/// </summary>
public static class MdfInPlaceModifier {

  private const string Label = "MDF";
  internal const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int SectorSize2336 = 2336;
  private const int PvdLba = 16;
  private const int Mode1DataOffset = 16;
  private const int Mode2Form1DataOffset = 24;
  private const uint EdcPolynomial = 0xD8018001u;

  private static readonly byte[] Sync = [
    0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
  ];

  private static readonly uint[] EdcTable = BuildEdcTable();
  private static readonly byte[] EccForward = new byte[256];
  private static readonly byte[] EccBackward = new byte[256];

  static MdfInPlaceModifier() {
    for (var i = 0; i < 256; ++i) {
      var doubled = i << 1;
      if ((i & 0x80) != 0)
        doubled ^= 0x11D;
      EccForward[i] = (byte)doubled;
      EccBackward[(byte)(i ^ doubled)] = (byte)i;
    }
  }

  /// <summary>Detected physical sector geometry of an MDF data stream.</summary>
  public readonly record struct SectorGeometry(int SectorSize, int DataOffset) {
    /// <summary>Whether sectors carry ECMA-130 framing around the 2 048-byte payload.</summary>
    public bool IsRaw => this.SectorSize != Iso9660SectorSize;
  }

  /// <summary>
  /// Detects the sector geometry by probing the ISO 9660 PVD at LBA 16.
  /// When no PVD exists, an exact sector-size divisibility check is used before
  /// retaining the historical raw-Mode-1 fallback.
  /// </summary>
  public static SectorGeometry DetectGeometry(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanRead)
      throw new ArgumentException("MDF geometry detection requires a readable, seekable stream.", nameof(image));

    var originalPosition = image.Position;
    try {
      if (TryProbe(image, RawSectorSize, Mode1DataOffset)) return new(RawSectorSize, Mode1DataOffset);
      if (TryProbe(image, RawSectorSize, Mode2Form1DataOffset)) return new(RawSectorSize, Mode2Form1DataOffset);
      if (TryProbe(image, SectorSize2336, 8)) return new(SectorSize2336, 8);
      if (TryProbe(image, Iso9660SectorSize, 0)) return new(Iso9660SectorSize, 0);

      if (image.Length > 0) {
        if (image.Length % RawSectorSize == 0) return new(RawSectorSize, Mode1DataOffset);
        if (image.Length % SectorSize2336 == 0) return new(SectorSize2336, 8);
        if (image.Length % Iso9660SectorSize == 0) return new(Iso9660SectorSize, 0);
      }
      return new(RawSectorSize, Mode1DataOffset);
    } finally {
      image.Position = originalPosition;
    }
  }

  private static bool TryProbe(Stream image, int sectorSize, int dataOffset) {
    var pvdAt = (long)PvdLba * sectorSize + dataOffset;
    if (pvdAt + 6 > image.Length) return false;
    image.Position = pvdAt;
    Span<byte> sig = stackalloc byte[6];
    if (image.Read(sig) < sig.Length) return false;
    ReadOnlySpan<byte> primaryVolumeDescriptor = [1, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1'];
    return sig.SequenceEqual(primaryVolumeDescriptor);
  }

  /// <summary>Reads one 2 048-byte user-data sector.</summary>
  internal static void ReadSector(Stream image, long lba, Span<byte> userData, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException($"Sector buffer must be exactly {Iso9660SectorSize} bytes.", nameof(userData));

    var dataStart = checked(lba * geometry.SectorSize + geometry.DataOffset);
    if (dataStart + Iso9660SectorSize > image.Length)
      throw new EndOfStreamException($"MDF sector {lba} lies past the end of the image.");
    image.Position = dataStart;
    image.ReadExactly(userData);
  }

  /// <summary>
  /// Rewrites one 2 048-byte payload. Raw Mode 1 / Mode 2 Form 1 sectors have
  /// their EDC and P/Q parity regenerated according to ECMA-130 / ECMA-168.
  /// </summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData) {
    ArgumentNullException.ThrowIfNull(image);
    WriteSector(image, lba, userData, DetectGeometry(image));
  }

  /// <summary>Writes a payload using a previously detected geometry.</summary>
  public static void WriteSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite)
      throw new ArgumentException("MDF sector writes require a writable, seekable stream.", nameof(image));
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.", nameof(userData));

    var sectorStart = (long)lba * geometry.SectorSize;
    if (sectorStart + geometry.SectorSize > image.Length) {
      AppendSector(image, lba, userData, geometry);
      return;
    }

    if (!geometry.IsRaw) {
      image.Position = sectorStart;
      image.Write(userData);
      return;
    }

    var sector = new byte[geometry.SectorSize];
    image.Position = sectorStart;
    image.ReadExactly(sector);
    userData.CopyTo(sector.AsSpan(geometry.DataOffset, Iso9660SectorSize));
    StampIntegrity(sector, geometry);
    image.Position = sectorStart;
    image.Write(sector);
  }

  /// <summary>
  /// Extends the MDF until <paramref name="lba"/> exists. Newly synthesised raw
  /// sectors receive a valid sync/header/subheader and ECMA-130 EDC/ECC.
  /// </summary>
  public static void AppendSector(Stream image, int lba, ReadOnlySpan<byte> userData, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanSeek || !image.CanWrite)
      throw new ArgumentException("MDF sector appends require a writable, seekable stream.", nameof(image));
    if (lba < 0) throw new ArgumentOutOfRangeException(nameof(lba));
    if (userData.Length != Iso9660SectorSize)
      throw new ArgumentException(
        $"Sector user data must be exactly {Iso9660SectorSize} bytes; got {userData.Length}.", nameof(userData));
    if (image.Length % geometry.SectorSize != 0)
      throw new InvalidDataException("MDF ends in a partial physical sector; appending would make its geometry ambiguous.");

    var firstMissingLba = checked((int)(image.Length / geometry.SectorSize));
    for (var current = firstMissingLba; current <= lba; ++current) {
      var payload = current == lba ? userData : ReadOnlySpan<byte>.Empty;
      image.Position = (long)current * geometry.SectorSize;
      WriteFramedSector(image, current, geometry, payload);
    }
  }

  private static void WriteFramedSector(Stream image, int lba, SectorGeometry geometry, ReadOnlySpan<byte> userData) {
    var sector = new byte[geometry.SectorSize];
    if (geometry.SectorSize == RawSectorSize) {
      Sync.CopyTo(sector, 0);
      WriteMsfAddress(sector, lba);
      sector[15] = geometry.DataOffset == Mode2Form1DataOffset ? (byte)2 : (byte)1;
      if (geometry.DataOffset == Mode2Form1DataOffset) {
        sector[18] = 0x08; // XA submode: data
        sector[22] = 0x08; // duplicated subheader
      }
    } else if (geometry.SectorSize == SectorSize2336) {
      sector[2] = 0x08;
      sector[6] = 0x08;
    }

    if (!userData.IsEmpty)
      userData.CopyTo(sector.AsSpan(geometry.DataOffset, Iso9660SectorSize));
    if (geometry.IsRaw)
      StampIntegrity(sector, geometry);
    image.Write(sector);
  }

  private static void WriteMsfAddress(Span<byte> sector, int lba) {
    var absolute = checked(lba + 150);
    sector[12] = ToBcd(absolute / (75 * 60));
    sector[13] = ToBcd(absolute / 75 % 60);
    sector[14] = ToBcd(absolute % 75);
  }

  private static byte ToBcd(int value) => checked((byte)(((value / 10) << 4) | value % 10));

  private static void StampIntegrity(Span<byte> sector, SectorGeometry geometry) {
    if (geometry.SectorSize == RawSectorSize && geometry.DataOffset == Mode1DataOffset) {
      StampMode1(sector);
      return;
    }
    if (geometry.SectorSize == RawSectorSize && geometry.DataOffset == Mode2Form1DataOffset) {
      StampMode2Form1(sector);
      return;
    }
    if (geometry.SectorSize == SectorSize2336 && geometry.DataOffset == 8) {
      Span<byte> full = stackalloc byte[RawSectorSize];
      sector.CopyTo(full[16..]);
      StampMode2Form1(full);
      full[16..].CopyTo(sector);
      return;
    }
    throw new NotSupportedException(
      $"MDF raw sector geometry {geometry.SectorSize}/{geometry.DataOffset} cannot be parity-stamped safely.");
  }

  private static void StampMode1(Span<byte> sector) {
    BinaryPrimitives.WriteUInt32LittleEndian(sector[2064..2068], ComputeEdc(sector[..2064]));
    sector[2068..2076].Clear();
    StampEcc(sector, zeroAddress: false);
  }

  private static void StampMode2Form1(Span<byte> sector) {
    BinaryPrimitives.WriteUInt32LittleEndian(sector[2072..2076], ComputeEdc(sector[16..2072]));
    StampEcc(sector, zeroAddress: true);
  }

  private static void StampEcc(Span<byte> sector, bool zeroAddress) {
    Span<byte> savedAddress = stackalloc byte[4];
    if (zeroAddress) {
      sector[12..16].CopyTo(savedAddress);
      sector[12..16].Clear();
    }

    try {
      ComputeEccBlock(sector[12..2076], 86, 24, 2, 86, sector[2076..2248]);
      // Q covers the P bytes, so P must already be present before this pass.
      ComputeEccBlock(sector[12..2248], 52, 43, 86, 88, sector[2248..2352]);
    } finally {
      if (zeroAddress)
        savedAddress.CopyTo(sector[12..16]);
    }
  }

  private static void ComputeEccBlock(
      ReadOnlySpan<byte> source,
      int majorCount,
      int minorCount,
      int majorMultiplier,
      int minorIncrement,
      Span<byte> destination) {
    var size = majorCount * minorCount;
    if (source.Length < size || destination.Length < majorCount * 2)
      throw new ArgumentException("ECC block buffers are shorter than the ECMA-130 matrix requires.");

    for (var major = 0; major < majorCount; ++major) {
      var index = (major >> 1) * majorMultiplier + (major & 1);
      byte a = 0;
      byte b = 0;
      for (var minor = 0; minor < minorCount; ++minor) {
        var value = source[index];
        index += minorIncrement;
        if (index >= size) index -= size;
        a ^= value;
        b ^= value;
        a = EccForward[a];
      }
      a = EccBackward[EccForward[a] ^ b];
      destination[major] = a;
      destination[major + majorCount] = (byte)(a ^ b);
    }
  }

  private static uint ComputeEdc(ReadOnlySpan<byte> bytes) {
    uint edc = 0;
    foreach (var value in bytes)
      edc = edc >> 8 ^ EdcTable[(byte)(edc ^ value)];
    return edc;
  }

  private static uint[] BuildEdcTable() {
    var result = new uint[256];
    for (var i = 0; i < result.Length; ++i) {
      var value = (uint)i;
      for (var bit = 0; bit < 8; ++bit)
        value = value >> 1 ^ ((value & 1) != 0 ? EdcPolynomial : 0);
      result[i] = value;
    }
    return result;
  }

  /// <summary>Zeros one existing user-data sector and regenerates raw parity.</summary>
  public static bool ZeroSector(Stream image, int lba) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) return false;
    return ZeroSector(image, lba, DetectGeometry(image));
  }

  /// <summary>Zeros one existing user-data sector using a cached geometry.</summary>
  public static bool ZeroSector(Stream image, int lba, SectorGeometry geometry) {
    ArgumentNullException.ThrowIfNull(image);
    if (lba < 0) return false;
    if ((long)(lba + 1) * geometry.SectorSize > image.Length) return false;
    WriteSector(image, lba, new byte[Iso9660SectorSize], geometry);
    return true;
  }

  /// <summary>Parses <c>sector-NNNNNN.bin</c> into its LBA.</summary>
  public static bool TryParseSectorEntryName(string entryName, out int lba) {
    lba = -1;
    if (string.IsNullOrEmpty(entryName)) return false;
    var leaf = Path.GetFileName(entryName);
    const string prefix = "sector-";
    const string suffix = ".bin";
    if (!leaf.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
      return false;
    var numeric = leaf.AsSpan(prefix.Length, leaf.Length - prefix.Length - suffix.Length);
    return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out lba) && lba >= 0;
  }

  /// <summary>Formats a synthetic sector name retained for low-level callers.</summary>
  public static string FormatSectorEntryName(int lba)
    => string.Create(CultureInfo.InvariantCulture, $"sector-{lba:D6}.bin");

  /// <summary>
  /// Low-level sector replacement API retained for callers that explicitly work
  /// in LBAs. The format descriptor itself now exposes file-level ISO 9660 edits.
  /// </summary>
  public static void AddOrReplaceSectors(Stream image, IEnumerable<(string ArchiveName, byte[] Data)> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    var geometry = DetectGeometry(image);
    foreach (var (name, data) in inputs) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a low-level sector name ('sector-NNNNNN.bin').");
      if (data.Length != Iso9660SectorSize)
        throw new ArgumentException(
          $"Sector entry '{name}' must carry exactly {Iso9660SectorSize} bytes; got {data.Length}.", nameof(inputs));
      WriteSector(image, lba, data, geometry);
    }
  }

  /// <summary>Low-level sector wipe API retained for explicit LBA callers.</summary>
  public static void RemoveSectors(Stream image, IEnumerable<string> entryNames) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entryNames);
    var geometry = DetectGeometry(image);
    foreach (var name in entryNames) {
      if (!TryParseSectorEntryName(name, out var lba))
        throw new NotSupportedException(
          $"{Label}: '{name}' is not a low-level sector name ('sector-NNNNNN.bin').");
      ZeroSector(image, lba, geometry);
    }
  }
}
