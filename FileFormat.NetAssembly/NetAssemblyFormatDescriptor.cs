#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.NetAssembly;

/// <summary>
/// Read-only archive view of a managed .NET assembly (CLI PE). Surfaces the metadata
/// streams, a decoded <c>references.txt</c> from <c>AssemblyRef</c>, and per-manifest-
/// resource entries under <c>resources/</c>. Detection is extension-based (<c>.dll</c> /
/// <c>.exe</c>) with a parser sanity check: the CLI header (data-directory index 14) must
/// be populated or <c>List</c> returns an empty set.
/// </summary>
public sealed class NetAssemblyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "NetAssembly";
  public string DisplayName => ".NET assembly";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dll";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 'MZ' PE header — same as every other PE descriptor. We keep the confidence below
    // PeResources/ResourceDll so extension-based routing wins; the List/Extract methods
    // themselves verify a populated CLI directory and return empty otherwise.
    new([(byte)'M', (byte)'Z'], Confidence: 0.20),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Managed .NET assembly (PE with CLI header) surfaced as an archive of metadata " +
    "streams, manifest resources, and an AssemblyRef listing.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var items = new NetAssemblyReader().ReadAll(stream);
    return items.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new NetAssemblyReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
