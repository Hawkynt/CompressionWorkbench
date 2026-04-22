#pragma warning disable CS1591
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ai;

/// <summary>
/// Adobe Illustrator (.ai). Two historical flavors:
/// - PDF-based (CS and later): PDF wrapper with AI-specific private dictionaries.
/// - PostScript-based (older): %!PS-Adobe- DSC with a hex-encoded TIFF thumbnail.
/// Read-only descriptor surfacing the raw file, DSC metadata where applicable,
/// and a decoded thumbnail when present.
/// </summary>
public sealed class AiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ai";
  public string DisplayName => "Adobe Illustrator";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest;
  public string DefaultExtension => ".ai";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [".ai"];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Adobe Illustrator artwork (PDF- or PostScript-based)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var result = new List<ArchiveEntryInfo>();
    try {
      var view = BuildView(stream);
      var idx = 0;
      result.Add(new ArchiveEntryInfo(idx++, "FULL.ai", view.FullBytes.LongLength, view.FullBytes.LongLength, "Stored", false, false, null, Kind: "Passthrough"));
      result.Add(new ArchiveEntryInfo(idx++, "metadata.ini", view.MetadataIni.LongLength, view.MetadataIni.LongLength, "Stored", false, false, null, Kind: "Metadata"));
      result.Add(new ArchiveEntryInfo(idx++, "document.ai", view.FullBytes.LongLength, view.FullBytes.LongLength, "Stored", false, false, null, Kind: view.IsPdfBased ? "PdfDocument" : "PostScriptDocument"));
      if (view.Thumbnail is { } thumb)
        result.Add(new ArchiveEntryInfo(idx++, "thumbnail.tiff", thumb.LongLength, thumb.LongLength, "Stored", false, false, null, Kind: "Thumbnail"));
    } catch {
      // Robust: never throw.
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var view = BuildView(stream);
    if (files == null || MatchesFilter("FULL.ai", files))
      WriteFile(outputDir, "FULL.ai", view.FullBytes);
    if (files == null || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", view.MetadataIni);
    if (files == null || MatchesFilter("document.ai", files))
      WriteFile(outputDir, "document.ai", view.FullBytes);
    if (view.Thumbnail is { } thumb && (files == null || MatchesFilter("thumbnail.tiff", files)))
      WriteFile(outputDir, "thumbnail.tiff", thumb);
  }

  private static AiView BuildView(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var full = ms.ToArray();

    bool isPdf = StartsWith(full, "%PDF-"u8);
    bool isPs = StartsWith(full, "%!PS-Adobe-"u8);

    string? creator = null;
    string? title = null;
    string? createdOn = null;
    string? boundingBox = null;
    string? fonts = null;
    byte[]? thumbnail = null;

    if (isPs) {
      // DSC comments live in the first ~64KB of the file in plain ASCII.
      var headerSlice = full.Length > 65536 ? full[..65536] : full;
      var headerText = Encoding.Latin1.GetString(headerSlice);
      title = DscValue(headerText, "%%Title:");
      creator = DscValue(headerText, "%%Creator:");
      createdOn = DscValue(headerText, "%%CreationDate:");
      boundingBox = DscValue(headerText, "%%BoundingBox:");
      fonts = DscValue(headerText, "%%DocumentFonts:");
      thumbnail = TryDecodePsThumbnail(full);
    } else if (isPdf) {
      // Best-effort creator detection from PDF metadata. Don't decrypt or parse
      // streams — just scrape the Info dictionary textually.
      var headerSlice = full.Length > 65536 ? full[..65536] : full;
      var headerText = Encoding.Latin1.GetString(headerSlice);
      creator = PdfMetadataValue(headerText, "Creator") ?? PdfMetadataValue(headerText, "Producer");
      title = PdfMetadataValue(headerText, "Title");
    }

    var sb = new StringBuilder();
    sb.AppendLine("[ai]");
    sb.AppendLine($"format={(isPdf ? "pdf" : isPs ? "ps" : "unknown")}");
    if (isPdf) sb.AppendLine("note=PDF-based AI — consider FileFormat.Pdf for deeper extraction");
    if (creator != null) sb.AppendLine($"creator={creator}");
    if (title != null) sb.AppendLine($"title={title}");
    if (createdOn != null) sb.AppendLine($"creation_date={createdOn}");
    if (boundingBox != null) sb.AppendLine($"bounding_box={boundingBox}");
    if (fonts != null) sb.AppendLine($"fonts={fonts}");
    sb.AppendLine($"has_thumbnail={(thumbnail != null ? "true" : "false")}");
    var metadataIni = Encoding.UTF8.GetBytes(sb.ToString());

    return new AiView(full, metadataIni, isPdf, isPs, thumbnail);
  }

  private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) {
    if (data.Length < prefix.Length) return false;
    for (var i = 0; i < prefix.Length; i++)
      if (data[i] != prefix[i]) return false;
    return true;
  }

  private static string? DscValue(string text, string tag) {
    var i = text.IndexOf(tag, StringComparison.Ordinal);
    if (i < 0) return null;
    i += tag.Length;
    var end = text.IndexOfAny(['\r', '\n'], i);
    if (end < 0) end = text.Length;
    var value = text[i..end].Trim();
    return string.IsNullOrEmpty(value) ? null : value;
  }

  private static string? PdfMetadataValue(string text, string key) {
    // Match e.g. /Creator (Adobe Illustrator 26.0) or /Creator <hex...>
    var m = Regex.Match(text, @"/" + Regex.Escape(key) + @"\s*\(([^)]*)\)", RegexOptions.Singleline);
    if (m.Success) {
      var v = m.Groups[1].Value.Trim();
      return string.IsNullOrEmpty(v) ? null : v;
    }
    return null;
  }

  /// <summary>
  /// Decodes the PostScript-flavor AI thumbnail. The preview bytes follow an
  /// <c>%%BeginBinary: N</c> (or <c>%AI7_Thumbnail:</c>) marker as hex digits,
  /// one pair per byte, lines prefixed with <c>%</c>. We pull everything between
  /// the begin and end markers and hex-decode it.
  /// </summary>
  private static byte[]? TryDecodePsThumbnail(byte[] full) {
    var text = Encoding.Latin1.GetString(full);
    // Prefer %AI7_Thumbnail: ... %AI7_EndThumbnail when present.
    var begin = text.IndexOf("%AI7_Thumbnail:", StringComparison.Ordinal);
    int end;
    if (begin >= 0) {
      var headerEnd = text.IndexOf('\n', begin);
      if (headerEnd < 0) return null;
      end = text.IndexOf("%AI7_EndThumbnail", headerEnd, StringComparison.Ordinal);
      if (end < 0) return null;
      return HexDecodePreview(text, headerEnd + 1, end);
    }
    // Fall back to %%BeginBinary: N ... %%EndBinary
    var beginBin = Regex.Match(text, @"%%BeginBinary:\s*\d+", RegexOptions.Multiline);
    if (!beginBin.Success) return null;
    var sliceStart = beginBin.Index + beginBin.Length;
    var endBin = text.IndexOf("%%EndBinary", sliceStart, StringComparison.Ordinal);
    if (endBin < 0) return null;
    return HexDecodePreview(text, sliceStart, endBin);
  }

  private static byte[]? HexDecodePreview(string text, int start, int end) {
    var buf = new List<byte>((end - start) / 2);
    int pending = -1;
    for (var i = start; i < end; i++) {
      var c = text[i];
      int v;
      if (c >= '0' && c <= '9') v = c - '0';
      else if (c >= 'a' && c <= 'f') v = c - 'a' + 10;
      else if (c >= 'A' && c <= 'F') v = c - 'A' + 10;
      else continue;
      if (pending < 0) pending = v;
      else {
        buf.Add((byte)((pending << 4) | v));
        pending = -1;
      }
    }
    return buf.Count == 0 ? null : buf.ToArray();
  }

  private sealed record AiView(byte[] FullBytes, byte[] MetadataIni, bool IsPdfBased, bool IsPostScriptBased, byte[]? Thumbnail);
}
