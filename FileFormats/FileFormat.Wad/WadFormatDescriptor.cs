#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Wad;

/// <summary>
/// Doom WAD (IWAD/PWAD) — the id Software lump-directory game-data archive.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://doomwiki.org/wiki/WAD</c> — Doom Wiki — definitive community WAD documentation</description></item>
///   <item><description>Matthew S. Fell, "The Unofficial Doom Specs" — the original public format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Doom_WAD</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class WadFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the WAD archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the WAD archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new WadReader(stream);
        return r.Entries.Where(e => !e.IsMarker).Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new WadWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddLump(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new WadReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  /// <summary>Gets the id.</summary>
  public string Id => "Wad";
  /// <summary>Gets the display name.</summary>
  public string DisplayName => "WAD";
  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: canonical trailing-directory WADs use changed-byte mutation; unusual
  // layouts fall back to the verified rebuild path rather than guessing.
  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds or replaces lumps. Canonical WADs keep all payloads before a trailing
  /// directory, so new data overwrites the old directory and a fresh directory
  /// is appended. Untouched payload bytes stay at their original offsets.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      WadInPlaceModifier.Add(archive, inputs);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
          continue;
        var destination = Path.Combine(temporaryDirectory, Path.GetFileName(input.ArchiveName));
        File.WriteAllBytes(destination, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes lumps by rewriting only the trailing directory and wiping the
  /// removed payload ranges. Shared/overlapping or non-canonical layouts fall
  /// back to verified rebuild because destructive wiping would not be safe.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    try {
      WadInPlaceModifier.Remove(archive, entryNames);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    var remove = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var file in Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(temporaryDirectory, file).Replace('\\', '/');
        if (remove.Contains(relative) || remove.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".wad";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".wad"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([(byte)'I', (byte)'W', (byte)'A', (byte)'D'], Confidence: 0.90),
    new([(byte)'P', (byte)'W', (byte)'A', (byte)'D'], Confidence: 0.90)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("wad", "WAD")];
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
public string Description => "Doom WAD game data archive";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WadReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size,
      "Stored", e.IsMarker, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WadReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsMarker) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The underlying
  /// reader produces the entry's bytes (decoded if the format compresses
  /// per-entry); the returned stream is a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so adjacent entries and trailing padding
  /// are physically unreachable.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new WadReader(archive);
    foreach (var e in r.Entries) {
      if (e.IsMarker) continue;
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.Extract(e);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new WadWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddLump(name, data);
  }
}
