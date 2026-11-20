#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Daa;

/// <summary>
/// gBurner GBI compressed CD/DVD image: PowerISO's DAA version 0x110 layout under a "GBI" signature,
/// with an obfuscated chunk table. Exposes the ISO as <c>image.iso</c> plus a synthetic
/// <c>metadata.ini</c>; reads deflate, LZMA and stored chunks, split images (from their first
/// volume's path, <c>x.part01.gbi</c>) and password-protected ones (with the password).
/// </summary>
/// <remarks>Specification: <c>docs/DAA-ON-DISK.md</c>, "gBurner GBI".</remarks>
public sealed class GbiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, ISyntheticEntryNames {
  private static readonly DaaFormatDescriptor Daa = new();

  public string Id => "Gbi";
  public string DisplayName => "GBI (gBurner)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".gbi";
  public IReadOnlyList<string> Extensions => [".gbi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("GBI\0\0\0\0\0\0\0\0\0\0\0\0\0"u8.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("deflate", "Deflate chunks"), new("lzma", "LZMA chunks")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "gBurner GBI compressed ISO image (DAA 0x110 layout, obfuscated chunk table)";
  public IReadOnlySet<string> SyntheticEntryNames => Daa.SyntheticEntryNames;

  public List<ArchiveEntryInfo> List(Stream stream, string? password) => Daa.List(stream, password);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) => Daa.Extract(stream, outputDir, password, files);
}
