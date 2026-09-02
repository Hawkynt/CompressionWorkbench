#pragma warning disable CS1591
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;

namespace FileFormat.Pdf;

/// <summary>
/// Walks the structural elements of a PDF file and emits the byte-level layout:
/// %PDF header, objects (<c>N 0 obj</c> markers), xref table, and <c>%%EOF</c>
/// marker as <see cref="DefragBlockInfo"/> tiles.
/// </summary>
public static partial class PdfLayoutMap {

  /// <summary>
  /// Enumerates the value.
  /// </summary>
public static IEnumerable<DefragBlockInfo> Enumerate(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;

    if (archive.Length < 5)
      yield break;

    // Read entire file for text-based scanning
    var data = new byte[archive.Length];
    var totalRead = 0;
    while (totalRead < data.Length) {
      var n = archive.Read(data, totalRead, data.Length - totalRead);
      if (n == 0) break;
      totalRead += n;
    }

    var text = Encoding.Latin1.GetString(data, 0, totalRead);

    // %PDF-x.y header (first line)
    var headerEnd = text.IndexOf('\n');
    if (headerEnd < 0) headerEnd = Math.Min(totalRead, 20);
    else headerEnd++; // include newline

    yield return new DefragBlockInfo(0, headerEnd, DefragBlockKind.MetadataReserved,
      FileName: "PDF Header");

    // Find all objects: "N 0 obj ... endobj"
    var objMatches = ObjPattern().Matches(text);
    foreach (Match m in objMatches) {
      var objNum = m.Groups[1].Value;
      var objStart = m.Index;
      var objLen = m.Length;

      // Check if this object contains a stream (has "stream" keyword)
      var objBody = m.Groups[2].Value;
      var hasStream = objBody.Contains("stream");
      var isImage = objBody.Contains("/Subtype") && objBody.Contains("/Image");

      if (hasStream) {
        // Object header (dictionary) up to "stream" keyword
        var streamIdx = text.IndexOf("stream", objStart, objLen, StringComparison.Ordinal);
        if (streamIdx > objStart) {
          var dictLen = streamIdx - objStart;
          yield return new DefragBlockInfo(objStart, dictLen,
            DefragBlockKind.MetadataReserved,
            FileName: $"Object {objNum} dict");

          // Find the stream data bounds
          var streamDataStart = streamIdx + 6; // skip "stream"
          if (streamDataStart < text.Length && text[streamDataStart] == '\r') streamDataStart++;
          if (streamDataStart < text.Length && text[streamDataStart] == '\n') streamDataStart++;

          var endstreamIdx = text.IndexOf("endstream", streamDataStart, StringComparison.Ordinal);
          if (endstreamIdx > streamDataStart) {
            var streamLen = endstreamIdx - streamDataStart;
            // Trim trailing whitespace
            while (streamLen > 0 && data[streamDataStart + streamLen - 1] is 0x0A or 0x0D)
              streamLen--;

            yield return new DefragBlockInfo(streamDataStart, streamLen,
              DefragBlockKind.Used,
              FileName: isImage ? $"Image {objNum}" : $"Stream {objNum}",
              Classification: isImage ? DefragBlockClass.Hot : DefragBlockClass.Normal);

            // endstream + endobj trailer
            var endObjEnd = objStart + objLen;
            if (endstreamIdx < endObjEnd) {
              yield return new DefragBlockInfo(endstreamIdx, endObjEnd - endstreamIdx,
                DefragBlockKind.MetadataReserved,
                FileName: $"Object {objNum} trailer");
            }
          } else {
            // Can't find endstream; emit entire object as Used
            yield return new DefragBlockInfo(objStart, objLen,
              DefragBlockKind.Used, FileName: $"Object {objNum}");
          }
        } else {
          yield return new DefragBlockInfo(objStart, objLen,
            DefragBlockKind.Used, FileName: $"Object {objNum}");
        }
      } else {
        // No stream — entire object is metadata (catalog, pages, font, etc.)
        yield return new DefragBlockInfo(objStart, objLen,
          DefragBlockKind.MetadataReserved,
          FileName: $"Object {objNum}");
      }
    }

    // Find xref table
    var xrefIdx = text.LastIndexOf("xref", StringComparison.Ordinal);
    if (xrefIdx >= 0) {
      // xref table runs until "trailer" keyword
      var trailerIdx = text.IndexOf("trailer", xrefIdx, StringComparison.Ordinal);
      if (trailerIdx > xrefIdx) {
        yield return new DefragBlockInfo(xrefIdx, trailerIdx - xrefIdx,
          DefragBlockKind.MetadataReserved, FileName: "Xref Table");

        // Trailer dictionary
        var startxrefIdx = text.IndexOf("startxref", trailerIdx, StringComparison.Ordinal);
        if (startxrefIdx > trailerIdx) {
          yield return new DefragBlockInfo(trailerIdx, startxrefIdx - trailerIdx,
            DefragBlockKind.MetadataReserved, FileName: "Trailer");
        }
      }
    }

    // %%EOF marker
    var eofIdx = text.LastIndexOf("%%EOF", StringComparison.Ordinal);
    if (eofIdx >= 0) {
      var eofLen = totalRead - eofIdx;
      yield return new DefragBlockInfo(eofIdx, eofLen,
        DefragBlockKind.MetadataReserved, FileName: "%%EOF");
    }
  }

  [GeneratedRegex(@"(\d+)\s+\d+\s+obj\s*(.*?)endobj", RegexOptions.Singleline)]
  private static partial Regex ObjPattern();
}
