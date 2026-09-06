#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Gob;

/// <summary>
/// LucasArts GOB resource archive used by Star Wars: Jedi Knight (Dark Forces II) and Outlaws.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/luciusDXL/TheForceEngine</c> — The Force Engine — maintained open reimplementation of the Jedi engine, reads GOB containers</description></item>
///   <item><description>No official specification — community-reverse-engineered LucasArts container</description></item>
/// </list>
/// </summary>
public sealed class GobFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    using var reader = new GobReader(archive, leaveOpen: true);

    yield return new DefragBlockInfo(0, GobConstants.HeaderSize, DefragBlockKind.MetadataReserved, FileName: "GOB header");
    foreach (var entry in reader.Entries)
      if (entry.Size > 0)
        yield return new DefragBlockInfo(entry.Offset, entry.Size, DefragBlockKind.Used, FileName: entry.Name);

    Span<byte> directoryOffsetBytes = stackalloc byte[4];
    archive.Position = 8;
    archive.ReadExactly(directoryOffsetBytes);
    var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(directoryOffsetBytes);
    var directoryLength = checked(4L + (long)reader.Entries.Count * GobConstants.DirectoryEntrySize);
    yield return new DefragBlockInfo(directoryOffset, directoryLength, DefragBlockKind.MetadataReserved, FileName: "GOB directory");
  }

  /// <summary>Gets the id.</summary>
  public string Id => "Gob";

  /// <summary>Gets the display name.</summary>
  public string DisplayName => "Lucasarts GOB";

  /// <summary>Gets the category.</summary>
  public FormatCategory Category => FormatCategory.Archive;

  /// <summary>Gets the capabilities.</summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>Gets the default extension.</summary>
  public string DefaultExtension => ".gob";

  /// <summary>Gets the extensions.</summary>
  public IReadOnlyList<string> Extensions => [".gob", ".goo"];

  /// <summary>Gets the compound extensions.</summary>
  public IReadOnlyList<string> CompoundExtensions => [];

  // Trailing space is part of the GOB v2 magic — without it we would collide with
  // GOB v1 (Dark Forces) which is structurally different and out of scope here.
  /// <summary>Gets the magic signatures.</summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [new("GOB "u8.ToArray(), Confidence: 0.95)];

  /// <summary>Gets the methods.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("gob2", "GOB v2")];

  /// <summary>Gets the tar compression format id.</summary>
  public string? TarCompressionFormatId => null;

  /// <summary>Gets the family.</summary>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <summary>Gets the description.</summary>
  public string Description => "Lucasarts archive (Jedi Knight, Outlaws)";

  /// <summary>Lists the entries in the supplied container.</summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var reader = new GobReader(stream, leaveOpen: true);
    return reader.Entries.Select((entry, index) => new ArchiveEntryInfo(index, entry.Name, entry.Size, entry.Size,
      "Stored", false, false, null)).ToList();
  }

  /// <summary>Extracts matching entries.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var reader = new GobReader(stream, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (files != null && !MatchesFilter(entry.Name, files))
        continue;
      WriteFile(outputDir, entry.Name, reader.Extract(entry));
    }
  }

  /// <summary>Creates a canonical GOB v2 archive.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var writer = new GobWriter(output, leaveOpen: true);
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddEntry(name, data);
  }

  /// <summary>
  /// Adds or same-name replaces entries. Canonical archives with a trailing
  /// directory use <see cref="GobInPlaceModifier"/>: changed payload bytes replace
  /// the old directory and a regenerated directory follows them. Unsupported
  /// layouts retain the verified extract/re-create fallback.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = FilesOnly(inputs).ToList();
    if (files.Count == 0)
      return;

    try {
      archive.Position = 0;
      GobInPlaceModifier.AddFiles(archive, files);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var (name, data) in files) {
        var relative = name.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.Combine(temporaryDirectory, relative);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
          Directory.CreateDirectory(directory);
        File.WriteAllBytes(destination, data);
      }
    });
  }

  /// <summary>
  /// Removes entries by rewriting only the trailing directory and wiping
  /// unreferenced removed payload ranges. Unsupported layouts rebuild.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Length == 0)
      return;

    try {
      archive.Position = 0;
      GobInPlaceModifier.RemoveFiles(archive, entryNames, wipeData: true);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    var remove = new HashSet<string>(entryNames.Select(NormalizeMatchName), StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var file in Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)) {
        var relative = NormalizeMatchName(Path.GetRelativePath(temporaryDirectory, file));
        var separator = relative.LastIndexOf('\\');
        var leaf = separator >= 0 ? relative[(separator + 1)..] : relative;
        if (remove.Contains(relative) || remove.Contains(leaf))
          File.Delete(file);
      }
    });
  }

  /// <summary>Rebuild-based defrag.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var reader = new GobReader(stream, leaveOpen: true);
        return reader.Entries.Select(entry => (entry.Name, reader.Extract(entry))).ToList();
      },
      buildImage: files => {
        using var memory = new MemoryStream();
        using (var writer = new GobWriter(memory, leaveOpen: true))
          foreach (var (name, data) in files)
            writer.AddEntry(name, data);
        return memory.ToArray();
      });
  }

  private static string NormalizeMatchName(string name)
    => name.Replace('/', '\\').TrimStart('\\');
}
