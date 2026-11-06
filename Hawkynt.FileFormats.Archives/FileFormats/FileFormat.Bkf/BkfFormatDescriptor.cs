#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Bkf;

/// <summary>
/// Microsoft NTBackup (<c>.bkf</c>) media in Microsoft Tape Format 1.00a: folders (DIRB) and
/// files (FILE) with their STAN data streams, read, written and edited.
///
/// <para>
/// <see cref="Create"/> writes the TAPE DBLK and one Data Set with soft filemarks, as NTBackup
/// lays out a backup file (<see cref="BkfWriter"/>). <see cref="Add"/> and <see cref="Remove"/>
/// write the Data Set anew in the same stream, keeping every remaining DBLK's bytes and
/// streams (<see cref="BkfEditor"/>). A Type 1 Media Based Catalog, which every NTBackup
/// backup file carries, is written anew to describe the edited sets; in a medium with several
/// Data Sets a path names its set (<c>set-NN/</c>). A Type 2 catalog and spanning are refused.
/// </para>
///
/// References:
/// <list type="bullet">
///   <item><description>"Microsoft Tape Format Specification" v1.00a (Seagate Software, 1998) — the defining MTF document</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/NTBackup</c> — background on the creating tool</description></item>
/// </list>
/// </summary>
public sealed class BkfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveModifiable, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Bkf";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Microsoft NTBackup (MTF)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".bkf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".bkf"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    // First DBLK is always TAPE — 4 ASCII bytes at offset 0.
    [new("TAPE"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("compressed", "Compressed (passthrough)"),
  ];
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
    "Microsoft NTBackup .bkf (Microsoft Tape Format 1.00a): folders and files read, written and " +
    "edited; compressed, encrypted and sparse data streams are listed but not decoded.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new BkfReader(stream, leaveOpen: true);
    var result = new List<ArchiveEntryInfo>(r.Entries.Count);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      var method = e.IsDirectory ? "stored" : (e.IsCompressed ? "compressed" : "stored");
      result.Add(new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, method, e.IsDirectory, false, null
      ));
    }
    return result;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new BkfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Compressed, encrypted and sparse data is listed but not decoded; nothing is faked.
      if (e.IsCompressed || e.IsSparse) continue;
      var data = r.Extract(e);
      WriteFile(outputDir, e.Name, data);
    }
  }

  // ── IArchiveCreatable ──────────────────────────────────────────────

  /// <summary>
  /// Writes a new medium: TAPE, then SSET, VOLB, a DIRB per folder each followed by its FILEs,
  /// and ESET, with soft filemarks between (<see cref="BkfWriter"/>).
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var items = new List<BkfWriter.Item>(inputs.Count);
    foreach (var input in inputs)
      items.Add(input.IsDirectory
        ? new BkfWriter.Item(input.ArchiveName, [], IsDirectory: true, input.SourceLastModifiedUtc?.ToLocalTime())
        : new BkfWriter.Item(input.ArchiveName, input.ReadContent(), IsDirectory: false, input.SourceLastModifiedUtc?.ToLocalTime()));

    var bytes = BkfWriter.Build(items);
    output.Write(bytes, 0, bytes.Length);
  }

  // ── IArchiveModifiable ─────────────────────────────────────────────

  /// <summary>
  /// Adds files and folders (a file of the same path is replaced) by writing the Data Set anew
  /// with every existing DBLK and stream kept (<see cref="BkfEditor"/>).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs.Where(static i => !string.IsNullOrEmpty(i.ArchiveName))) {
      if (input.IsDirectory) BkfEditor.AddDirectory(archive, input.ArchiveName);
      else BkfEditor.AddFile(archive, input.ArchiveName, input.ReadContent(), input.SourceLastModifiedUtc?.ToLocalTime());
    }
  }

  /// <summary>
  /// Removes files, and folders with everything in them, by full path; a name that is not there
  /// is reported.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    foreach (var name in entryNames)
      if (!BkfEditor.Remove(archive, name))
        throw new FileNotFoundException($"BKF: '{name}' is not in the backup.", name);
  }
}
