#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.MooseFs;

/// <summary>
/// Partial R/O descriptor for MooseFS master-metadata images
/// (<c>metadata.mfs</c>). Surfaces the metadata envelope (signature,
/// counters, section index) and the raw payload bytes of each walked
/// section. Path-tree (NODE/EDGE) and chunk-id (CHNK) bodies are
/// version-specific and require golden samples to decode honestly — the
/// reader makes no claim about their internal structure.
///
/// <para>
/// MooseFS file content lives on chunk servers and is unreachable from a
/// single metadata image. Listing therefore surfaces ONLY synthetic
/// metadata + per-section raw payloads, never POSIX paths.
/// </para>
/// </summary>
public sealed class MooseFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "MooseFs";
  public string DisplayName => "MooseFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".mfsm";
  // Note: ".mfs" intentionally NOT claimed — collides with Macintosh File System (FileSystem.Mfs).
  // MooseFS detection is by magic 'MFSM' at offset 0; the ".mfsm" extension is MooseFS-specific.
  public IReadOnlyList<string> Extensions => [".mfsm"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "MFSM" at offset 0 — MooseFS master metadata tag.
    new("MFSM"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "MooseFS — partial R/O of master metadata envelope (signature + section " +
    "index). File content lives on chunk servers and is unreachable from a " +
    "single metadata image; only metadata.ini + raw image + per-section " +
    "raw payloads are surfaced.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new MooseFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new MooseFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new MooseFsReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"MooseFS entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
