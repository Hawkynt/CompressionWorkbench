#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.GsOs;

/// <summary>
/// Read-only descriptor for Apple IIgs GS/OS 2IMG disk images. The
/// 2IMG container wraps a ProDOS / HFS / DOS 3.3 volume with a 64-byte
/// header — this descriptor parses the header and surfaces the inner
/// volume as one opaque entry. Detection by the "2IMG" ASCII magic at
/// offset 0; uses the .gsdos extension to coexist with FileSystem.ProDos
/// which owns .2mg/.po.
/// </summary>
public sealed class GsOsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "GsOs";
  public string DisplayName => "Apple IIgs GS/OS (2IMG)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  // .2mg is owned by FileSystem.ProDos; we register the GS/OS-specific
  // .gsdos extension only to avoid extension routing conflicts.
  public string DefaultExtension => ".gsdos";
  public IReadOnlyList<string> Extensions => [".gsdos"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Magic intentionally omitted: ProDos already advertises "2IMG"@0, and
  // we don't want detector first-match to fight over the same bytes.
  // Routing to GS/OS is by extension; the reader still parses the 2IMG header.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple IIgs GS/OS 2IMG — stub: header-only, embedded ProDOS/HFS/DOS 3.3 volume surfaced opaque for delegation.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new GsOsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new GsOsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
