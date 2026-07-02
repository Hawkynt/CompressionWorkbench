#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Slf;

/// <summary>
/// Sir-Tech SLF library archive (Jagged Alliance 2).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/ja2-stracciatella/ja2-stracciatella</c> — JA2 Stracciatella — open Jagged Alliance 2 engine; its SLF reader is the open reference</description></item>
///   <item><description>Sir-Tech's library format; no official spec</description></item>
/// </list>
/// </summary>
public sealed class SlfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the SLF archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the SLF archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new SlfReader(stream);
        return r.Entries.Select(e => (e.Name, r.Extract(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        using (var w = new SlfWriter(ms, leaveOpen: true)) {
          foreach (var (n, d) in files) w.AddEntry(n, d);
        }
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    var r = new SlfReader(archive);
    foreach (var e in r.Entries) {
      if (e.Size > 0)
        yield return new DefragBlockInfo(e.Offset, e.Size, DefragBlockKind.Used, FileName: e.Name);
    }
  }

  public string Id => "Slf";
  public string DisplayName => "Sir-Tech SLF";
  public FormatCategory Category => FormatCategory.Archive;
  // R/W: a mutable archive. Add/Replace/Remove go through the verified extract ->
  // edit -> re-create rebuild (default IArchiveModifiable); relayouting the container
  // on edit is honest R/W. See FormatCapabilities.cs (WORM vs R/W).
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".slf";
  public IReadOnlyList<string> Extensions => [".slf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // SLF has no magic bytes — JA2 dispatches purely on the .slf extension. Detection here is extension-only,
  // and the reader's plausibility checks (entry count, in-bounds offsets) catch garbage at parse time.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("slf", "SLF")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Sir-Tech library archive (Jagged Alliance 2)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SlfReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", false, false,
      e.LastModified == DateTime.MinValue ? null : e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SlfReader(stream);
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
    var r = new SlfReader(archive);
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
    using var w = new SlfWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
  }
}
