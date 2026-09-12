#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileFormat.Pdf;

/// <summary>
/// True in-place R/W for PDF file attachments via ISO 32000-1 §7.5.6
/// incremental updates. Every pre-existing byte of the file is preserved
/// byte-identical; mutations are appended after the current EOF as a new
/// section consisting of:
/// <list type="bullet">
///   <item>One revised Catalog object (with the updated /EmbeddedFiles tree).</item>
///   <item>For <see cref="AddFile"/>: one new Filespec + one new EmbeddedFile per input.</item>
///   <item>A new xref subsection covering the revised Catalog and (for Add) the new objects,
///         or for Remove, the freed Filespec + EmbeddedFile entries marked 'f'.</item>
///   <item>A new trailer dictionary with <c>/Prev</c> pointing at the original xref offset.</item>
///   <item>A new <c>startxref</c> + <c>%%EOF</c> tail.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Reader contract</b>: <see cref="PdfReader"/> honours the resulting
/// chain by reading the last trailer's xref + walking /Prev backwards to build
/// a single "live object" map. Free entries ('f') tombstone the original
/// object: its original bytes survive in the file (true in-place — not
/// overwritten) but are unreachable to spec-aware readers.</para>
/// <para><b>Scope</b>: only file attachments under the Catalog's
/// <c>/Names /EmbeddedFiles /Names</c> array are tracked. Images, page
/// content, fonts and other PDF structures are left untouched.</para>
/// </remarks>
public static partial class PdfInPlaceModifier {

  /// <summary>
  /// Appends one or more file attachments to the PDF via an ISO 32000-1
  /// incremental update. The bytes before the original <c>%%EOF</c> stay
  /// byte-identical: only a new section is written at the tail.
  /// </summary>
  public static void AddFile(Stream pdf, string name, byte[] data) {
    ArgumentNullException.ThrowIfNull(pdf);
    ArgumentNullException.ThrowIfNull(name);
    ArgumentNullException.ThrowIfNull(data);
    AddFiles(pdf, [(name, data)]);
  }

