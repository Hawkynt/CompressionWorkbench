using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FileFormat.Pdf;

namespace Compression.Tests.Pdf;

[TestFixture]
public class PdfPerPageTests {

  // ── Fixture builder ──────────────────────────────────────────────────────

  /// <summary>
  /// Builds a 3-page PDF byte-by-byte with computed xref offsets. Each page
  /// has its own /Contents stream containing a tiny text-showing operator
  /// sequence so the per-page slices have something page-specific.
  /// </summary>
  private static byte[] BuildThreePagePdf() {
    var content1 = "BT /F1 12 Tf 100 700 Td (Page 1) Tj ET";
    var content2 = "BT /F1 12 Tf 100 700 Td (Page 2) Tj ET";
    var content3 = "BT /F1 12 Tf 100 700 Td (Page 3) Tj ET";

    var ms = new MemoryStream();
    var offsets = new long[9]; // index 1..8 used

    void Emit(string s) {
      var b = Encoding.Latin1.GetBytes(s);
      ms.Write(b, 0, b.Length);
    }
    void Mark(int idx) => offsets[idx] = ms.Position;

    Emit("%PDF-1.4\n");

    Mark(1); Emit("1 0 obj\n<</Type/Catalog /Pages 2 0 R>>\nendobj\n");
    Mark(2); Emit("2 0 obj\n<</Type/Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3>>\nendobj\n");
    Mark(3); Emit("3 0 obj\n<</Type/Page /Parent 2 0 R /Resources <<>> /MediaBox [0 0 612 792] /Contents 6 0 R>>\nendobj\n");
    Mark(4); Emit("4 0 obj\n<</Type/Page /Parent 2 0 R /Resources <<>> /MediaBox [0 0 612 792] /Contents 7 0 R>>\nendobj\n");
    Mark(5); Emit("5 0 obj\n<</Type/Page /Parent 2 0 R /Resources <<>> /MediaBox [0 0 612 792] /Contents 8 0 R>>\nendobj\n");

    Mark(6); Emit($"6 0 obj\n<</Length {content1.Length}>>\nstream\n{content1}\nendstream\nendobj\n");
    Mark(7); Emit($"7 0 obj\n<</Length {content2.Length}>>\nstream\n{content2}\nendstream\nendobj\n");
    Mark(8); Emit($"8 0 obj\n<</Length {content3.Length}>>\nstream\n{content3}\nendstream\nendobj\n");

    var xrefPos = ms.Position;
    Emit("xref\n0 9\n");
    Emit("0000000000 65535 f \n");
    for (var i = 1; i <= 8; ++i)
      Emit(offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
    Emit($"trailer\n<</Size 9 /Root 1 0 R>>\nstartxref\n{xrefPos}\n%%EOF\n");
    return ms.ToArray();
  }

  // ── Tests ─────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void List_ThreePagePdf_EmitsThreePageEntries() {
    var pdf = BuildThreePagePdf();
    using var ms = new MemoryStream(pdf);
    var desc = new PdfFormatDescriptor();
    var entries = desc.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Has.Member("pages/page_01.pdf"));
    Assert.That(names, Has.Member("pages/page_02.pdf"));
    Assert.That(names, Has.Member("pages/page_03.pdf"));
  }

  [Test, Category("HappyPath")]
  public void Extract_ThreePagePdf_WritesEachPageAsValidPdf() {
    var pdf = BuildThreePagePdf();
    var tmp = Path.Combine(Path.GetTempPath(), "pdf_per_page_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(pdf);
      var desc = new PdfFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      foreach (var i in new[] { 1, 2, 3 }) {
        var path = Path.Combine(tmp, "pages", $"page_{i:D2}.pdf");
        Assert.That(File.Exists(path), Is.True, $"missing page_{i:D2}.pdf");
        var bytes = File.ReadAllBytes(path);
        Assert.That(Encoding.ASCII.GetString(bytes, 0, 5), Is.EqualTo("%PDF-"),
          $"page_{i:D2}.pdf does not start with %PDF-");

        // Parse the output: it must declare exactly one page.
        var text = Encoding.Latin1.GetString(bytes);
        var countMatch = Regex.Match(text, @"/Type\s*/Pages\s*/Kids\s*\[[^\]]*\]\s*/Count\s+(\d+)");
        Assert.That(countMatch.Success, Is.True, $"page_{i:D2}.pdf missing Pages/Count");
        Assert.That(countMatch.Groups[1].Value, Is.EqualTo("1"),
          $"page_{i:D2}.pdf should have exactly one page");
      }
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_PageSlice_IsSelfContained() {
    var pdf = BuildThreePagePdf();
    var tmp = Path.Combine(Path.GetTempPath(), "pdf_self_contained_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(pdf);
      var desc = new PdfFormatDescriptor();
      desc.Extract(ms, tmp, null, null);

      var slicedPath = Path.Combine(tmp, "pages", "page_02.pdf");
      var sliced = File.ReadAllBytes(slicedPath);

      // Re-parse the slice as if it were a fresh PDF.
      using var sliceStream = new MemoryStream(sliced);
      var reReader = new PdfReader(sliceStream);
      // The slice must surface one page entry of its own.
      Assert.That(reReader.PageEntries, Has.Count.EqualTo(1));

      // It must also still start with %PDF-.
      Assert.That(Encoding.ASCII.GetString(sliced, 0, 5), Is.EqualTo("%PDF-"));

      // The slice should contain the original page-2 content stream.
      Assert.That(Encoding.Latin1.GetString(sliced).Contains("Page 2"), Is.True,
        "page_02.pdf should preserve the original page-2 content stream");
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void List_GarbageInput_DoesNotThrow() {
    var bytes = "%PDF-1.4\nthis is not really a pdf body\n%%EOF"u8.ToArray();
    using var ms = new MemoryStream(bytes);
    var desc = new PdfFormatDescriptor();
    Assert.DoesNotThrow(() => {
      var entries = desc.List(ms, null);
      // No pages found is acceptable.
      Assert.That(entries.Any(e => e.Name.StartsWith("pages/")), Is.False);
    });
  }

  [Test, Category("ErrorHandling")]
  public void List_TruncatedHeaderOnly_DoesNotThrow() {
    var bytes = "%PDF-1.4\n"u8.ToArray();
    using var ms = new MemoryStream(bytes);
    var desc = new PdfFormatDescriptor();
    Assert.DoesNotThrow(() => desc.List(ms, null));
  }

  [Test, Category("HappyPath")]
  public void List_ThreePagePdf_EntriesUseTwoDigitNumbering() {
    var pdf = BuildThreePagePdf();
    using var ms = new MemoryStream(pdf);
    var desc = new PdfFormatDescriptor();
    var entries = desc.List(ms, null);
    var pageNames = entries.Where(e => e.Name.StartsWith("pages/")).Select(e => e.Name).ToList();
    foreach (var n in pageNames)
      Assert.That(Regex.IsMatch(n, @"^pages/page_\d{2}\.pdf$"), Is.True,
        $"unexpected page name shape: {n}");
    // No page_00 — numbering is 1-based.
    Assert.That(pageNames, Has.No.Member("pages/page_00.pdf"));
  }
}
