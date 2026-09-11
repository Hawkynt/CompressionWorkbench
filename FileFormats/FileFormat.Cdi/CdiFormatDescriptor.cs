#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI disc image (Padus) — CD track data followed by a trailing
/// session/track descriptor.
///
/// <para>The public specification was never released. The implemented v3.5
/// writer profile is a clean-room reconstruction from the container behaviour
/// documented by CDIrip and cross-checked against independent DiscJuggler
/// readers; see <c>docs/CDI-ON-DISK.md</c>.</para>
/// </summary>
public sealed class CdiFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveShrinkable {

  public string Id => "Cdi";
  public string DisplayName => "CDI";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".cdi";
  public IReadOnlyList<string> Extensions => [".cdi"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DiscJuggler CDI disc image (R/W through verified ISO 9660 rebuild; existing-sector low-level rewrite retained for legacy footer-only images)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new CdiReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index,
      entry.FullPath,
      entry.Size,
      entry.Size,
      "iso9660",
      entry.IsDirectory,
      false,
      null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new CdiReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory) continue;
      if (files != null && !MatchesFilter(entry.FullPath, files)) continue;
      WriteFile(outputDir, entry.FullPath, reader.Extract(entry));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("CDI creation requires a writable, seekable stream.", nameof(output));

    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);

    var payload = iso.Build();
    output.Position = 0;
    output.SetLength(0);
    output.Write(payload);

    var remainder = payload.Length % 2048;
    if (remainder != 0)
      output.Write(new byte[2048 - remainder]);

    var sectorCount = checked((uint)(output.Position / 2048));
    output.Write(CdiDescriptor.BuildSingleTrackV35(sectorCount));
  }

  /// <summary>
  /// Adds/replaces ordinary ISO files through the verified rebuild path. The
  /// obsolete footer-only profile emitted by older CompressionWorkbench builds
  /// keeps its explicit sector namespace for backward compatibility.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    if (UsesLegacySectorNamespace(archive)) {
      CdiInPlaceModifier.AddOrReplaceSectors(archive,
        inputs.Where(input => !input.IsDirectory).Select(input => (input.ArchiveName, input.ReadContent())));
      return;
    }

    RebuildVerb.EditViaRebuild(archive, this, this, tempDirectory => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
          continue;

        var archiveName = input.ArchiveName.Replace('\\', '/');
        DeleteExistingIgnoringCase(tempDirectory, archiveName);
        var destination = Path.Combine(tempDirectory, archiveName.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
          Directory.CreateDirectory(parent);
        File.WriteAllBytes(destination, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes ordinary ISO files through the verified rebuild path. Legacy
  /// footer-only images retain their old sector-clearing namespace.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    if (UsesLegacySectorNamespace(archive)) {
      CdiInPlaceModifier.RemoveSectors(archive, entryNames);
      return;
    }

    var skip = new HashSet<string>(entryNames.Select(name => name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tempDirectory => {
      foreach (var file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tempDirectory, file).Replace('\\', '/');
        if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  private static bool UsesLegacySectorNamespace(Stream archive)
    => CdiDescriptor.TryReadFooter(archive, out var footer) && footer.IsLegacyFooterOnly;

  private static void DeleteExistingIgnoringCase(string root, string archiveName) {
    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
      var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
      if (!relative.Equals(archiveName, StringComparison.OrdinalIgnoreCase))
        continue;
      File.Delete(file);
      return;
    }
  }
}
