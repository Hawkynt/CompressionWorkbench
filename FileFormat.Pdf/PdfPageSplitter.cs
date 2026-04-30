#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileFormat.Pdf;

/// <summary>
/// Structurally slices a PDF into one self-contained single-page PDF per leaf
/// page object. No rendering — we copy the page object's transitive reference
/// closure verbatim (streams included), renumber the surviving objects 1..N,
/// and emit a fresh xref + trailer pointing at a synthetic catalog that wraps
/// a single-page Pages tree.
/// </summary>
/// <remarks>
/// Resources shared across pages (e.g. catalogue-level fonts) are duplicated
/// per output rather than de-duplicated; the result is a constant per-page
/// overhead in exchange for each output being independently parseable.
/// </remarks>
internal sealed partial class PdfPageSplitter {

  /// <summary>Object number → (full obj-byte-range start inclusive, end exclusive).</summary>
  private readonly Dictionary<int, (int Start, int End, int BodyStart, int BodyEnd, int? StreamStart, int StreamLength)> _objs = [];

  /// <summary>Leaf /Type /Page object numbers in document order.</summary>
  private readonly List<int> _pageObjs = [];

  private readonly byte[] _data;

  public PdfPageSplitter(byte[] data) {
    this._data = data ?? throw new ArgumentNullException(nameof(data));
    this.IndexObjects();
    this.LocatePages();
  }

  public IReadOnlyList<int> PageObjectNumbers => this._pageObjs;

  /// <summary>
  /// Indexes every <c>N G obj … endobj</c> span in the file together with the
  /// optional <c>stream … endstream</c> byte range. Streams are recorded by
  /// raw byte offset so they can be copied verbatim into the output.
  /// </summary>
  private void IndexObjects() {
    var text = Encoding.Latin1.GetString(this._data);
    foreach (Match m in ObjPattern().Matches(text)) {
      var objNum = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      var bodyStart = m.Groups[2].Index;
      var bodyEnd = bodyStart + m.Groups[2].Length;
      var endobjEnd = m.Index + m.Length; // up to and including "endobj"

      // Locate stream data (if any).
      int? streamStart = null;
      var streamLen = 0;
      var streamIdx = text.IndexOf("stream", bodyStart, m.Length, StringComparison.Ordinal);
      if (streamIdx >= 0) {
        var s = streamIdx + 6;
        if (s < text.Length && text[s] == '\r') ++s;
        if (s < text.Length && text[s] == '\n') ++s;
        // Stream end: find "endstream" between s and endobjEnd.
        var endStreamIdx = text.IndexOf("endstream", s, endobjEnd - s, StringComparison.Ordinal);
        if (endStreamIdx > s) {
          var len = endStreamIdx - s;
          // Trim trailing \r\n preceding "endstream".
          while (len > 0 && this._data[s + len - 1] is 0x0A or 0x0D)
            --len;
          streamStart = s;
          streamLen = len;
        }
      }

      this._objs[objNum] = (m.Index, endobjEnd, bodyStart, bodyEnd, streamStart, streamLen);
    }
  }

  /// <summary>
  /// Walks the page tree from any <c>/Type /Pages</c> root, descending through
  /// <c>/Kids</c> until a <c>/Type /Page</c> leaf is found. Records leaf object
  /// numbers in encounter order. Cycles are guarded by a visited set.
  /// </summary>
  private void LocatePages() {
    // Find page-tree roots: any object with /Type /Pages that's not a child of another /Pages.
    var allPagesObjs = new List<int>();
    foreach (var kv in this._objs) {
      var body = this.GetBody(kv.Key);
      if (PagesTypePattern().IsMatch(body))
        allPagesObjs.Add(kv.Key);
    }
    if (allPagesObjs.Count == 0) return;

    // Build child-of-pages set: every Kid of a /Pages.
    var childOfPages = new HashSet<int>();
    foreach (var pages in allPagesObjs) {
      foreach (var kid in this.ParseKids(pages))
        childOfPages.Add(kid);
    }
    var roots = allPagesObjs.Where(o => !childOfPages.Contains(o)).ToList();
    if (roots.Count == 0) roots = allPagesObjs; // fallback

    var visited = new HashSet<int>();
    foreach (var root in roots)
      this.WalkPageTree(root, visited);
  }

  private void WalkPageTree(int objNum, HashSet<int> visited) {
    if (!visited.Add(objNum)) return;
    if (!this._objs.ContainsKey(objNum)) return;
    var body = this.GetBody(objNum);
    if (PageTypePattern().IsMatch(body)) {
      this._pageObjs.Add(objNum);
      return;
    }
    if (PagesTypePattern().IsMatch(body)) {
      foreach (var kid in this.ParseKids(objNum))
        this.WalkPageTree(kid, visited);
    }
  }