  /// <summary>
  /// Bulk variant of <see cref="AddFile"/>: appends every (name, data) pair in
  /// a single incremental update section, so a multi-add commits only one
  /// new xref subsection + trailer.
  /// </summary>
  public static void AddFiles(Stream pdf, IReadOnlyList<(string Name, byte[] Data)> attachments) {
    ArgumentNullException.ThrowIfNull(pdf);
    ArgumentNullException.ThrowIfNull(attachments);
    if (attachments.Count == 0) return;
    if (!pdf.CanSeek || !pdf.CanRead || !pdf.CanWrite)
      throw new ArgumentException("PDF stream must be readable, writable, and seekable.", nameof(pdf));

    var ctx = ReadContext(pdf);
    var nextObj = ctx.MaxObjectNumber + 1;
    var catalogObj = ctx.CatalogObjectNumber;

    // Plan the new objects: per attachment we allocate Filespec + EmbeddedFile.
    var newEntries = new List<(string Name, int FsObj, int EfObj, byte[] Data)>(attachments.Count);
    foreach (var (n, d) in attachments) {
      var fsObj = nextObj++;
      var efObj = nextObj++;
      newEntries.Add((n, fsObj, efObj, d));
    }

    // Build the merged /EmbeddedFiles names list = previous live attachments + new ones.
    var mergedNames = new List<(string Name, int FsObj)>(ctx.LiveAttachments.Count + newEntries.Count);
    foreach (var (n, fs) in ctx.LiveAttachments) mergedNames.Add((n, fs));
    foreach (var e in newEntries) mergedNames.Add((e.Name, e.FsObj));

    // Position at EOF so every byte appended starts strictly after the original file.
    pdf.Position = pdf.Length;

    // Per ISO 32000-1 §7.5.6: an incremental section MUST start on a new line
    // and SHOULD be preceded by an EOL so it's lexically separate from the
    // original "%%EOF" marker.
    EmitNewline(pdf);

    var newObjectOffsets = new List<(int ObjNum, long Offset)>();

    // 1) Emit each new EmbeddedFile stream object.
    foreach (var e in newEntries) {
      newObjectOffsets.Add((e.EfObj, pdf.Position));
      EmitAscii(pdf, $"{e.EfObj} 0 obj\n<< /Type /EmbeddedFile /Length {e.Data.Length} >>\nstream\n");
      pdf.Write(e.Data);
      EmitAscii(pdf, "\nendstream\nendobj\n");
    }

    // 2) Emit each new Filespec object.
    foreach (var e in newEntries) {
      newObjectOffsets.Add((e.FsObj, pdf.Position));
      EmitAscii(pdf,
        $"{e.FsObj} 0 obj\n<< /Type /Filespec /F ({EscapePdfString(e.Name)}) /EF << /F {e.EfObj} 0 R >> >>\nendobj\n");
    }

    // 3) Emit a revised Catalog object pointing at the merged /EmbeddedFiles tree.
    var revisedCatalogOffset = pdf.Position;
    EmitAscii(pdf, $"{catalogObj} 0 obj\n{BuildCatalogDict(ctx, mergedNames)}\nendobj\n");

    // 4) Build the new xref subsection. Append revised Catalog + new objects.
    //    Each subsection covers a contiguous (objnum, count) range. We emit
    //    one subsection per contiguous run for simplicity.
    var entries = new List<(int ObjNum, long Offset, int Gen, char Tag)> {
      (catalogObj, revisedCatalogOffset, 0, 'n'),
    };
    foreach (var (obj, off) in newObjectOffsets)
      entries.Add((obj, off, 0, 'n'));

    var xrefOffset = pdf.Position;
    EmitXref(pdf, entries);

    // 5) Trailer with /Prev = old xref offset.
    var totalSize = nextObj; // /Size = highest obj num + 1; nextObj already that.
    EmitAscii(pdf,
      $"trailer\n<< /Size {totalSize} /Root {catalogObj} 0 R /Prev {ctx.XrefOffset} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
  }

  /// <summary>
  /// Removes named attachments via an ISO 32000-1 incremental update. The
  /// freed objects (Filespec + EmbeddedFile) are tombstoned in the new xref
  /// subsection with the 'f' tag and an incremented generation; their
  /// original on-disk bytes survive untouched but become unreachable to any
  /// spec-aware reader walking the trailer chain.
  /// </summary>
  /// <returns>Number of named entries that were found and freed.</returns>
  public static int RemoveFiles(Stream pdf, IReadOnlyList<string> names) {
    ArgumentNullException.ThrowIfNull(pdf);
    ArgumentNullException.ThrowIfNull(names);
    if (names.Count == 0) return 0;
    if (!pdf.CanSeek || !pdf.CanRead || !pdf.CanWrite)
      throw new ArgumentException("PDF stream must be readable, writable, and seekable.", nameof(pdf));

    var ctx = ReadContext(pdf);
    var catalogObj = ctx.CatalogObjectNumber;

    // Build the surviving attachment list + the set of freed (Filespec, EmbeddedFile) pairs.
    var keep = new List<(string Name, int FsObj)>(ctx.LiveAttachments.Count);
    var freeOps = new List<(int ObjNum, int Gen)>();
    var requested = new HashSet<string>(names, StringComparer.Ordinal);
    var hits = 0;
    foreach (var (n, fs) in ctx.LiveAttachments) {
      if (requested.Contains(n)) {
        hits++;
        var fsGen = ctx.ObjectGenerations.GetValueOrDefault(fs, 0);
        freeOps.Add((fs, (fsGen + 1) & 0xFFFF));
        // Find the EmbeddedFile referenced by this Filespec — we know it from
        // the parse pass, recorded in LiveAttachmentEfMap.
        if (ctx.LiveAttachmentEfMap.TryGetValue(fs, out var efObj)) {
          var efGen = ctx.ObjectGenerations.GetValueOrDefault(efObj, 0);
          freeOps.Add((efObj, (efGen + 1) & 0xFFFF));
        }
      } else {
        keep.Add((n, fs));
      }
    }

    if (hits == 0) return 0;

    pdf.Position = pdf.Length;
    EmitNewline(pdf);

    // 1) Revised Catalog (without the removed entries).
    var revisedCatalogOffset = pdf.Position;
    EmitAscii(pdf, $"{catalogObj} 0 obj\n{BuildCatalogDict(ctx, keep)}\nendobj\n");

    // 2) Xref subsection: revised Catalog (n) + each freed object (f).
    var entries = new List<(int ObjNum, long Offset, int Gen, char Tag)> {
      (catalogObj, revisedCatalogOffset, 0, 'n'),
    };
    foreach (var (obj, gen) in freeOps)
      entries.Add((obj, 0, gen, 'f'));

    var xrefOffset = pdf.Position;
    EmitXref(pdf, entries);

    // 3) Trailer (Size stays at ctx.MaxObjectNumber + 1 — we didn't add objects).
    var totalSize = ctx.MaxObjectNumber + 1;
    EmitAscii(pdf,
      $"trailer\n<< /Size {totalSize} /Root {catalogObj} 0 R /Prev {ctx.XrefOffset} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
    return hits;
  }

  // ── Context parsing ───────────────────────────────────────────────────────

  /// <summary>Result of walking the existing trailer chain from EOF backwards.</summary>
  internal sealed class PdfContext {
    public required long XrefOffset { get; init; }
    public required int CatalogObjectNumber { get; init; }
    public required int MaxObjectNumber { get; init; }
    /// <summary>For every in-use object (latest xref entry has 'n'), maps object number → generation.</summary>
    public required Dictionary<int, int> ObjectGenerations { get; init; }
    /// <summary>Live attachments per the latest /EmbeddedFiles names array (name, Filespec obj number).</summary>
    public required List<(string Name, int FsObj)> LiveAttachments { get; init; }
    /// <summary>Map of Filespec obj number → EmbeddedFile obj number for currently live attachments.</summary>
    public required Dictionary<int, int> LiveAttachmentEfMap { get; init; }
    /// <summary>Cached file text for downstream object lookups.</summary>
    public required string Text { get; init; }
  }

  internal static PdfContext ReadContext(Stream pdf) {
    pdf.Position = 0;
    using var ms = new MemoryStream();
    pdf.CopyTo(ms);
    var data = ms.ToArray();
    var text = Encoding.Latin1.GetString(data);

    // 1) Locate the most recent startxref.
    var xrefOffset = FindLatestStartXref(text);
    if (xrefOffset < 0)
      throw new InvalidDataException("PDF: no startxref marker found — cannot read existing xref table.");

    // 2) Walk the trailer chain backwards via /Prev, collecting latest xref entries per object.
    var latest = new Dictionary<int, (long Offset, int Gen, char Tag)>();
    int? rootObj = null;
    var maxObj = 0;
    var currentXref = xrefOffset;
    var visited = new HashSet<long>();
    while (currentXref >= 0) {
      if (!visited.Add(currentXref)) break;
      if (!TryParseXrefSection(text, (int)currentXref, latest, ref maxObj, out var trailer))
        break;
      rootObj ??= ExtractTrailerRoot(trailer);
      currentXref = ExtractTrailerPrev(trailer);
    }

    if (rootObj is null)
      throw new InvalidDataException("PDF: /Root not found in trailer chain.");

    // 3) Build the set of in-use objects (latest entry tagged 'n') with their generations.
    var liveGens = new Dictionary<int, int>();
    foreach (var (obj, info) in latest)
      if (info.Tag == 'n')
        liveGens[obj] = info.Gen;

    // 4) Walk the Catalog → Names → EmbeddedFiles tree to enumerate live attachments.
    var live = new List<(string Name, int FsObj)>();
    var efMap = new Dictionary<int, int>();
    EnumerateLiveAttachments(text, rootObj.Value, liveGens, live, efMap);

    return new PdfContext {
      XrefOffset = xrefOffset,
      CatalogObjectNumber = rootObj.Value,
      MaxObjectNumber = maxObj,
      ObjectGenerations = liveGens,
      LiveAttachments = live,
      LiveAttachmentEfMap = efMap,
      Text = text,
    };
  }

  private static long FindLatestStartXref(string text) {
    var idx = text.LastIndexOf("startxref", StringComparison.Ordinal);
    if (idx < 0) return -1;
    var p = idx + "startxref".Length;
    while (p < text.Length && (text[p] == ' ' || text[p] == '\r' || text[p] == '\n')) p++;
    var start = p;
    while (p < text.Length && text[p] >= '0' && text[p] <= '9') p++;
    if (p == start) return -1;
    return long.TryParse(text.AsSpan(start, p - start), out var off) ? off : -1;
  }

  private static bool TryParseXrefSection(string text, int xrefOffset,
      Dictionary<int, (long Offset, int Gen, char Tag)> latest, ref int maxObj, out string trailer) {
    trailer = "";
    if (xrefOffset < 0 || xrefOffset + 4 > text.Length) return false;
    if (text[xrefOffset] != 'x' || text.AsSpan(xrefOffset, 4) is not "xref") return false;
    var p = xrefOffset + 4;
    while (p < text.Length && (text[p] == ' ' || text[p] == '\r' || text[p] == '\n')) p++;

    while (p < text.Length) {
      var lineStart = p;
      while (p < text.Length && text[p] != '\n') p++;
      var line = text[lineStart..p].TrimEnd('\r', ' ');
      if (p < text.Length) p++;
      if (line.StartsWith("trailer", StringComparison.Ordinal)) {
        // trailer keyword — capture trailer dict starting here.
        var trailerEnd = text.IndexOf("startxref", lineStart, StringComparison.Ordinal);
        if (trailerEnd < 0) trailerEnd = text.Length;
        trailer = text[lineStart..trailerEnd];
        return latest.Count > 0 || maxObj > 0 || true; // trailer found
      }
      if (string.IsNullOrEmpty(line)) continue;

      // Subsection header: "first count"
      var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length != 2 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var count))
        return false;
      for (var i = 0; i < count; i++) {
        if (p + 20 > text.Length) return false;
        // Each entry is exactly 20 bytes: "nnnnnnnnnn ggggg t \n"
        var entry = text.Substring(p, 20);
        p += 20;
        var objNum = first + i;
        if (objNum > maxObj) maxObj = objNum;
        if (!long.TryParse(entry.AsSpan(0, 10), out var off)) continue;
        if (!int.TryParse(entry.AsSpan(11, 5), out var gen)) continue;
        var tag = entry[17];
        // Only overwrite if not already present (latest xref wins because we walk last-first).
        if (!latest.ContainsKey(objNum))
          latest[objNum] = (off, gen, tag);
      }
    }
    return false;
  }

