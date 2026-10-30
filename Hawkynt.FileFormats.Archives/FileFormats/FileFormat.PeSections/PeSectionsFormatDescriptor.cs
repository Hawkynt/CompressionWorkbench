#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.PeSections;

/// <summary>
/// Windows PE image as a section archive: every section of the section table, the Authenticode
/// certificate table, debug data stored outside the sections, the COFF symbol table, unclaimed gaps
/// and the trailing overlay, each extracted exactly as stored.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows/win32/debug/pe-format</c> — Microsoft PE/COFF specification</description></item>
///   <item><description><c>https://github.com/ip7z/7zip/blob/main/CPP/7zip/Archive/PeHandler.cpp</c> — 7-Zip's PE handler (LGPL-2.1-or-later); extraction rules converted from it, and the oracle</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Claims no extension: <c>.exe</c>/<c>.dll</c> belong to the resource view
/// (<c>PeResources</c>) and the DOS view (<c>Mz</c>). Pick this view explicitly, or let a bare
/// <c>MZ</c> file without a better match fall through to it.
/// </remarks>
public sealed class PeSectionsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "PeSections";
  public string DisplayName => "PE sections (Windows binary)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => "";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Between Mz (0.08) and PeResources (0.25): never outranks the resource view on a real PE.
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("MZ"u8.ToArray(), Confidence: 0.15)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Windows PE image split into its sections (raw .rsrc included), CERTIFICATE, out-of-section debug data, " +
    "COFF_SYMBOLS, unclaimed [n] gaps and the trailing [overlay]; read-only.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => PeSectionArchive.List(new PeSectionReader(PeSectionArchive.ReadAll(stream)).Items);

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var image = PeSectionArchive.ReadAll(stream);
    PeSectionArchive.Extract(image, new PeSectionReader(image).Items, outputDir, files);
  }
}