  private IEnumerable<int> ParseKids(int objNum) {
    var body = this.GetBody(objNum);
    var kidsMatch = KidsPattern().Match(body);
    if (!kidsMatch.Success) yield break;
    var inner = kidsMatch.Groups[1].Value;
    foreach (Match r in IndirectRefPattern().Matches(inner))
      yield return int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
  }

  private string GetBody(int objNum) {
    var (_, _, bs, be, _, _) = this._objs[objNum];
    return Encoding.Latin1.GetString(this._data, bs, be - bs);
  }

  /// <summary>
  /// Computes the transitive closure of indirect-object references from the
  /// given seed object. The page object itself is included. The seed page's
  /// <c>/Parent</c> reference is excluded — we synthesise a fresh single-page
  /// Pages tree, so pulling in the original Pages tree would re-introduce
  /// the other pages and inflate the slice. Any <c>/Type /Pages</c> object
  /// encountered transitively is also dropped for the same reason. Missing/
  /// forward references that don't resolve are silently skipped.
  /// </summary>
  private HashSet<int> CollectClosure(int pageObj) {
    var closure = new HashSet<int>();
    var stack = new Stack<int>();

    // Seed: scan page body, skip /Parent and /Type /Pages references.
    if (this._objs.ContainsKey(pageObj)) {
      var seedBody = this.GetBody(pageObj);
      var parentRef = ParentRefPattern().Match(seedBody);
      var bodyForRefs = parentRef.Success
        ? seedBody.Remove(parentRef.Index, parentRef.Length)
        : seedBody;
      foreach (Match r in IndirectRefPattern().Matches(bodyForRefs)) {
        var refObj = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
        if (refObj > 0 && refObj != pageObj) stack.Push(refObj);
      }
    }
    closure.Add(pageObj);

    while (stack.Count > 0) {
      var obj = stack.Pop();
      if (closure.Contains(obj)) continue;
      if (!this._objs.ContainsKey(obj)) continue;
      var body = this.GetBody(obj);
      // Drop transitive Pages tree objects — they would drag in sibling pages.
      if (PagesTypePattern().IsMatch(body)) continue;
      closure.Add(obj);
      foreach (Match r in IndirectRefPattern().Matches(body)) {
        var refObj = int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
        if (refObj > 0 && !closure.Contains(refObj))
          stack.Push(refObj);
      }
    }
    return closure;
  }

