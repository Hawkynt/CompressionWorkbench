#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DvdisasterEcc;

/// <summary>The fixed header of a dvdisaster error-correction file.</summary>
public sealed record DvdisasterEccHeader(
  string Method, byte[] MethodFlags, byte[] MediumFingerprint, byte[] MediumMd5, byte[] EccMd5,
  long Sectors, int DataBytes, int EccBytes, int CreatorVersion, int NeededVersion, int FingerprintSector) {

  /// <summary>Header size: the ecc data starts after one 4 KiB block.</summary>
  public const int Size = 4096;

  /// <summary>Formats a dvdisaster version number (7910 = 0.79.10).</summary>
  public static string Version(int v) => $"{v / 10000}.{v / 100 % 100}" + (v % 100 != 0 ? $".{v % 100}" : "");

  public static DvdisasterEccHeader Read(ReadOnlySpan<byte> file) {
    if (file.Length < Size || !file.StartsWith("*dvdisaster*"u8))
      throw new InvalidDataException("dvdisaster ecc: missing the *dvdisaster* cookie.");
    return new DvdisasterEccHeader(
      Encoding.ASCII.GetString(file.Slice(12, 4)), file.Slice(16, 4).ToArray(), file.Slice(20, 16).ToArray(),
      file.Slice(36, 16).ToArray(), file.Slice(52, 16).ToArray(),
      BinaryPrimitives.ReadInt64LittleEndian(file[68..]), BinaryPrimitives.ReadInt32LittleEndian(file[76..]),
      BinaryPrimitives.ReadInt32LittleEndian(file[80..]), BinaryPrimitives.ReadInt32LittleEndian(file[84..]),
      BinaryPrimitives.ReadInt32LittleEndian(file[88..]), BinaryPrimitives.ReadInt32LittleEndian(file[92..]));
  }
}

/// <summary>
/// dvdisaster error-correction file (<c>.ecc</c>), read-only: the parity a disc image is repaired from.
/// It holds no files, so the view is its header as <c>metadata.ini</c> and, for the RS01 method, the
/// per-sector CRC table and the Reed-Solomon parity layers; RS03 files list their header and raw data.
/// Test checks the ecc MD5 the header records.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://dvdisaster.jcea.es/</c> — dvdisaster documentation, RS01/RS03 methods and the ecc header</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Header (little-endian, 4096 bytes): <c>"*dvdisaster*"</c>, method (<c>RS01</c>/<c>RS03</c>), 4 method flags,
/// MD5 of the fingerprint sector, MD5 of the image, MD5 of everything after the header, Int64 medium
/// sectors, Int32 data bytes and Int32 roots per codeword (they sum to 255), Int32 creator and needed
/// versions, Int32 fingerprint sector. RS01 then stores one CRC32 per medium sector and
/// roots × ceil(sectors / dataBytes) × 2048 bytes of parity.
/// </remarks>
public sealed class DvdisasterEccFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  private const int SectorSize = 2048;

  public string Id => "DvdisasterEcc";
  public string DisplayName => "dvdisaster ecc";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".ecc";
  public IReadOnlyList<string> Extensions => [".ecc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("*dvdisaster*"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("rs01", "RS01"), new("rs03", "RS03")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "dvdisaster error-correction file: header as metadata.ini, RS01 sector CRCs and parity (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => [.. Entries(ReadAll(stream)).Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length, "stored", false, false, null))];

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var (name, data) in Entries(ReadAll(stream)))
      if (files == null || MatchesFilter(name, files))
        WriteFile(outputDir, name, data);
  }

  /// <summary>Whether the data after the header still has the MD5 the header records.</summary>
  public static bool EccDataIntact(ReadOnlySpan<byte> file)
    => MD5.HashData(file[DvdisasterEccHeader.Size..]).AsSpan().SequenceEqual(DvdisasterEccHeader.Read(file).EccMd5);

  private static List<(string Name, byte[] Data)> Entries(byte[] file) {
    var entries = new List<(string Name, byte[] Data)>();
    var h = DvdisasterEccHeader.Read(file);
    if (!EccDataIntact(file))
      throw new InvalidDataException("dvdisaster ecc: the ecc data no longer has the MD5 its header records.");
    var ini = new StringBuilder()
      .Append("[dvdisaster]\n")
      .Append(CultureInfo.InvariantCulture, $"method={h.Method}\n")
      .Append(CultureInfo.InvariantCulture, $"medium_sectors={h.Sectors}\n")
      .Append(CultureInfo.InvariantCulture, $"data_bytes={h.DataBytes}\n")
      .Append(CultureInfo.InvariantCulture, $"roots={h.EccBytes}\n")
      .Append(CultureInfo.InvariantCulture, $"image_md5={Convert.ToHexStringLower(h.MediumMd5)}\n")
      .Append(CultureInfo.InvariantCulture, $"fingerprint_sector={h.FingerprintSector}\n")
      .Append(CultureInfo.InvariantCulture, $"fingerprint_md5={Convert.ToHexStringLower(h.MediumFingerprint)}\n")
      .Append(CultureInfo.InvariantCulture, $"ecc_md5={Convert.ToHexStringLower(h.EccMd5)}\n")
      .Append(CultureInfo.InvariantCulture, $"created_by=dvdisaster-{DvdisasterEccHeader.Version(h.CreatorVersion)}\n")
      .Append(CultureInfo.InvariantCulture, $"requires=dvdisaster-{DvdisasterEccHeader.Version(h.NeededVersion)}\n");
    entries.Add(("metadata.ini", Encoding.UTF8.GetBytes(ini.ToString())));

    var body = file.AsSpan(DvdisasterEccHeader.Size);
    if (h.Method == "RS01") {
      if (h.Sectors < 0 || h.DataBytes <= 0 || h.EccBytes <= 0 || h.DataBytes + h.EccBytes != 255)
        throw new InvalidDataException($"dvdisaster ecc: RS01 with {h.DataBytes} data bytes and {h.EccBytes} roots.");
      var crcLength = h.Sectors * 4;
      var layers = (h.Sectors + h.DataBytes - 1) / h.DataBytes;
      var parityLength = h.EccBytes * layers * SectorSize;
      if (crcLength + parityLength != body.Length)
        throw new InvalidDataException($"dvdisaster ecc: RS01 for {h.Sectors} sectors needs {crcLength + parityLength} bytes after the header, the file has {body.Length}.");
      entries.Add(("sector-crc32.bin", body[..(int)crcLength].ToArray()));
      entries.Add(("parity.bin", body[(int)crcLength..].ToArray()));
    } else {
      entries.Add(("ecc.bin", body.ToArray()));
    }
    return entries;
  }

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
