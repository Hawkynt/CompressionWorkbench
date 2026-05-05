#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.VppV2;

/// <summary>
/// Volition Package v2 (Saint's Row 2 era) descriptor — handles <c>.vpp_pc</c> archives with
/// optional per-entry zlib compression.
/// </summary>
/// <remarks>
/// On-disk magic <c>CE 0A 89 51</c> is shared with VPP v1; both descriptors match the same
/// signature bytes. We declare a strictly higher confidence (0.93 vs v1's 0.95... see note) and
/// reject Version != 2 inside the reader so <see cref="FormatRegistry"/> falls through to the v1
/// descriptor for older archives. Saint's Row 2 ships <c>.vpp_pc</c>, which differs from v1's
/// <c>.vpp</c>, so extension-based detection routes correctly without ambiguity.
/// </remarks>
public sealed class VppV2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "VppV2";
  public string DisplayName => "Volition VPP v2 (Saint's Row 2)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vpp_pc";
  public IReadOnlyList<string> Extensions => [".vpp_pc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(new byte[] { 0xCE, 0x0A, 0x89, 0x51 }, Confidence: 0.93)
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("zlib", "Zlib"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Volition VPP v2 (Saint's Row 2 / SR2 era), zlib-compressible per entry";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new VppV2Reader(stream);
    return r.Entries.Select((e, i) =>
      new ArchiveEntryInfo(i, e.Name, e.DataSize, e.CompressedSize,
        e.IsCompressed ? "Zlib" : "Stored", false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new VppV2Reader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new VppV2Writer(output, leaveOpen: true);
    foreach (var (name, data) in FilesOnly(inputs))
      w.AddEntry(name, data);
  }
}
