#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.PeSections;

namespace FileFormat.Te;

/// <summary>
/// Terse Executable (TE) — the stripped-header PE variant of UEFI PEI-phase modules, as found in
/// firmware volumes. Lists and extracts the image's sections.
///
/// References:
/// <list type="bullet">
///   <item><description>UEFI Platform Initialization Specification, Vol. 1, "TE Image" — <c>https://uefi.org/specifications</c></description></item>
///   <item><description><c>https://github.com/ip7z/7zip/blob/main/CPP/7zip/Archive/PeHandler.cpp</c> — 7-Zip's TE handler (LGPL-2.1-or-later); validation converted from it, and the oracle</description></item>
/// </list>
/// </summary>
public sealed class TeFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Te";
  public string DisplayName => "TE (Terse Executable)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".te";
  public IReadOnlyList<string> Extensions => [".te"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("VZ"u8.ToArray(), Confidence: 0.2)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "UEFI Terse Executable image; lists and extracts its sections (read-only).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => PeSectionArchive.List(new TeReader(PeSectionArchive.ReadAll(stream)).Sections);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = PeSectionArchive.ReadAll(stream);
    PeSectionArchive.Extract(image, new TeReader(image).Sections, outputDir, files);
  }
}