  private static int? ExtractTrailerRoot(string trailer) {
    var m = TrailerRootPattern().Match(trailer);
    return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
  }

  private static long ExtractTrailerPrev(string trailer) {
    var m = TrailerPrevPattern().Match(trailer);
    return m.Success && long.TryParse(m.Groups[1].Value, out var v) ? v : -1;
  }

  /// <summary>
  /// Walks the Catalog object → /Names /EmbeddedFiles /Names array, collecting
  /// (name, Filespec object number) pairs. The reader's regex scan can't tell
  /// dead from live attachments after a Remove; this xref-aware traversal can.
  /// </summary>
  private static void EnumerateLiveAttachments(string text, int catalogObj,
      Dictionary<int, int> liveGens, List<(string Name, int FsObj)> live, Dictionary<int, int> efMap) {
    if (!TryFindLatestObjectBody(text, catalogObj, liveGens, out var catalogBody)) return;

    // Resolve /Names dictionary (direct or via indirect reference).
    var namesDict = ResolveDictReference(text, catalogBody, "/Names", liveGens);
    if (namesDict is null) return;

    var efDict = ResolveDictReference(text, namesDict, "/EmbeddedFiles", liveGens);
    if (efDict is null) return;

    var namesArray = ExtractInlineArray(efDict, "/Names");
    if (namesArray is null) return;

    // Parse alternating (string) (refnum 0 R) pairs.
    var p = 0;
    while (p < namesArray.Length) {
      while (p < namesArray.Length && char.IsWhiteSpace(namesArray[p])) p++;
      if (p >= namesArray.Length) break;
      if (namesArray[p] != '(') break;
      var nameEnd = FindStringClose(namesArray, p);
      if (nameEnd < 0) break;
      var name = UnescapePdfString(namesArray[(p + 1)..nameEnd]);
      p = nameEnd + 1;
      while (p < namesArray.Length && char.IsWhiteSpace(namesArray[p])) p++;
      // Parse "<objnum> <gen> R"
      var refStart = p;
      while (p < namesArray.Length && namesArray[p] != 'R') p++;
      if (p >= namesArray.Length) break;
      p++; // skip R
      var refStr = namesArray[refStart..(p - 1)].Trim();
      var refParts = refStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (refParts.Length >= 2 && int.TryParse(refParts[0], out var fsObj)) {
        if (liveGens.ContainsKey(fsObj)) {
          // Look up the Filespec body to extract the /EF reference.
          if (TryFindLatestObjectBody(text, fsObj, liveGens, out var fsBody)) {
            var efRefMatch = EfRefPattern().Match(fsBody);
            if (efRefMatch.Success && int.TryParse(efRefMatch.Groups[1].Value, out var efObj)) {
              if (liveGens.ContainsKey(efObj)) {
                live.Add((name, fsObj));
                efMap[fsObj] = efObj;
              }
            }
          }
        }
      }
    }
  }

