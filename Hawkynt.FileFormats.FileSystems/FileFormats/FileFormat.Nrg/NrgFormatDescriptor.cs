#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nrg;

/// <summary>
/// Nero Burning ROM NRG disc image — a sector stream followed by a chunked
/// session/track descriptor and a trailing NERO/NER5 footer.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://cdemu.sourceforge.io</c> — CDEmu / libMirage NRG parser, used as a behavioural oracle for the reverse-engineered chunk layout</description></item>
///   <item><description><c>https://problemkaputt.de/psx-spx.htm</c> — independently documented NRG CUEX/DAOX/ETN structures</description></item>
///   <item><description>No official public Nero specification is available; the format is proprietary and reverse-engineered</description></item>
/// </list>
/// </summary>
public sealed class NrgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable,
  IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable {

  /// <summary>Gets the id.</summary>
  public string Id => "Nrg";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "NRG";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".nrg";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".nrg"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // NRG magic is a footer signature ("NER5" or "NERO" at a variable offset from EOF),
  // which cannot be represented as a fixed-offset MagicSignature.
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("iso9660", "ISO 9660"),
    new("cdda", "CD-DA raw sectors"),
    new("raw-track", "Raw NRG data track"),
  ];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description =>
    "Nero Burning ROM disc image (NRG v1/v2 multi-session/multi-track reader; raw CD-DA extraction; v2 DAO writer; named ISO edits use verified rebuild)";

  /// <summary>
  /// Lists ISO files from every readable data track plus exact raw entries for CD-DA and non-ISO tracks.
  /// The first ISO filesystem remains rooted at the archive root for backwards compatibility.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new NrgReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(
      index, entry.FullPath, entry.Size, entry.Size, GetEntryMethod(reader, entry),
      entry.IsDirectory, false, null)).ToList();
  }

  /// <summary>Extracts ISO files and exact stored raw-track entries without transcoding CD-DA.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new NrgReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.IsDirectory)
        continue;
      if (files != null && !MatchesFilter(entry.FullPath, files))
        continue;
      WriteFile(outputDir, entry.FullPath, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Creates the generic archive API profile: one DAO session containing one cooked
  /// Mode-1 ISO 9660 track. Call <see cref="NrgWriter.Write(Stream,NrgDiscDefinition)"/>
  /// directly for multi-session, mixed data/audio, pregap, MCN, ISRC, CD-TEXT or raw-sector authoring.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var iso = new FileSystem.Iso.IsoWriter();
    foreach (var input in inputs.Where(static input => !input.IsDirectory))
      iso.AddFile(input.ArchiveName.Replace('\\', '/'), input.ReadContent());

    NrgWriter.Write(output, new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, iso.Build()),
      ]),
    ]));
  }

  /// <summary>
  /// Adds/replaces named ISO entries through a verified rebuild. Multi-track, audio and non-ISO NRGs are
  /// deliberately refused here because flattening them to the generic single-ISO create profile
  /// would destroy disc structure.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    EnsureNamedFileMutationProfile(archive);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
          continue;
        var destination = Path.Combine(tmpDir, input.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
          Directory.CreateDirectory(parent);
        File.WriteAllBytes(destination, input.ReadContent());
      }
    });
  }

  /// <summary>Removes named ISO entries through the same profile-gated verified rebuild.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    EnsureNamedFileMutationProfile(archive);
    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Purges the single-ISO-track R/W profile to a valid empty NRG.</summary>
  public void Purge(Stream archive) {
    EnsureNamedFileMutationProfile(archive);
    RebuildVerb.PurgeViaModifier(archive, this, this);
  }

  /// <summary>Rebuild-defragments the single-ISO-track R/W profile.</summary>
  public void Defragment(Stream archive) {
    EnsureNamedFileMutationProfile(archive);
    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  /// <summary>Progress-reporting rebuild defrag for the single-ISO-track R/W profile.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException($"NRG rebuild defrag supports only {DefragMode.ConsolidateAtStart}.");
    EnsureNamedFileMutationProfile(archive);
    RebuildVerb.RebuildInPlace(archive, this, this,
      onProgress: options.OnProgress,
      cancellationToken: options.CancellationToken);
  }

  /// <summary>
  /// Tight-packs the single-ISO-track profile. Multi-track, audio and non-ISO images are copied through
  /// unchanged rather than being flattened into one ISO track.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!IsNamedFileMutationProfile(input)) {
      CopyThrough(input, output);
      return;
    }

    using var rebuilt = RebuildVerb.CreateScratchStream();
    var useRebuilt = false;
    try {
      RebuildVerb.RebuildToStream(input, rebuilt, this, this);
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
  }

  private static string GetEntryMethod(NrgReader reader, NrgEntry entry) {
    if (entry.Kind != NrgEntryKind.RawTrack)
      return "iso9660";
    var track = reader.Tracks.FirstOrDefault(track =>
      track.SessionNumber == entry.SessionNumber && track.TrackNumber == entry.TrackNumber);
    return track?.IsAudio == true ? "cdda" : "raw-track";
  }

  private static void CopyThrough(Stream input, Stream output) {
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
  }

  private static bool IsNamedFileMutationProfile(Stream archive) {
    if (!archive.CanRead || !archive.CanSeek)
      return false;

    var saved = archive.Position;
    try {
      var profile = NrgStructureInspector.Inspect(archive);
      if (!profile.IsSingleDataTrack)
        return false;

      archive.Position = 0;
      using var reader = new NrgReader(archive, leaveOpen: true);
      return reader.Tracks.Count == 1 && reader.Tracks[0].HasIso9660;
    } catch {
      return false;
    } finally {
      archive.Position = saved;
    }
  }

  private static void EnsureNamedFileMutationProfile(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!IsNamedFileMutationProfile(archive))
      throw new NotSupportedException(
        "Named-file mutation/defrag/purge is supported only for a single ISO 9660 data-track NRG. " +
        "Multi-session, mixed-mode, audio and non-ISO images are not flattened during file-level maintenance.");
  }
}
