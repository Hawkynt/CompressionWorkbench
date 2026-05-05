#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Djvu;

/// <summary>
/// DjVu document (IFF). Surfaces the full file, a metadata summary, and — depending
/// on the form type — either the individual pages (<c>DJVM</c> multi-page bundle)
/// or the constituent chunks (single-page <c>DJVU</c>). Text-layer (<c>TXTa</c>/
/// <c>TXTz</c>) and annotation (<c>ANTa</c>/<c>ANTz</c>) chunks are surfaced raw;
/// the <c>z</c> variants are ZP-coder compressed and are not decoded.
/// </summary>
public sealed class DjvuFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Djvu";
  public string DisplayName => "DjVu";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".djvu";
  public IReadOnlyList<string> Extensions => [".djvu", ".djv"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // AT&T at offset 0
    new(new byte[] { 0x41, 0x54, 0x26, 0x54 }, Offset: 0, Confidence: 0.97),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "DjVu document (IFF-based). Multi-page bundles surface per-page sub-forms; single pages " +
    "surface their constituent chunks. ZP-coded text/annotation chunks are surfaced raw.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private sealed record Chunk(string Id, int Offset, int Length);

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.djvu", "Track", blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; DjVu document metadata");

    // Must start with AT&T (4) + FORM/FOR (4) + size (4 BE) + formtype (4).
    if (blob.Length < 16
      || blob[0] != 0x41 || blob[1] != 0x54 || blob[2] != 0x26 || blob[3] != 0x54) {
      meta.AppendLine("form_type=unknown");
      entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
      return entries;
    }

    var formId = Encoding.ASCII.GetString(blob, 4, 4);
    var formSize = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8));
    var formType = Encoding.ASCII.GetString(blob, 12, 4);
    meta.Append("form_type=").AppendLine(formType.TrimEnd());
    meta.Append("container=").AppendLine(formId);

    // Top-level layout: "AT&T"(4) + "FORM"(4) + size(4) + formType(4) + chunks.
    // formSize covers formType + chunks, so body-end = 12 + formSize.
    var bodyStart = 16;
    var bodyEnd = Math.Min(12 + formSize, blob.Length);

    var topChunks = ParseChunks(blob, bodyStart, bodyEnd);
    meta.Append("total_chunks=").AppendLine(topChunks.Count.ToString(CultureInfo.InvariantCulture));

    if (formType == "DJVM") {
      // Multi-page: DIRM + (optional NAVM) + a sequence of embedded FORM:DJVU / FORM:DJVI per page.
      var pageIdx = 0;
      foreach (var c in topChunks) {
        if (c.Id != "FORM") continue;
        if (c.Length < 4) continue;
        var subType = Encoding.ASCII.GetString(blob, c.Offset, 4);
        if (subType != "DJVU" && subType != "DJVI") continue;
        // Reconstruct per-page envelope: [AT&T][FORM][uint32 BE: chunk length][FORM body].
        var wrapped = new byte[12 + c.Length];
        Encoding.ASCII.GetBytes("AT&T").CopyTo(wrapped, 0);
        Encoding.ASCII.GetBytes("FORM").CopyTo(wrapped, 4);
        BinaryPrimitives.WriteUInt32BigEndian(wrapped.AsSpan(8, 4), (uint)c.Length);
        Array.Copy(blob, c.Offset, wrapped, 12, c.Length);
        entries.Add(($"pages/page_{pageIdx:D2}.djvu", "Track", wrapped));

        // Also surface per-page text and annotation chunks.
        var subBody = ParseChunks(blob, c.Offset + 4, c.Offset + c.Length);
        foreach (var sc in subBody) {
          var data = blob.AsSpan(sc.Offset, sc.Length).ToArray();
          if (sc.Id is "TXTa" or "TXTz")
            entries.Add(($"text/page_{pageIdx:D2}_text.bin", "Tag", data));
          else if (sc.Id is "ANTa" or "ANTz")
            entries.Add(($"annotations/page_{pageIdx:D2}_anno.bin", "Tag", data));
        }
        pageIdx++;
      }
      meta.Append("page_count=").AppendLine(pageIdx.ToString(CultureInfo.InvariantCulture));
    } else if (formType == "DJVU" || formType == "DJVI") {
      // Single page: flatly emit all chunks.
      var chunkCounts = new Dictionary<string, int>(StringComparer.Ordinal);
      foreach (var c in topChunks) {
        var data = blob.AsSpan(c.Offset, c.Length).ToArray();
        chunkCounts.TryGetValue(c.Id, out var existing);
        chunkCounts[c.Id] = existing + 1;
        var safeId = SanitizeId(c.Id);
        entries.Add(($"chunks/{safeId}_{chunkCounts[c.Id]:D2}.bin", "Tag", data));
        if (c.Id is "TXTa" or "TXTz")
          entries.Add(($"text/page_00_text.bin", "Tag", data));
        else if (c.Id is "ANTa" or "ANTz")
          entries.Add(($"annotations/page_00_anno.bin", "Tag", data));
      }
    }

    entries.Insert(1, ("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));
    return entries;
  }

  private static List<Chunk> ParseChunks(byte[] blob, int start, int end) {
    var list = new List<Chunk>();
    var pos = start;
    while (pos + 8 <= end) {
      var id = Encoding.ASCII.GetString(blob, pos, 4);
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(pos + 4));
      pos += 8;
      if (size < 0 || pos + size > end) break;
      list.Add(new Chunk(id, pos, size));
      pos += size;
      if ((size & 1) == 1 && pos < end) pos++; // pad to 2-byte boundary
    }
    return list;
  }

  private static string SanitizeId(string id) {
    var sb = new StringBuilder(id.Length);
    foreach (var c in id)
      sb.Append(char.IsLetterOrDigit(c) ? c : '_');
    return sb.ToString();
  }
}
