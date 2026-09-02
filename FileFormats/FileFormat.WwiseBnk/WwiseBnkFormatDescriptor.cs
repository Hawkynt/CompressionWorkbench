#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.WwiseBnk;

/// <summary>
/// Audiokinetic Wwise SoundBank (.bnk) — BKHD/DIDX/DATA/HIRC chunked container.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/bnnm/wwiser</c> — wwiser — most complete open .bnk parser (community reverse engineering)</description></item>
///   <item><description><c>https://github.com/eXpl0it3r/bnkextr</c> — bnkextr — minimal BKHD/DIDX/DATA extractor</description></item>
///   <item><description><c>https://www.audiokinetic.com/</c> — Audiokinetic — vendor; the bank format itself is not publicly documented</description></item>
/// </list>
/// </summary>
public sealed class WwiseBnkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "WwiseBnk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Wwise SoundBank";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bnk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bnk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("BKHD"u8.ToArray(), Confidence: 0.9)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("bnk", "Wwise SoundBank")];
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
public string Description => "Audiokinetic Wwise SoundBank container (BKHD/DIDX/DATA/HIRC)";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WwiseBnkReader(stream);
    var list = new List<ArchiveEntryInfo>();
    int idx = 0;
    list.Add(new ArchiveEntryInfo(idx++, "FULL.bnk", stream.Length, stream.Length, "Stored", false, false, null, Kind: "Track"));
    list.Add(new ArchiveEntryInfo(idx++, "metadata.ini", 0, 0, "Stored", false, false, null, Kind: "Tag"));
    if (r.HircObjects.Count > 0)
      list.Add(new ArchiveEntryInfo(idx++, "hirc_objects.txt", 0, 0, "Stored", false, false, null, Kind: "Tag"));
    // Per-section raw blobs (sections/BKHD.bin, sections/HIRC.bin, …) for every
    // top-level chunk, indexed in file order via ChunkSpans.
    foreach (var (tag, span) in r.ChunkSpans)
      list.Add(new ArchiveEntryInfo(idx++, SectionName(tag), span.Length, span.Length, "Stored", false, false, null, Kind: "Section"));
    foreach (var w in r.Wems)
      list.Add(new ArchiveEntryInfo(idx++, $"wems/{w.WemId}.wem", w.Size, w.Size, "Stored", false, false, null, Kind: "Sample"));
    return list;
  }

  /// <summary>
  /// Opens a single bank entry as a bounded read-only stream. Handles the
  /// synthetic <c>FULL.bnk</c> passthrough, the <c>metadata.ini</c>
  /// summary, <c>hirc_objects.txt</c>, and per-WEM positional slices. All
  /// returns are wrapped in
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to their logical length so adjacent regions can't leak.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    if (string.Equals(entryName, "FULL.bnk", StringComparison.OrdinalIgnoreCase)) {
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new Compression.Registry.Streaming.ReadOnlyStreamSlice(archive, 0, archive.Length),
        archive.Length, leaveOpen: false);
    }
    var r = new WwiseBnkReader(archive);
    if (string.Equals(entryName, "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
      var sb = new StringBuilder();
      sb.AppendLine("[wwise_bnk]");
      sb.AppendLine($"version={r.BankVersion}");
      sb.AppendLine($"bank_id=0x{r.BankId:X8}");
      sb.AppendLine($"hirc_object_count={r.HircObjects.Count}");
      sb.AppendLine($"wem_count={r.Wems.Count}");
      sb.AppendLine($"chunks={string.Join(",", r.Chunks.Keys)}");
      var meta = Encoding.UTF8.GetBytes(sb.ToString());
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(meta, writable: false), meta.Length, leaveOpen: false);
    }
    if (string.Equals(entryName, "hirc_objects.txt", StringComparison.OrdinalIgnoreCase)) {
      var sb = new StringBuilder();
      foreach (var h in r.HircObjects)
        sb.AppendLine($"{h.Type} 0x{h.Id:X8} {h.Size}");
      var hirc = Encoding.UTF8.GetBytes(sb.ToString());
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(hirc, writable: false), hirc.Length, leaveOpen: false);
    }
    foreach (var (tag, _) in r.ChunkSpans) {
      if (!string.Equals(SectionName(tag), entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractChunk(tag);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    foreach (var w in r.Wems) {
      var name = $"wems/{w.WemId}.wem";
      if (!string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      var bytes = r.ExtractWem(w);
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

  /// <inheritdoc />
  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    using var s = this.OpenEntry(input, entryName, password);
    s.CopyTo(output);
  }

  private static string SectionName(string tag) => $"sections/{tag.Trim()}.bin";

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WwiseBnkReader(stream);

    if (files == null || MatchesFilter("FULL.bnk", files)) {
      stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      WriteFile(outputDir, "FULL.bnk", ms.ToArray());
    }

    if (files == null || MatchesFilter("metadata.ini", files)) {
      var sb = new StringBuilder();
      sb.AppendLine("[wwise_bnk]");
      sb.AppendLine($"version={r.BankVersion}");
      sb.AppendLine($"bank_id=0x{r.BankId:X8}");
      sb.AppendLine($"hirc_object_count={r.HircObjects.Count}");
      sb.AppendLine($"wem_count={r.Wems.Count}");
      sb.AppendLine($"chunks={string.Join(",", r.Chunks.Keys)}");
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    if (r.HircObjects.Count > 0 && (files == null || MatchesFilter("hirc_objects.txt", files))) {
      var sb = new StringBuilder();
      foreach (var h in r.HircObjects)
        sb.AppendLine($"{h.Type} 0x{h.Id:X8} {h.Size}");
      WriteFile(outputDir, "hirc_objects.txt", Encoding.UTF8.GetBytes(sb.ToString()));
    }

    foreach (var (tag, _) in r.ChunkSpans) {
      var name = SectionName(tag);
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ExtractChunk(tag));
    }

    foreach (var w in r.Wems) {
      var name = $"wems/{w.WemId}.wem";
      if (files != null && !MatchesFilter(name, files)) continue;
      WriteFile(outputDir, name, r.ExtractWem(w));
    }
  }
}
