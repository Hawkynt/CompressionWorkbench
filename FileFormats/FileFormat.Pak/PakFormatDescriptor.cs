#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pak;

/// <summary>
/// id Software Quake PAK resource archive ('PACK' header + 64-byte-entry directory).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/id-Software/Quake</c> — released Quake source — the pakfile code is the canonical definition</description></item>
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
        var r = new PakReader(stream);
        var list = new List<(string, byte[])>();
        while (r.GetNextEntry() is { } e)
          list.Add((e.FileName, r.ReadEntryData()));
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new PakWriter(ms);
        foreach (var (n, d) in files) w.AddEntry(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new Arc.ArcReader(archive);
    while (r.GetNextEntry() is { } e) {
      var headerSize = e.Method >= Arc.ArcConstants.MethodStored ? Arc.ArcConstants.NewHeaderSize : Arc.ArcConstants.OldHeaderSize;
      var dataStart = archive.Position;
      var headerStart = dataStart - headerSize;
      yield return new DefragBlockInfo(headerStart, headerSize, DefragBlockKind.MetadataReserved, FileName: "Header: " + e.FileName);
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(dataStart, e.CompressedSize, DefragBlockKind.Used, FileName: e.FileName);
      archive.Position = dataStart + e.CompressedSize;
    }
    var eoaPos = archive.Position - 2;
    if (eoaPos >= 0)
      yield return new DefragBlockInfo(eoaPos, 2, DefragBlockKind.MetadataReserved, FileName: "End-of-archive");
  }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Pak";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PAK";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing PAK archive.
  /// PAK shares the ARC binary layout so this delegates to
  /// <see cref="PakInPlaceModifier"/>, which itself wraps
  /// <see cref="Arc.ArcModifier"/>. Add overwrites only the trailing
  /// end-of-archive marker; Remove walks the entry chain and shifts
  /// trailing bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      PakInPlaceModifier.RemoveFile(archive, name, wipeData: true);
      PakInPlaceModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries via <see cref="PakInPlaceModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      PakInPlaceModifier.RemoveFile(archive, name, wipeData: true);
  }
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".pak";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pak"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("pak", "PAK")];
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
  public string Description => "Quake PAK game resource archive";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new PakReader(stream);
    var entries = new List<ArchiveEntryInfo>();
    var i = 0;
    while (r.GetNextEntry() is { } e)
      entries.Add(new(i++, e.FileName, e.OriginalSize, e.CompressedSize,
        $"Method {e.Method}", false, false, e.LastModified.DateTime));
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new PakReader(stream);
    while (r.GetNextEntry() is { } e) {
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.ReadEntryData());
    }
  }

  /// <summary>
  /// Opens a single PAK entry as a bounded read-only stream. PAK shares the
  /// ARC binary layout: a forward-iterating reader produces per-entry bytes
  /// (decompressed if the entry was stored compressed). The bytes are
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's original length — adjacent entries and trailing padding
  /// are physically unreachable.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new PakReader(archive);
    while (r.GetNextEntry() is { } e) {
      if (!string.Equals(e.FileName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ReadEntryData();
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
    var w = new PakWriter(output);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }
}
