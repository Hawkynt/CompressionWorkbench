#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ExePackers;

/// <summary>
/// Pseudo-archive descriptor for PKLITE-packed DOS executables. PKLITE was a
/// commercial DOS exe-compressor by PKWARE (1990); the unpacker stub it
/// prepends carries the distinctive <c>"PKLITE Copr."</c> copyright string and
/// a version field at MZ-header offset <c>0x1C</c>.
/// </summary>
public sealed class PkLiteFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "PkLite";
  public string DisplayName => "PKLITE (DOS exe)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".exe";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  // PKLITE is identified by its embedded copyright string (no leading magic).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "PKLITE-packed DOS executable — surfaces the MZ header, the PKLITE stub, " +
    "the compressed payload, and metadata (version, ext info flag). " +
    "Decompression is delegated to external tools (pklite -x).";

  /// <summary>Copyright marker present at byte offset 0x24..0x32 in unmodified PKLITE stubs.</summary>
  private static ReadOnlySpan<byte> Copyright1 => "PKLITE Copr."u8;
  /// <summary>Lowercase variant emitted by some PKLITE Pro releases.</summary>
  private static ReadOnlySpan<byte> Copyright2 => "PKlite Copr."u8;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null))
      .ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private static List<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var bytes = ms.ToArray();

    if (!PackerScanner.IsMzExecutable(bytes))
      throw new InvalidDataException("PKLITE: not a DOS MZ executable.");

    var idx = PackerScanner.IndexOfBounded(bytes, Copyright1, 0x400);
    if (idx < 0) idx = PackerScanner.IndexOfBounded(bytes, Copyright2, 0x400);
    if (idx < 0)
      throw new InvalidDataException("PKLITE: copyright marker not found in first 1 KB.");

    return [
      ("metadata.ini", BuildMetadata(bytes, idx)),
      ("mz_header.bin", bytes.AsSpan(0, Math.Min(0x40, bytes.Length)).ToArray()),
      ("packed_payload.bin", bytes),
    ];
  }

  private static byte[] BuildMetadata(ReadOnlySpan<byte> data, int copyrightOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[pklite]");
    sb.Append(CultureInfo.InvariantCulture, $"copyright_offset = 0x{copyrightOffset:X4}\n");

    // The version byte sits 2 bytes before the copyright string: high nibble = major,
    // low nibble = minor. Bit 0x20 = "extra info" flag in some releases.
    if (copyrightOffset >= 2) {
      var verByte = data[copyrightOffset - 2];
      var extra = data[copyrightOffset - 1];
      sb.Append(CultureInfo.InvariantCulture, $"version_byte = 0x{verByte:X2} (major={verByte >> 4}, minor={verByte & 0x0F})\n");
      sb.Append(CultureInfo.InvariantCulture, $"extra_info_flag = 0x{extra:X2}\n");
    }
    sb.Append("note = decompression delegated to `pklite -x` (or `unp`)\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
