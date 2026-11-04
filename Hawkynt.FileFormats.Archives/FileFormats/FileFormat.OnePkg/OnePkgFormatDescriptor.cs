#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Cab;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.OnePkg;

/// <summary>
/// OneNote package (<c>.onepkg</c>) — the single-file export of a OneNote notebook: a Microsoft
/// Cabinet holding the notebook's <c>.onetoc2</c> table of contents and its <c>.one</c> sections.
/// Recognised by extension only, because its <c>MSCF</c> magic belongs to the CAB descriptor.
///
/// <para>Read-only: listing and extraction are the CAB reader's, but no OneNote is available to
/// confirm that a package we would build opens as a notebook, so creation is not offered.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>PRONOM <c>fmt/987</c> — Microsoft OneNote Package File (signature <c>MSCF</c>, CAB 1.3, a member named <c>*.onetoc*</c>)</description></item>
///   <item><description>[MS-CAB]: Cabinet File Format — Microsoft Open Specifications</description></item>
/// </list>
/// </summary>
public sealed class OnePkgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "OnePkg";
  public string DisplayName => "OneNote package";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".onepkg";
  public IReadOnlyList<string> Extensions => [".onepkg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("cab", "Cabinet")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "OneNote notebook package (Microsoft Cabinet of .onetoc2 and .one sections), read-only";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new CabReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.UncompressedSize, -1,
      "CAB", false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new CabReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ExtractEntry(e));
    }
  }
}
