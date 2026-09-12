#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI disc image (Padus) — CD track data followed by a trailing
/// session/track descriptor.
///
/// <para>The public specification was never released. Descriptor parsing follows
/// the independently documented on-disk layout and is cross-checked against
/// CDIrip, Aaru and mkdcdisc; see <c>docs/CDI-ON-DISK.md</c>.</para>
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
    "DiscJuggler CDI (multisession/multitrack/audio/Mode-2 read; R/W rebuild for the single-session cooked Mode-1 profile; mixed layouts fail closed on mutation)";

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

    var zeroSector = new byte[2048];
    for (var i = 0; i < CdiDescriptor.StandardPregapSectors; ++i)
      output.Write(zeroSector);

    output.Write(payload);
    var remainder = payload.Length % 2048;
    if (remainder != 0)
      output.Write(zeroSector.AsSpan(0, 2048 - remainder));

    var dataSectorCount = checked((uint)((payload.Length + 2047L) / 2048L));
    output.Write(CdiDescriptor.BuildSingleTrackV35(dataSectorCount));
  }

  /// <summary>
  /// Adds/replaces ordinary ISO files through a verified rebuild when the image
  /// is the layout-preserving single-track Mode-1 profile. Mixed/multisession
  /// images are readable but intentionally refused for mutation because a
  /// rebuild would silently discard their audio tracks, pregaps or session map.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    if (UsesLegacySectorNamespace(archive) && InputsAreSectorAddresses(inputs)) {
      CdiInPlaceModifier.AddOrReplaceSectors(archive,
        inputs.Select(input => (input.ArchiveName, input.ReadContent())));
      return;
    }

    EnsureRebuildSafeProfile(archive);
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

  /// <summary>Removes ordinary ISO files through the same profile-preserving rebuild path.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    if (UsesLegacySectorNamespace(archive) && entryNames.Length > 0 && entryNames.All(IsSectorAddress)) {
      CdiInPlaceModifier.RemoveSectors(archive, entryNames);
      return;
    }

    EnsureRebuildSafeProfile(archive);
    var skip = new HashSet<string>(entryNames.Select(name => name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tempDirectory => {
      foreach (var file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tempDirectory, file).Replace('\\', '/');
        if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Purges only profiles whose optical layout the creator can preserve.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    EnsureRebuildSafeProfile(archive);
    RebuildVerb.PurgeViaModifier(archive, this, this);
  }

  /// <summary>Rebuild-defragments the supported single-track profile.</summary>
  public void Defragment(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    EnsureRebuildSafeProfile(archive);
    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  /// <summary>Rebuild-defragments with progress/cancellation while preserving the profile gate.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"CDI supports only {DefragMode.ConsolidateAtStart} defragmentation.");

    EnsureRebuildSafeProfile(archive);
    RebuildVerb.RebuildInPlace(
      archive,
      this,
      this,
      onProgress: options.OnProgress,
      cancellationToken: options.CancellationToken
    );
  }

  /// <summary>Shrinks by verified rebuild only when rebuilding preserves the optical layout profile.</summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    EnsureRebuildSafeProfile(input);
    ((IArchiveShrinkable)this).ShrinkDefault(input, output);
  }

  private static bool UsesLegacySectorNamespace(Stream archive) {
    var position = archive.CanSeek ? archive.Position : 0;
    try {
      return CdiDescriptor.TryReadFooter(archive, out var footer) && footer.IsLegacyFooterOnly;
    } finally {
      if (archive.CanSeek) archive.Position = position;
    }
  }

  private static void EnsureRebuildSafeProfile(Stream archive) {
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("CDI rebuild mutation requires a readable, seekable stream.", nameof(archive));

    var originalPosition = archive.Position;
    try {
      if (CdiDescriptor.TryReadFooter(archive, out var footer) && footer.IsLegacyFooterOnly)
        return;

      archive.Position = 0;
      using var reader = new CdiReader(archive, leaveOpen: true);
      if (reader.Tracks.Count == 1) {
        var track = reader.Tracks[0];
        if (track.SessionNumber == 1 &&
            track.TrackNumber == 1 &&
            track.Mode == CdiTrackMode.Mode1 &&
            track.ReadMode == CdiReadMode.Mode1_2048)
          return;
      }

      throw new NotSupportedException(
        "CDI mutation is limited to the single-session, single cooked Mode-1 track profile. " +
        "This image has a mixed, multisession, audio, Mode-2, raw-sector or otherwise unsupported layout; " +
        "rebuilding it as one ISO track would destroy optical-disc semantics.");
    } finally {
      archive.Position = originalPosition;
    }
  }

  private static bool InputsAreSectorAddresses(IReadOnlyList<ArchiveInputInfo> inputs)
    => inputs.Count > 0 && inputs.All(input => !input.IsDirectory && IsSectorAddress(input.ArchiveName));

  private static bool IsSectorAddress(string name)
    => CdiInPlaceModifier.TryParseSectorEntryName(name, out _);

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