  /// <summary>
  /// Returns the textual body (between the dict open and "endobj") of the
  /// MOST RECENT live revision of <paramref name="objNum"/>. We scan all
  /// "N 0 obj" matches and select the one whose byte offset matches the
  /// latest xref entry for that object.
  /// </summary>
  private static bool TryFindLatestObjectBody(string text, int objNum,
      Dictionary<int, int> liveGens, out string body) {
    body = "";
    if (!liveGens.TryGetValue(objNum, out _)) return false;

    // The latest xref's offset is the canonical one but we don't carry it back
    // here — instead we trust that the latest "N \d obj" lexical occurrence in
    // the file is the live one (incremental updates append to EOF).
    var pattern = $"\n{objNum} ";
    var lastObjStart = -1;
    var searchFrom = 0;
    while (true) {
      var idx = text.IndexOf(pattern, searchFrom, StringComparison.Ordinal);
      if (idx < 0) break;
      // Match must be followed by "\d+ obj".
      var p = idx + pattern.Length;
      while (p < text.Length && text[p] >= '0' && text[p] <= '9') p++;
      if (p + 4 > text.Length || text.AsSpan(p, 4) is not " obj") {
        searchFrom = idx + 1;
        continue;
      }
      lastObjStart = idx + 1;
      searchFrom = idx + pattern.Length;
    }

    // Handle the (rare) case where the object starts at byte 0 — but PDFs
    // always have a "%PDF-" header so this is purely defensive.
    if (lastObjStart < 0 && text.StartsWith($"{objNum} ", StringComparison.Ordinal))
      lastObjStart = 0;

    if (lastObjStart < 0) return false;

    var endObj = text.IndexOf("endobj", lastObjStart, StringComparison.Ordinal);
    if (endObj < 0) return false;
    body = text[lastObjStart..endObj];
    return true;
  }

