#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Awb;

/// <summary>
/// CRI Audio Wave Bank (AFS2) — used by Capcom (Resident Evil, Monster Hunter), Sega
/// (Yakuza, Persona 5), and other CRI Middleware titles. Contains raw codec payloads
/// (HCA, ADX, etc.) which are surfaced verbatim — we do not decode the inner audio.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/vgmstream/vgmstream</c> — vgmstream — implements AFS2/AWB parsing; the de-facto reference</description></item>
///   <item><description>CRI Middleware never published the AFS2 layout; it was recovered by the VGM ripping community</description></item>
/// </list>
/// </summary>
public sealed class AwbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new AwbReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }


  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Awb";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "CRI Audio Wave Bank";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".awb";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".awb", ".acb"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("AFS2"u8.ToArray(), Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("afs2", "AFS2")];
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
public string Description => "CRI Middleware Audio Wave Bank (Capcom / Sega games)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AwbReader(stream, leaveOpen: true);
    var meta = r.BuildMetadataIni();
    var list = new List<ArchiveEntryInfo>(r.Entries.Count + 1);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      list.Add(new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false, null));
    }
    list.Add(new ArchiveEntryInfo(r.Entries.Count, "metadata.ini", meta.Length, meta.Length, "Stored", false, false, null));
    return list;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AwbReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
    if (files == null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", r.BuildMetadataIni());
  }

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. The underlying
  /// reader produces the entry's bytes (decoded if the format compresses
  /// per-entry); the returned stream is a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to the entry's logical length so adjacent entries and any trailing
  /// padding are physically unreachable through this view.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    using var r = new AwbReader(archive, leaveOpen: true);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var meta = r.BuildMetadataIni();
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    foreach (var e in r.Entries) {
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
    using var w = new AwbWriter(output, leaveOpen: true);
    foreach (var (_, data) in FlatFiles(inputs))
      w.AddEntry(data);
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new AwbReader(stream, leaveOpen: true);
        // Materialise so the reader can be disposed before we yield.
        var list = new List<(string, byte[])>(r.Entries.Count);
        foreach (var e in r.Entries) list.Add((e.Name, r.Extract(e)));
        return list;
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new AwbWriter(ms, leaveOpen: true)) {
          foreach (var (_, d) in files) w.AddEntry(d);
        }
        return ms.ToArray();
      });
  }
}
