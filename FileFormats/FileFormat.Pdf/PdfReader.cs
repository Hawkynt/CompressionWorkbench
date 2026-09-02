#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileFormat.Pdf;

/// <summary>
/// Reads a PDF file and extracts embedded images and file attachments.
/// </summary>
/// <remarks>
/// Supports extracting JPEG (DCTDecode), JPEG2000 (JPXDecode), raw image
/// streams (FlateDecode), and file attachments (/Type /EmbeddedFile with
/// /Type /Filespec naming). JPEG/JPEG2000 images are returned as-is; raw
/// images are returned as raw pixel data with metadata in the entry.
/// </remarks>
public sealed partial class PdfReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<PdfEntry> _entries = [];
  private readonly List<PdfEntry> _pages = [];
  private readonly Dictionary<int, ImageInfo> _images = [];
  private readonly Dictionary<int, AttachInfo> _attachments = [];

  /// <summary>
  /// Gets the entries.
  /// </summary>
public IReadOnlyList<PdfEntry> Entries => _entries;

  /// <summary>
  /// Per-page slice entries (one self-contained single-page PDF per leaf page).
  /// Kept separate from <see cref="Entries"/> so the existing image/attachment
  /// surface is unchanged for callers that only care about embedded content.
  /// </summary>
  public IReadOnlyList<PdfEntry> PageEntries => _pages;

  /// <summary>
  /// Initializes a new instance of <see cref="PdfReader"/>.
  /// </summary>
public PdfReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
  }

  private sealed class AttachInfo {
    public int ObjectNumber;
    public string FileName = "";
    public long StreamOffset;
    public long StreamLength;
  }

  private sealed class ImageInfo {
    public int ObjectNumber;
    public long StreamOffset;
    public long StreamLength;
    public string Filter = "";
    public int Width;
    public int Height;
    public int BitsPerComponent;
    public string ColorSpace = "";
  }

  private void Parse() {
    var text = Encoding.Latin1.GetString(_data);

    this.ParsePages();

    // Find all objects with image XObjects
    var objMatches = ObjPattern().Matches(text);
    foreach (Match m in objMatches) {
      var objNum = int.Parse(m.Groups[1].Value);
      var objBody = m.Groups[2].Value;

      // Check if this is an image XObject
      if (!objBody.Contains("/Subtype") || !objBody.Contains("/Image"))
        continue;

      var info = new ImageInfo { ObjectNumber = objNum };

      // Parse filter
      var filterMatch = FilterPattern().Match(objBody);
      if (filterMatch.Success)
        info.Filter = filterMatch.Groups[1].Value.Trim();

      // Parse dimensions
      var widthMatch = WidthPattern().Match(objBody);
      if (widthMatch.Success)
        info.Width = int.Parse(widthMatch.Groups[1].Value);

      var heightMatch = HeightPattern().Match(objBody);
      if (heightMatch.Success)
        info.Height = int.Parse(heightMatch.Groups[1].Value);

      var bpcMatch = BpcPattern().Match(objBody);
      if (bpcMatch.Success)
        info.BitsPerComponent = int.Parse(bpcMatch.Groups[1].Value);

      var csMatch = ColorSpacePattern().Match(objBody);
      if (csMatch.Success)
        info.ColorSpace = csMatch.Groups[1].Value.Trim();

      // Find stream data
      var streamStart = FindStreamStart(text, m.Index, m.Length);
      if (streamStart >= 0) {
        // Check for /Length
        var lenMatch = LengthPattern().Match(objBody);
        if (lenMatch.Success && int.TryParse(lenMatch.Groups[1].Value, out var declaredLen)) {
          info.StreamOffset = streamStart;
          info.StreamLength = declaredLen;
        } else {
          // Find endstream
          var endIdx = text.IndexOf("endstream", streamStart, StringComparison.Ordinal);
          if (endIdx > streamStart) {
            info.StreamOffset = streamStart;
            info.StreamLength = endIdx - streamStart;
            // Trim trailing newline
            while (info.StreamLength > 0 && _data[streamStart + info.StreamLength - 1] is 0x0A or 0x0D)
              info.StreamLength--;
          }
        }
      }

      if (info.StreamLength > 0) {
        _images[objNum] = info;

        var ext = info.Filter switch {
          "/DCTDecode" => ".jpg",
          "/JPXDecode" => ".jp2",
          _ => ".raw",
        };

        _entries.Add(new PdfEntry {
          Name = $"image_{objNum}{ext}",
          Size = info.StreamLength,
          ObjectNumber = objNum,
          Filter = info.Filter,
          Width = info.Width,
          Height = info.Height,
        });
      }
    }

    // --- Second pass: extract file attachments (/Type /EmbeddedFile) ---
    // When the file carries a parseable xref + trailer chain (ISO 32000-1 §7.5.4),
    // honour incremental-update tombstones: only objects whose latest xref entry
    // is in-use ('n') are considered live. This lets RemoveFiles tombstone an
    // attachment by emitting a new xref subsection marking its Filespec +
    // EmbeddedFile entries 'f' — their bytes survive but become unreachable.
    var liveObjects = TryBuildLiveObjectSet(text);

    // Build map: stream-object-number → (name, offset, length).
    // First collect Filespec objects to get filenames and their EF stream refs.
    var filespecs = new Dictionary<int, string>(); // stream-obj-number → filename
    var seenFilespecNames = new HashSet<int>();
    foreach (Match m in objMatches) {
      var objNum = int.Parse(m.Groups[1].Value);
      var objBody = m.Groups[2].Value;
      if (!objBody.Contains("/Type") || !objBody.Contains("/Filespec")) continue;
      if (liveObjects != null && !liveObjects.Contains(objNum)) continue;
      var fnMatch = FilespecFnPattern().Match(objBody);
      if (!fnMatch.Success) continue;
      var fn = fnMatch.Groups[1].Value.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");
      // Skip filespecs with empty names — defensive against any in-place
      // tombstone scheme that might null out the name without xref tracking.
      if (string.IsNullOrEmpty(fn)) continue;
      var efMatch = EfRefPattern().Match(objBody);
      if (!efMatch.Success) continue;
      var efObjNum = int.Parse(efMatch.Groups[1].Value);
      filespecs[efObjNum] = fn;
      seenFilespecNames.Add(efObjNum);
    }

    // Now collect EmbeddedFile stream objects referenced by filespecs.
    foreach (Match m in objMatches) {
      var objNum = int.Parse(m.Groups[1].Value);
      if (!filespecs.TryGetValue(objNum, out var fileName)) continue;
      if (liveObjects != null && !liveObjects.Contains(objNum)) continue;
      var objBody = m.Groups[2].Value;
      if (!objBody.Contains("/Type") || !objBody.Contains("/EmbeddedFile")) continue;

      var streamStart = FindStreamStart(text, m.Index, m.Length);
      if (streamStart < 0) continue;
      long streamLen;
      var lenMatch = LengthPattern().Match(objBody);
      if (lenMatch.Success && long.TryParse(lenMatch.Groups[1].Value, out var dl)) {
        streamLen = dl;
      } else {
        var endIdx = text.IndexOf("endstream", streamStart, StringComparison.Ordinal);
        streamLen = endIdx > streamStart ? endIdx - streamStart : 0;
        while (streamLen > 0 && _data[streamStart + streamLen - 1] is 0x0A or 0x0D) streamLen--;
      }
      if (streamLen <= 0) continue;

      var ai = new AttachInfo { ObjectNumber = objNum, FileName = fileName, StreamOffset = streamStart, StreamLength = streamLen };
      _attachments[objNum] = ai;
      _entries.Add(new PdfEntry {
        Name = fileName,
        Size = streamLen,
        ObjectNumber = objNum,
        Filter = "EmbeddedFile",
      });
    }
  }

  /// <summary>
  /// Walks the most recent <c>startxref</c> + every <c>/Prev</c>-linked
  /// trailer to build the set of object numbers whose latest xref entry is
  /// in-use ('n'). Returns <c>null</c> if the file has no parseable xref
  /// (typical for ad-hoc / minimal-test PDFs without proper sections) —
  /// in that case the caller falls back to "treat every lexically present
  /// object as live", matching the original behaviour.
  /// </summary>
  private static HashSet<int>? TryBuildLiveObjectSet(string text) {
    var startXrefIdx = text.LastIndexOf("startxref", StringComparison.Ordinal);
    if (startXrefIdx < 0) return null;
    var p = startXrefIdx + "startxref".Length;
    while (p < text.Length && (text[p] == ' ' || text[p] == '\r' || text[p] == '\n')) p++;
    var start = p;
    while (p < text.Length && text[p] >= '0' && text[p] <= '9') p++;
    if (p == start) return null;
    if (!long.TryParse(text.AsSpan(start, p - start), out var xrefOffset)) return null;

    var latest = new Dictionary<int, char>();
    var visited = new HashSet<long>();
    var current = xrefOffset;
    var anyEntries = false;
    while (current >= 0 && current < text.Length) {
      if (!visited.Add(current)) break;
      if (text[(int)current] != 'x') break;
      if (text.AsSpan((int)current, 4) is not "xref") break;
      var q = (int)current + 4;
      while (q < text.Length && (text[q] == ' ' || text[q] == '\r' || text[q] == '\n')) q++;

      while (q < text.Length) {
        var lineStart = q;
        while (q < text.Length && text[q] != '\n') q++;
        var line = text[lineStart..q].TrimEnd('\r', ' ');
        if (q < text.Length) q++;
        if (line.StartsWith("trailer", StringComparison.Ordinal)) {
          // Walk to "startxref"; chase /Prev.
          var trailerEnd = text.IndexOf("startxref", lineStart, StringComparison.Ordinal);
          if (trailerEnd < 0) trailerEnd = text.Length;
          var trailer = text[lineStart..trailerEnd];
          var prev = PrevPattern().Match(trailer);
          current = prev.Success && long.TryParse(prev.Groups[1].Value, out var pv) ? pv : -1;
          break;
        }
        if (string.IsNullOrEmpty(line)) continue;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var count))
          return null;
        for (var i = 0; i < count; i++) {
          if (q + 20 > text.Length) return null;
          var entry = text.Substring(q, 20);
          q += 20;
          if (entry.Length < 18) continue;
          var objNum = first + i;
          var tag = entry[17];
          anyEntries = true;
          if (!latest.ContainsKey(objNum))
            latest[objNum] = tag;
        }
      }
    }

    if (!anyEntries) return null;
    var live = new HashSet<int>();
    foreach (var (obj, tag) in latest)
      if (tag == 'n') live.Add(obj);
    return live;
  }

  private static int FindStreamStart(string text, int objStart, int objLen) {
    // Look for "stream\r\n" or "stream\n" after the obj definition
    var searchEnd = Math.Min(objStart + objLen + 200, text.Length);
    var idx = text.IndexOf("stream", objStart, searchEnd - objStart, StringComparison.Ordinal);
    if (idx < 0) return -1;
    idx += 6; // skip "stream"
    if (idx < text.Length && text[idx] == '\r') idx++;
    if (idx < text.Length && text[idx] == '\n') idx++;
    return idx;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public byte[] Extract(PdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    // Page-slice entries carry their bytes directly.
    if (entry.PageData is { } pageBytes)
      return pageBytes;

    // Check attachments first.
    if (_attachments.TryGetValue(entry.ObjectNumber, out var attach))
      return _data.AsSpan((int)attach.StreamOffset, (int)attach.StreamLength).ToArray();

    if (!_images.TryGetValue(entry.ObjectNumber, out var info))
      throw new InvalidDataException($"PDF: object {entry.ObjectNumber} not found.");

    var rawStream = _data.AsSpan((int)info.StreamOffset, (int)info.StreamLength).ToArray();

    return info.Filter switch {
      "/DCTDecode" => rawStream, // JPEG — return as-is
      "/JPXDecode" => rawStream, // JPEG 2000 — return as-is
      "/FlateDecode" => DeflateStream(rawStream),
      _ => rawStream, // Unknown filter — return raw
    };
  }

  private static byte[] DeflateStream(byte[] compressed) {
    try {
      // PDF uses zlib (RFC 1950) wrapping around deflate
      using var input = new MemoryStream(compressed);
      using var deflate = new DeflateStream(
        new ZLibStream(input, CompressionMode.Decompress), CompressionMode.Decompress);
      using var output = new MemoryStream();
      deflate.CopyTo(output);
      return output.ToArray();
    } catch {
      // Fallback: try raw deflate (skip 2-byte zlib header manually)
      try {
        if (compressed.Length >= 2) {
          using var input = new MemoryStream(compressed, 2, compressed.Length - 2);
          using var deflate = new DeflateStream(input, CompressionMode.Decompress);
          using var output = new MemoryStream();
          deflate.CopyTo(output);
          return output.ToArray();
        }
      } catch {
        // If all decompression fails, return raw
      }
      return compressed;
    }
  }

  /// <summary>
  /// Splits the PDF into one self-contained single-page PDF per leaf page,
  /// adding a <c>pages/page_NN.pdf</c> entry for each. Failures are silently
  /// swallowed so <c>List()</c> never throws — the existing image/attachment
  /// surface remains the fallback.
  /// </summary>
  private void ParsePages() {
    try {
      var splitter = new PdfPageSplitter(_data);
      var pages = splitter.PageObjectNumbers;
      if (pages.Count == 0) return;
      var width = pages.Count.ToString().Length;
      if (width < 2) width = 2;
      for (var i = 0; i < pages.Count; ++i) {
        byte[] pdfBytes;
        try {
          pdfBytes = splitter.BuildSinglePagePdf(pages[i]);
        } catch {
          continue; // skip an individual page that fails to slice
        }
        var name = $"pages/page_{(i + 1).ToString().PadLeft(width, '0')}.pdf";
        _pages.Add(new PdfEntry {
          Name = name,
          Size = pdfBytes.Length,
          ObjectNumber = pages[i],
          Filter = "Page",
          PageData = pdfBytes,
        });
      }
    } catch {
      // Bounded read: never propagate parse failures from List().
    }
  }

  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
public void Dispose() { }

  [GeneratedRegex(@"(\d+)\s+\d+\s+obj\s*(.*?)endobj", RegexOptions.Singleline)]
  private static partial Regex ObjPattern();

  [GeneratedRegex(@"/Filter\s*(/\w+)")]
  private static partial Regex FilterPattern();

  [GeneratedRegex(@"/Width\s+(\d+)")]
  private static partial Regex WidthPattern();

  [GeneratedRegex(@"/Height\s+(\d+)")]
  private static partial Regex HeightPattern();

  [GeneratedRegex(@"/BitsPerComponent\s+(\d+)")]
  private static partial Regex BpcPattern();

  [GeneratedRegex(@"/ColorSpace\s*(/\w+)")]
  private static partial Regex ColorSpacePattern();

  [GeneratedRegex(@"/Length\s+(\d+)")]
  private static partial Regex LengthPattern();

  [GeneratedRegex(@"/F\s*\(([^)]*)\)")]
  private static partial Regex FilespecFnPattern();

  [GeneratedRegex(@"/EF\s*<<\s*/F\s+(\d+)\s+0\s+R")]
  private static partial Regex EfRefPattern();

  [GeneratedRegex(@"/Prev\s+(\d+)")]
  private static partial Regex PrevPattern();
}
