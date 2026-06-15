#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DragonFs;

/// <summary>
/// Read-only descriptor for DragonFS — the embedded read-only filesystem
/// used by Libdragon (open Nintendo 64 SDK) to bundle assets inside an
/// N64 ROM image. DragonFS is big-endian throughout, uses 32-byte
/// directory records starting at file offset 256 (Libdragon
/// DFS_ROOT_OFFSET), and lacks an unambiguous fixed magic in original
/// images — detection is by .dfs extension plus an optional "DragonFS"
/// ASCII tag at offset 0 for self-produced research images.
/// </summary>
public sealed class DragonFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable {
  public string Id => "DragonFs";
  public string DisplayName => "DragonFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dfs";
  public IReadOnlyList<string> Extensions => [".dfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Optional 8-byte "DragonFS" ASCII tag at offset 0 — only present in
    // images that opt into the explicit tag; canonical Libdragon DFS images
    // start straight with binary directory entries at offset 256.
    new("DragonFS"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "DragonFS embedded read-only filesystem (Libdragon / Nintendo 64).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new DragonFsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new DragonFsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Produces a fresh DragonFS image from scratch holding <paramref name="inputs"/>.
  /// DragonFS is a flat filesystem, so subdirectory paths are flattened to their
  /// leaf names via <see cref="DragonFsWriter.AddFile"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var w = new DragonFsWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => throw new NotSupportedException("DragonFs read-only — defragmentation requires a writer.");

  public void Defragment(Stream archive, DefragOptions options)
    => throw new NotSupportedException("DragonFs read-only — defragmentation requires a writer.");
}
