#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pak;

/// <summary>
/// id Software Quake PAK resource archive: a 12-byte <c>PACK</c> header,
/// verbatim file payloads, and a 64-byte-per-entry directory referenced by the
/// header. Canonical archives place that directory at EOF.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/id-Software/Quake</c> — released Quake source; <c>dpackheader_t</c>/<c>dpackfile_t</c> are the canonical definition</description></item>
///   <item><description>Unofficial Quake Specs (Olivier Montanuy et al.) — long-standing community format documentation</description></item>
/// </list>
/// </summary>
public sealed class PakFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the PAK archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the PAK archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new PakReader(stream);
        var list = new List<(string, byte[])>();
        while (r.GetNextEntry() is { } e)
          list.Add((e.FileName, r.ReadEntryData()));
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using var w = new PakWriter(ms);
        foreach (var (name, data) in files)
          w.AddEntry(name, data);
        w.Finish();
        return ms.ToArray();
      });
  }

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    PakReader reader;
    try {
      archive.Position = 0;
      reader = new PakReader(archive);
    } catch {
      yield break;
    }

    yield return new DefragBlockInfo(0, PakReader.HeaderSize, DefragBlockKind.MetadataReserved, FileName: "PACK header");
    foreach (var entry in reader.Entries)
      if (entry.Size > 0)
        yield return new DefragBlockInfo(entry.FileOffset, entry.Size, DefragBlockKind.Used, FileName: entry.FileName);
    if (reader.DirectoryLength > 0)
      yield return new DefragBlockInfo(reader.DirectoryOffset, reader.DirectoryLength, DefragBlockKind.MetadataReserved, FileName: "PACK directory");
  }

  /// <summary>
  /// Adds or same-name replaces files. Canonical trailing-directory archives use
  /// <see cref="PakInPlaceModifier"/>: new bytes overwrite the old directory,
  /// then a regenerated directory is appended. Unsupported non-canonical layouts
  /// fall back to the verified rebuild.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).ToList();
    if (files.Count == 0)
      return;

    try {
      archive.Position = 0;
      PakInPlaceModifier.AddFiles(archive, files);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var (name, data) in files) {
        var destination = Path.Combine(tmpDir, name.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
          Directory.CreateDirectory(directory);
        File.WriteAllBytes(destination, data);
      }
    });
  }

  /// <summary>
  /// Removes named files by rewriting only the trailing directory and wiping
  /// unreferenced removed payload ranges. Non-canonical layouts rebuild.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0)
      return;

    try {
      archive.Position = 0;
      PakInPlaceModifier.RemoveFiles(archive, entryNames, wipeData: true);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    var skip = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (skip.Contains(relative) || skip.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Gets the id.</summary>
  public string Id => "Pak";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "PAK";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".pak";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".pak"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("PACK"u8.ToArray(), Confidence: 0.95)];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description => "Quake PACK game resource archive";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new PakReader(stream);
    return r.Entries.Select((entry, index) =>
      new ArchiveEntryInfo(index, entry.FileName, entry.Size, entry.Size, "Stored", false, false, null)).ToList();
  }

  /// <summary>Extracts matching entries.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new PakReader(stream);
    while (r.GetNextEntry() is { } entry) {
      if (files != null && !MatchesFilter(entry.FileName, files))
        continue;
      WriteFile(outputDir, entry.FileName, r.ReadEntryData());
    }
  }

  /// <summary>Opens one PAK entry as a bounded read-only stream.</summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek)
      archive.Position = 0;
    using var r = new PakReader(archive);
    while (r.GetNextEntry() is { } entry) {
      if (!string.Equals(entry.FileName, entryName, StringComparison.OrdinalIgnoreCase))
        continue;
      var bytes = r.ReadEntryData();
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var stream = this.OpenEntry(archive, entryName, password);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    return memory.ToArray();
  }

  /// <summary>Creates a canonical Quake PACK archive.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var writer = new PakWriter(output);
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddEntry(name, data);
    writer.Finish();
  }
}
