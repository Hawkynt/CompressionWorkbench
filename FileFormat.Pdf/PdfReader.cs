#pragma warning disable CS1591
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace FileFormat.Pdf;

/// <summary>
/// Reads a PDF file and extracts embedded images.
/// </summary>
/// <remarks>
/// Supports extracting JPEG (DCTDecode), JPEG2000 (JPXDecode), and raw image
/// streams (FlateDecode). JPEG/JPEG2000 images are returned as-is; raw images
/// are returned as raw pixel data with metadata in the entry.
/// </remarks>
public sealed partial class PdfReader : IDisposable {
  private readonly byte[] _data;
  private readonly List<PdfEntry> _entries = [];
  private readonly Dictionary<int, ImageInfo> _images = [];

  public IReadOnlyList<PdfEntry> Entries => _entries;

  public PdfReader(Stream stream, bool leaveOpen = false) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _data = ms.ToArray();
    Parse();
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

  public byte[] Extract(PdfEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);

    if (!_images.TryGetValue(entry.ObjectNumber, out var info))
      throw new InvalidDataException($"PDF: image object {entry.ObjectNumber} not found.");

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
}
