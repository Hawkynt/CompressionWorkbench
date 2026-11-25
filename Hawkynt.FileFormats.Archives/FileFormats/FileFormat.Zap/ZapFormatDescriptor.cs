#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zap;

/// <summary>
/// Amiga ZAP 1.41 disk archive (".zap", GREMLIN of MAYHEM, 1990): an 880 KiB floppy crunched in ten
/// blocks of eight cylinders. Read-only: <c>disk.adf</c> is the floppy image, <c>banner.txt</c> the
/// attached text when present.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/ZAP-ON-DISK.md</c> — layout measured on ZAP 1.41 output</description></item>
///   <item><description>libxad's ZAP client (Stuart Caie, <c>libxad/amiga/source/clients/ZAP.c</c>)</description></item>
/// </list>
/// </summary>
public sealed class ZapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  private const string ImageName = "disk.adf";
  private const string BannerName = "banner.txt";

  public string Id => "Zap";
  public string DisplayName => "ZAP (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".zap";
  public IReadOnlyList<string> Extensions => [".zap"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("ZAP V1.41"u8.ToArray(), Offset: 4, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("zap", "ZAP LZ77 + run-length")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga ZAP floppy disk archive (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new ZapReader(stream);
      var entries = new List<ArchiveEntryInfo> {
        new(0, ImageName, ZapReader.Cylinders * ZapReader.CylinderSize, stream.CanSeek ? stream.Length : 0, "zap", false, false, null),
      };
      if (r.Banner is { } banner)
        entries.Add(new(1, BannerName, banner.Length, banner.Length, "zap", false, false, null));
      return entries;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.zap", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZapReader(stream);
    if (files is not { Length: > 0 } || MatchesFilter(ImageName, files))
      WriteFile(outputDir, ImageName, r.ExtractDisk());
    if (r.Banner is { } banner && (files is not { Length: > 0 } || MatchesFilter(BannerName, files)))
      WriteFile(outputDir, BannerName, banner);
  }
}
