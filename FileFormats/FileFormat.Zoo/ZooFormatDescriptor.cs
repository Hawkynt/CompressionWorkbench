#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Zoo;

public sealed class ZooFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap {

  /// <summary>Rebuild-based defrag: extracts then re-creates the Zoo archive in listing order.</summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>Rebuild-based defrag: extracts then re-creates the Zoo archive per the requested mode.</summary>
  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        var r = new ZooReader(stream);
        return r.Entries.Select(e => (e.EffectiveName, r.ExtractEntry(e)));
      },
      buildImage: files => {
        using var ms = new MemoryStream();
        var w = new ZooWriter(ms);
        foreach (var (n, d) in files) w.AddEntry(n, d);
        w.Finish();
        return ms.ToArray();
      });
  }


  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    archive.Position = 0;
    if (archive.Length < ZooConstants.ArchiveHeaderSize) yield break;

    yield return new DefragBlockInfo(0, ZooConstants.ArchiveHeaderSize, DefragBlockKind.MetadataReserved, FileName: "Zoo Archive Header");

    var r = new ZooReader(archive);
    foreach (var e in r.Entries) {
      if (e.DataOffset > 0 && e.CompressedSize > 0)
        yield return new DefragBlockInfo(e.DataOffset, e.CompressedSize, DefragBlockKind.Used, FileName: e.EffectiveName);
    }
  }

  public string Id => "Zoo";
  public string DisplayName => "ZOO";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Zoo archive.
  /// Uses <see cref="ZooModifier"/> — Add walks the linked-list chain to
  /// the tail, writes a Stored entry at end-of-stream, and patches the
  /// previous tail's <c>nextOffset</c> link.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      ZooModifier.RemoveFile(archive, name, wipeData: true);
      ZooModifier.AddFile(archive, name, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="ZooModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      ZooModifier.RemoveFile(archive, name, wipeData: true);
  }

  public string DefaultExtension => ".zoo";
  public IReadOnlyList<string> Extensions => [".zoo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'Z', (byte)'O', (byte)'O'], Confidence: 0.80)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("lzw", "LZW"), new("store", "Store")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Zoo archive, early DOS compressor by Rahul Dhesi";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ZooReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.EffectiveName, e.OriginalSize, e.CompressedSize,
      e.CompressionMethod.ToString(), false, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ZooReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.EffectiveName, files)) continue;
      WriteFile(outputDir, e.EffectiveName, r.ExtractEntry(e));
    }
  }

  /// <summary>
  /// Opens a single Zoo entry as a bounded read-only <see cref="Stream"/>.
  /// The reader's per-entry extractor returns the fully-decompressed bytes;
  /// they are wrapped in a <see cref="BoundedEntryStream"/> sized to the
  /// entry's original size.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    var r = new ZooReader(archive, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (!string.Equals(e.EffectiveName, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractEntry(e);
      return new BoundedEntryStream(new MemoryStream(bytes, writable: false),
        bytes.Length, leaveOpen: false);
    }
    return new BoundedEntryStream(new MemoryStream(System.Array.Empty<byte>(), writable: false),
      0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var zooMethod = options.MethodName switch {
      "store" => ZooCompressionMethod.Store,
      _ => ZooCompressionMethod.Lzw,
    };
    var w = new ZooWriter(output, defaultMethod: zooMethod);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddEntry(name, data);
    w.Finish();
  }
}
