#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.PackDev;

/// <summary>
/// Amiga PackDev device image (".pkd", ".dsq"; magic "PKD\x13" or the older "PKD\x11"): a floppy or
/// other block device stored track by track, free blocks left out. Read-only: the single entry is the
/// rebuilt image, <c>disk.adf</c> for a DD or HD floppy and <c>disk.img</c> otherwise.
///
/// Reference: <c>https://github.com/MacPaw/XADMaster</c> — libxad's PackDev client
/// (<c>libxad/clients/PackDev.c</c>).
/// </summary>
public sealed class PackDevFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "PackDev";
  public string DisplayName => "PackDev (Amiga)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".pkd";
  public IReadOnlyList<string> Extensions => [".pkd", ".dsq"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x50, 0x4B, 0x44, 0x13], Confidence: 0.90),
    new([0x50, 0x4B, 0x44, 0x11], Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored blocks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga PackDev device image (read-only)";

  internal static string ImageName(PackDevReader r) =>
    r.ImageSize is 901120 or 1802240 ? "disk.adf" : "disk.img";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var r = new PackDevReader(stream);
      var method = r.XpkPacker == 0 ? "stored" : $"XPK {r.PackerName}".TrimEnd();
      return [new(0, ImageName(r), r.ImageSize, stream.CanSeek ? stream.Length : 0, method, false, false, null)];
    } catch (InvalidDataException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.pkd", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PackDevReader(stream);
    var name = ImageName(r);
    if (files is not { Length: > 0 } || MatchesFilter(name, files))
      WriteFile(outputDir, name, r.ExtractImage());
  }
}