  /// <summary>
  /// Looks up <paramref name="key"/> in <paramref name="dictBody"/>. If the
  /// value is an inline dictionary, returns its body. If it's an indirect
  /// reference, resolves it via <see cref="TryFindLatestObjectBody"/>.
  /// </summary>
  private static string? ResolveDictReference(string text, string dictBody, string key,
      Dictionary<int, int> liveGens) {
    var idx = dictBody.IndexOf(key, StringComparison.Ordinal);
    if (idx < 0) return null;
    var p = idx + key.Length;
    while (p < dictBody.Length && char.IsWhiteSpace(dictBody[p])) p++;
    if (p >= dictBody.Length) return null;

    if (dictBody[p] == '<' && p + 1 < dictBody.Length && dictBody[p + 1] == '<') {
      // Inline dictionary
      var depth = 0;
      var start = p;
      while (p < dictBody.Length) {
        if (dictBody[p] == '<' && p + 1 < dictBody.Length && dictBody[p + 1] == '<') {
          depth++;
          p += 2;
          continue;
        }
        if (dictBody[p] == '>' && p + 1 < dictBody.Length && dictBody[p + 1] == '>') {
          depth--;
          p += 2;
          if (depth == 0) return dictBody[start..p];
          continue;
        }
        p++;
      }
      return null;
    }

    // Indirect reference: "N G R"
    if (char.IsDigit(dictBody[p])) {
      var refStart = p;
      while (p < dictBody.Length && dictBody[p] != 'R') p++;
      if (p >= dictBody.Length) return null;
      var refStr = dictBody[refStart..p].Trim();
      var parts = refStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
      if (parts.Length >= 2 && int.TryParse(parts[0], out var obj)) {
        if (TryFindLatestObjectBody(text, obj, liveGens, out var resolved))
          return resolved;
      }
    }
    return null;
  }

  /// <summary>
  /// Extracts the literal <c>[...]</c> contents (without enclosing brackets)
  /// of the array following <paramref name="key"/> inside a dict body. Inline
  /// arrays only — indirect refs are not chased.
  /// </summary>
  private static string? ExtractInlineArray(string dictBody, string key) {
    var idx = dictBody.IndexOf(key, StringComparison.Ordinal);
    if (idx < 0) return null;
    var p = idx + key.Length;
    while (p < dictBody.Length && char.IsWhiteSpace(dictBody[p])) p++;
    if (p >= dictBody.Length || dictBody[p] != '[') return null;
    var start = p + 1;
    var depth = 1;
    p = start;
    while (p < dictBody.Length && depth > 0) {
      var c = dictBody[p];
      if (c == '[') depth++;
      else if (c == ']') depth--;
      else if (c == '(') {
        var close = FindStringClose(dictBody, p);
        if (close < 0) return null;
        p = close;
      }
      p++;
    }
    if (depth != 0) return null;
    return dictBody[start..(p - 1)];
  }