  /// <summary>
  /// Builds a self-contained single-page PDF for the given page object. The
  /// page is renumbered to object 3, wrapped by synthetic catalog (1) + Pages
  /// tree (2). All transitively referenced objects are renumbered 4..N. The
  /// page object's <c>/Parent</c> reference is rewritten to point at object 2.
  /// </summary>
  public byte[] BuildSinglePagePdf(int pageObjNum) {
    if (!this._objs.ContainsKey(pageObjNum))
      throw new ArgumentException($"Object {pageObjNum} not present.", nameof(pageObjNum));

    var closure = this.CollectClosure(pageObjNum);
    closure.Remove(pageObjNum); // page goes to slot 3 explicitly

    // Renumbering map: old → new. 1=Catalog, 2=Pages, 3=Page, 4..=closure.
    var renumber = new Dictionary<int, int> { [pageObjNum] = 3 };
    var nextNum = 4;
    foreach (var oldNum in closure.OrderBy(n => n))
      renumber[oldNum] = nextNum++;

    using var ms = new MemoryStream();
    var offsets = new List<long>(); // index = newNum - 1, value = byte offset

    void EmitAscii(string s) {
      var b = Encoding.Latin1.GetBytes(s);
      ms.Write(b, 0, b.Length);
    }
    void EmitRaw(byte[] b, int off, int len) => ms.Write(b, off, len);

    // %PDF-1.4 + binary marker (4 high bytes per spec hint).
    EmitAscii("%PDF-1.4\n");
    EmitRaw([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A], 0, 6);

    // Object 1: Catalog
    offsets.Add(ms.Position);
    EmitAscii("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

    // Object 2: Pages (single kid = object 3)
    offsets.Add(ms.Position);
    EmitAscii("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

    // Object 3: the page (with /Parent rewritten and other refs renumbered)
    offsets.Add(ms.Position);
    this.EmitRenumberedObject(ms, pageObjNum, 3, renumber, isPage: true);

    // Closure objects 4..N
    foreach (var (oldNum, newNum) in renumber.Where(kv => kv.Key != pageObjNum).OrderBy(kv => kv.Value)) {
      offsets.Add(ms.Position);
      this.EmitRenumberedObject(ms, oldNum, newNum, renumber, isPage: false);
    }

    var xrefPos = ms.Position;
    var totalObjs = 1 + offsets.Count; // 0 (free) + N
    EmitAscii($"xref\n0 {totalObjs}\n");
    EmitAscii("0000000000 65535 f \n");
    foreach (var off in offsets)
      EmitAscii(off.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");

    EmitAscii($"trailer\n<< /Size {totalObjs} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF\n");

    return ms.ToArray();
  }

  /// <summary>
  /// Writes a single object whose body has been textually rewritten to use
  /// renumbered indirect references. The stream payload (if any) is copied
  /// byte-for-byte from the input.
  /// </summary>
  private void EmitRenumberedObject(MemoryStream ms, int oldNum, int newNum,
      Dictionary<int, int> renumber, bool isPage) {
    var (_, _, bs, be, streamStart, streamLen) = this._objs[oldNum];

    // The body up to either "stream" or end-of-body. We rewrite the *non-stream*
    // dictionary text only; binary stream payload is copied raw.
    int dictEnd;
    if (streamStart is not null) {
      // Locate the literal "stream" keyword between body-start and stream payload.
      var bodyText = Encoding.Latin1.GetString(this._data, bs, streamStart.Value - bs);
      var idx = bodyText.LastIndexOf("stream", StringComparison.Ordinal);
      dictEnd = idx >= 0 ? bs + idx : streamStart.Value;
    } else {
      dictEnd = be;
    }

    var dictText = Encoding.Latin1.GetString(this._data, bs, dictEnd - bs);
    var rewritten = RewriteRefs(dictText, renumber, oldNum, newNum, isPage, parentTarget: 2);

    var header = Encoding.Latin1.GetBytes($"{newNum} 0 obj\n");
    ms.Write(header, 0, header.Length);
    var rewrittenBytes = Encoding.Latin1.GetBytes(rewritten);
    ms.Write(rewrittenBytes, 0, rewrittenBytes.Length);

    if (streamStart is not null) {
      // Re-emit "stream\n" + raw payload + "\nendstream\n"
      var streamHeader = Encoding.Latin1.GetBytes("stream\n");
      ms.Write(streamHeader, 0, streamHeader.Length);
      ms.Write(this._data, streamStart.Value, streamLen);
      var streamFooter = Encoding.Latin1.GetBytes("\nendstream\n");
      ms.Write(streamFooter, 0, streamFooter.Length);
    }
    var endobj = Encoding.Latin1.GetBytes("endobj\n");
    ms.Write(endobj, 0, endobj.Length);
  }

  /// <summary>
  /// Rewrites every <c>N G R</c> indirect reference in a dictionary text using
  /// the supplied old→new renumber map. References to objects not in the map
  /// are replaced with <c>null</c> so the resulting PDF doesn't reference
  /// non-existent objects. For leaf page objects, the <c>/Parent</c> entry is
  /// rewritten to point at the synthetic Pages object (typically obj 2).
  /// </summary>
  private static string RewriteRefs(string dictText, Dictionary<int, int> renumber,
      int oldNum, int newNum, bool isPage, int parentTarget) {
    var s = dictText;

    // Rewrite /Parent <num> 0 R to point at synthetic Pages obj.
    if (isPage)
      s = ParentRefPattern().Replace(s, $"/Parent {parentTarget} 0 R");

    // General indirect-ref rewrite.
    s = IndirectRefPattern().Replace(s, m => {
      var refObj = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
      if (renumber.TryGetValue(refObj, out var nn))
        return $"{nn} 0 R";
      return "null";
    });

    return s;
  }

  // ── Patterns ─────────────────────────────────────────────────────────────

  [GeneratedRegex(@"(\d+)\s+\d+\s+obj\s*(.*?)endobj", RegexOptions.Singleline)]
  private static partial Regex ObjPattern();

  [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])")]
  private static partial Regex PageTypePattern();

  [GeneratedRegex(@"/Type\s*/Pages(?![a-zA-Z])")]
  private static partial Regex PagesTypePattern();

  [GeneratedRegex(@"/Kids\s*\[(.*?)\]", RegexOptions.Singleline)]
  private static partial Regex KidsPattern();

  [GeneratedRegex(@"(\d+)\s+\d+\s+R")]
  private static partial Regex IndirectRefPattern();

  [GeneratedRegex(@"/Parent\s+\d+\s+\d+\s+R")]
  private static partial Regex ParentRefPattern();
}
