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
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://ecma-international.org/publications-and-standards/standards/ecma-335/</c> — ECMA-335 Common Language Infrastructure — metadata streams and physical layout</description></item>
///   <item><description><c>https://learn.microsoft.com/en-us/windows/win32/debug/pe-format</c> — Microsoft PE/COFF specification — the PE envelope including the CLI header</description></item>
///   <item><description><c>https://github.com/dotnet/runtime</c> — .NET runtime — canonical implementation</description></item>
/// </list>
/// </summary>
public sealed class NetAssemblyFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "NetAssembly";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => ".NET assembly";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".dll";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // 'MZ' PE header — same as every other PE descriptor. We keep the confidence below
    // PeResources/ResourceDll so extension-based routing wins; the List/Extract methods
    // themselves verify a populated CLI directory and return empty otherwise.
    new([(byte)'M', (byte)'Z'], Confidence: 0.20),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Managed .NET assembly (PE with CLI header) surfaced as an archive of metadata " +
    "streams, manifest resources, and an AssemblyRef listing.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var items = new NetAssemblyReader().ReadAll(stream);
    return items.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null
    )).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in new NetAssemblyReader().ReadAll(stream)) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}
