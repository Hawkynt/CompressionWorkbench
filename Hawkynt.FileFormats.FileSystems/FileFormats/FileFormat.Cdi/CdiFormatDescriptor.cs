#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Cdi;

/// <summary>
/// DiscJuggler CDI disc image (Padus) — CD track data followed by a trailing
/// session/track descriptor. Reading covers modern and old v2/v3 descriptor
/// dialects; supported Mode-1 and Mode-2 Form-1 filesystems can be rebuilt
/// inside an existing multi-track layout without rewriting the optical descriptor.
/// </summary>
public sealed class CdiFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveCreatable,
  IArchiveModifiable,
  IArchiveDefragmentable,
  IArchiveShrinkable,
  IFormatOptionsSchema {

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
    "DiscJuggler CDI (v2/v3/v3.5; multisession/multitrack/audio read; layout-preserving R/W for Mode-1 and Mode-2 Form-1 data tracks)";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(
      FormatOptionKeys.TargetCompatibility,
      "DiscJuggler descriptor version",
      FormatOptionKind.Enum,
      "3.5",
      ["3.5", "3.0", "2.0"],
      "Writer compatibility target. v2/v3 use the older absolute-descriptor-offset dialect; v3.5 uses the trailing descriptor-length dialect.",
      IsOptimizationAxis: false
    ),
  ];

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

    var version = ParseTargetVersion(options.GetOption(FormatOptionKeys.TargetCompatibility, "3.5"));
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
    if (version == CdiDescriptor.Version35) {
      output.Write(CdiDescriptor.BuildSingleTrackV35(dataSectorCount));
      return;
    }

    var descriptorOffset = checked((uint)output.Position);
    output.Write(CdiDescriptor.BuildSingleTrackLegacy(version, dataSectorCount, descriptorOffset));
  }

  /// <summary>
  /// Adds/replaces ordinary ISO files. Descriptor-bearing images use a
  /// transactional embedded-ISO rebuild that leaves all optical tracks and the
  /// descriptor byte layout in place. Raw Mode-1 and Mode-2 Form-1 sectors have
  /// their standard CD EDC/ECC regenerated; Form-2/formless sectors fail closed.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    if (UsesLegacySectorNamespace(archive) && InputsAreSectorAddresses(inputs)) {
      CdiInPlaceModifier.AddOrReplaceSectors(archive,
        inputs.Select(input => (input.ArchiveName, input.ReadContent())));
      return;
    }

    if (CanPreserveOpticalLayout(archive)) {
      CdiEmbeddedIsoRebuilder.Rewrite(archive, this, tempDirectory => {
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
      return;
    }

    EnsureLegacyFooterOrThrow(archive);
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

  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);

    if (UsesLegacySectorNamespace(archive) && entryNames.Length > 0 && entryNames.All(IsSectorAddress)) {
      CdiInPlaceModifier.RemoveSectors(archive, entryNames);
      return;
    }

    var skip = new HashSet<string>(entryNames.Select(name => name.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
    if (CanPreserveOpticalLayout(archive)) {
      CdiEmbeddedIsoRebuilder.Rewrite(archive, this, tempDirectory => {
        foreach (var file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories)) {
          var relative = Path.GetRelativePath(tempDirectory, file).Replace('\\', '/');
          if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
            File.Delete(file);
        }
      });
      return;
    }

    EnsureLegacyFooterOrThrow(archive);
    RebuildVerb.EditViaRebuild(archive, this, this, tempDirectory => {
      foreach (var file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tempDirectory, file).Replace('\\', '/');
        if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Empties the active data track, preserving the optical layout where the rebuilder can.</summary>
  public void Purge(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (CanPreserveOpticalLayout(archive)) {
      CdiEmbeddedIsoRebuilder.Rewrite(archive, this, tempDirectory => {
        foreach (var file in Directory.GetFiles(tempDirectory, "*", SearchOption.AllDirectories))
          File.Delete(file);
        foreach (var directory in Directory.GetDirectories(tempDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
          Directory.Delete(directory, recursive: false);
      });
      return;
    }

    EnsureLegacyFooterOrThrow(archive);
    RebuildVerb.PurgeViaModifier(archive, this, this);
  }

  /// <summary>Consolidates the active data track, preserving the optical layout where the rebuilder can.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Consolidating defragmentation with progress and cancellation; other modes are refused.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"CDI supports only {DefragMode.ConsolidateAtStart} defragmentation.");

    if (CanPreserveOpticalLayout(archive)) {
      CdiEmbeddedIsoRebuilder.Rewrite(archive, this, progress: options);
      return;
    }

    EnsureLegacyFooterOrThrow(archive);
    RebuildVerb.RebuildInPlace(
      archive,
      this,
      this,
      onProgress: options.OnProgress,
      cancellationToken: options.CancellationToken
    );
  }

  /// <summary>
  /// A multi-track CDI has a fixed optical track map, so shrink cannot remove
  /// bytes without rewriting that map. Raw/Mode-2 tracks likewise cannot be
  /// recreated smaller by the current cooked-only creator without changing their
  /// sector geometry. Those profiles therefore copy through unchanged. A single
  /// cooked Mode-1 track may be rebuilt smaller while preserving its descriptor
  /// compatibility target.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("CDI shrink requires a readable, seekable input.", nameof(input));

    var original = input.Position;
    try {
      input.Position = 0;
      using var reader = new CdiReader(input, leaveOpen: true);
      var active = reader.ActiveDataTrack;
      if (reader.Tracks.Count > 0 && !CdiEmbeddedIsoRebuilder.CanRewrite(reader))
        throw CdiEmbeddedIsoRebuilder.UnsupportedLayout();

      var recreatableCookedProfile = reader.Tracks.Count == 1 && active is {
        Mode: CdiTrackMode.Mode1,
        ReadMode: CdiReadMode.Mode1_2048,
        StoredSectorSize: 2048,
      };
      if (reader.Tracks.Count > 1 || reader.Tracks.Count == 1 && !recreatableCookedProfile) {
        CopyThrough(input, output);
        return;
      }

      var formatSpecific = reader.CdiVersion is CdiDescriptor.Version2 or CdiDescriptor.Version3 or CdiDescriptor.Version35
        ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
          [FormatOptionKeys.TargetCompatibility] = CompatibilityName(reader.CdiVersion),
        }
        : null;

      using var rebuilt = RebuildVerb.CreateScratchStream();
      var useRebuilt = false;
      try {
        input.Position = 0;
        RebuildVerb.RebuildToStream(input, rebuilt, this, this, formatSpecific);
        useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
      } catch {
        useRebuilt = false;
      }

      output.Position = 0;
      output.SetLength(0);
      if (useRebuilt) {
        rebuilt.Position = 0;
        rebuilt.CopyTo(output);
      } else {
        input.Position = 0;
        input.CopyTo(output);
      }
    } finally {
      input.Position = Math.Min(original, input.Length);
    }
  }

  private static bool CanPreserveOpticalLayout(Stream archive) {
    if (!archive.CanRead || !archive.CanSeek)
      return false;
    var position = archive.Position;
    try {
      archive.Position = 0;
      using var reader = new CdiReader(archive, leaveOpen: true);
      return reader.Tracks.Count > 0 && CdiEmbeddedIsoRebuilder.CanRewrite(reader);
    } finally {
      archive.Position = position;
    }
  }

  private static bool UsesLegacySectorNamespace(Stream archive) {
    var position = archive.CanSeek ? archive.Position : 0;
    try {
      return CdiDescriptor.TryReadFooter(archive, out var footer) && footer.IsLegacyFooterOnly;
    } finally {
      if (archive.CanSeek) archive.Position = position;
    }
  }

  private static void EnsureLegacyFooterOrThrow(Stream archive) {
    if (!archive.CanRead || !archive.CanSeek)
      throw new ArgumentException("CDI rebuild mutation requires a readable, seekable stream.", nameof(archive));
    var position = archive.Position;
    try {
      if (CdiDescriptor.TryReadFooter(archive, out var footer) && footer.IsLegacyFooterOnly)
        return;
      throw CdiEmbeddedIsoRebuilder.UnsupportedLayout();
    } finally {
      archive.Position = position;
    }
  }

  private static uint ParseTargetVersion(string target) => target.Trim() switch {
    "3.5" or "v3.5" or "V3.5" => CdiDescriptor.Version35,
    "3" or "3.0" or "v3" or "V3" or "v3.0" or "V3.0" => CdiDescriptor.Version3,
    "2" or "2.0" or "v2" or "V2" or "v2.0" or "V2.0" => CdiDescriptor.Version2,
    _ => throw new ArgumentException($"Unsupported CDI compatibility target '{target}'. Expected 3.5, 3.0, or 2.0."),
  };

  private static string CompatibilityName(uint version) => version switch {
    CdiDescriptor.Version2 => "2.0",
    CdiDescriptor.Version3 => "3.0",
    _ => "3.5",
  };

  private static void CopyThrough(Stream input, Stream output) {
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
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