  private static int FindStringClose(string s, int openIdx) {
    var depth = 0;
    var p = openIdx;
    while (p < s.Length) {
      var c = s[p];
      if (c == '\\' && p + 1 < s.Length) { p += 2; continue; }
      if (c == '(') depth++;
      else if (c == ')') {
        depth--;
        if (depth == 0) return p;
      }
      p++;
    }
    return -1;
  }

  // ── Emission ──────────────────────────────────────────────────────────────

  private static void EmitNewline(Stream s) => s.WriteByte((byte)'\n');

  private static void EmitAscii(Stream s, string text) {
    var bytes = Encoding.ASCII.GetBytes(text);
    s.Write(bytes);
  }

  private static void EmitXref(Stream s, List<(int ObjNum, long Offset, int Gen, char Tag)> entries) {
    // Group into contiguous runs to emit one subsection per run.
    entries.Sort((a, b) => a.ObjNum.CompareTo(b.ObjNum));
    var sb = new StringBuilder();
    sb.Append("xref\n");

    var i = 0;
    while (i < entries.Count) {
      var first = entries[i].ObjNum;
      var j = i;
      while (j + 1 < entries.Count && entries[j + 1].ObjNum == entries[j].ObjNum + 1) j++;
      var count = j - i + 1;
      sb.Append(first).Append(' ').Append(count).Append('\n');
      for (var k = i; k <= j; k++) {
        var e = entries[k];
        sb.Append(e.Offset.ToString("D10", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(e.Gen.ToString("D5", CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(e.Tag);
        sb.Append(" \n"); // entries must be exactly 20 bytes ending in " \n"
      }
      i = j + 1;
    }

    EmitAscii(s, sb.ToString());
  }

  private static string BuildCatalogDict(PdfContext ctx, List<(string Name, int FsObj)> attachments) {
    // Preserve the original Catalog's /Pages reference if present. We rebuild a
    // minimal valid Catalog dict carrying the same /Pages target and the new
    // /Names /EmbeddedFiles tree.
    var pagesRef = ExtractPagesReference(ctx);

    var sb = new StringBuilder();
    sb.Append("<< /Type /Catalog");
    if (!string.IsNullOrEmpty(pagesRef))
      sb.Append(" /Pages ").Append(pagesRef);
    if (attachments.Count > 0) {
      sb.Append(" /Names << /EmbeddedFiles << /Names [");
      for (var i = 0; i < attachments.Count; i++) {
        if (i > 0) sb.Append(' ');
        sb.Append('(').Append(EscapePdfString(attachments[i].Name)).Append(") ");
        sb.Append(attachments[i].FsObj).Append(" 0 R");
      }
      sb.Append("] >> >>");
    } else {
      // Empty /Names — emit so spec-aware readers see an empty attachments tree.
      sb.Append(" /Names << /EmbeddedFiles << /Names [] >> >>");
    }
    sb.Append(" >>");
    return sb.ToString();
  }

  private static string ExtractPagesReference(PdfContext ctx) {
    if (!TryFindLatestObjectBody(ctx.Text, ctx.CatalogObjectNumber, ctx.ObjectGenerations, out var body))
      return "";
    var m = PagesPattern().Match(body);
    return m.Success ? m.Groups[1].Value : "";
  }

  // ── String helpers ────────────────────────────────────────────────────────

  private static string EscapePdfString(string s) =>
    s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

  private static string UnescapePdfString(string s) =>
    s.Replace("\\(", "(").Replace("\\)", ")").Replace("\\\\", "\\");

  [GeneratedRegex(@"/Prev\s+(\d+)")]
  private static partial Regex TrailerPrevPattern();

  [GeneratedRegex(@"/Root\s+(\d+)\s+\d+\s+R")]
  private static partial Regex TrailerRootPattern();

  [GeneratedRegex(@"/Pages\s+(\d+\s+\d+\s+R)")]
  private static partial Regex PagesPattern();

  [GeneratedRegex(@"/EF\s*<<\s*/F\s+(\d+)\s+0\s+R")]
  private static partial Regex EfRefPattern();
}
