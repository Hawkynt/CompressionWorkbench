#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nsa;

/// <summary>
/// NScripter NSA game-data archive (entry table + data-offset header).
///
/// References:
/// <list type="bullet">
///   <item><description>ONScripter (Ogapee) — the open NScripter engine whose NsaReader is the de-facto format reference</description></item>
///   <item><description>No official specification — NScripter is proprietary; the container is documented by the visual-novel tooling community</description></item>
/// </list>
/// </summary>
public sealed class NsaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new NsaReader(archive);
    foreach (var e in r.Entries) {
      if (e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.Offset, e.CompressedSize, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Nsa";
  public string DisplayName => "NSA";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nsa";
  public IReadOnlyList<string> Extensions => [".nsa"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods =>
    [new("none", "None"), new("lzss", "LZSS"), new("nbz", "NBZ")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NScripter NSA archive. In-place edits are impossible by header design: the " +
    "6-byte header carries `uint32 BE data_offset` pointing to where the data " +
    "area starts; the variable-length index between the header and the data " +
    "area must grow whenever an entry is added, which shifts data_offset and " +
    "every byte after it. Modify is therefore served by the verified extract -> " +
    "edit -> re-create rebuild (stored mode only — the LZSS and NBZ decoders " +
    "have no paired encoders), relayouting the container on every edit.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NsaReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name,
      e.OriginalSize, e.CompressedSize,
      e.CompressionType.ToString(), false, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NsaReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
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
    var r = new NsaReader(archive);
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new NsaWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, i.ReadContent());
    }
    w.WriteTo(output);
  }

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new NsaReader(stream, leaveOpen: true);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        var w = new NsaWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        using var ms = new MemoryStream();
        w.WriteTo(ms);
        return ms.ToArray();
      });
  }
}
