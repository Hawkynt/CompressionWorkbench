using FileFormat.Pdf;

namespace Compression.Tests.Pdf;

[TestFixture]
public class PdfTests {

  // ── Minimal PDF builder ─────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal PDF containing a single inline JPEG image (DCTDecode).
  /// </summary>
  private static byte[] BuildPdfWithJpeg(byte[] jpegData) {
    // Minimal JPEG-in-PDF: the image stream is stored with /DCTDecode filter.
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("%PDF-1.4");

    // Object 1: Catalog
    sb.AppendLine("1 0 obj");
    sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
    sb.AppendLine("endobj");

    // Object 2: Pages
    sb.AppendLine("2 0 obj");
    sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
    sb.AppendLine("endobj");

    // Object 3: Page
    sb.AppendLine("3 0 obj");
    sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 4 0 R >> >> >>");
    sb.AppendLine("endobj");

    // Object 4: Image XObject
    sb.Append("4 0 obj\n");
    sb.Append($"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode /Length {jpegData.Length} >>\n");
    sb.Append("stream\n");

    var headerBytes = System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    var trailerStr = "\nendstream\nendobj\n%%EOF\n";
    var trailerBytes = System.Text.Encoding.Latin1.GetBytes(trailerStr);

    var result = new byte[headerBytes.Length + jpegData.Length + trailerBytes.Length];
    headerBytes.CopyTo(result, 0);
    jpegData.CopyTo(result, headerBytes.Length);
    trailerBytes.CopyTo(result, headerBytes.Length + jpegData.Length);
    return result;
  }

  /// <summary>
  /// Builds a minimal PDF with a raw (unfiltered) image stream.
  /// </summary>
  private static byte[] BuildPdfWithRawImage(byte[] pixelData, int width, int height) {
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("%PDF-1.4");
    sb.AppendLine("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj");
    sb.AppendLine("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj");
    sb.AppendLine("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im0 4 0 R >> >> >> endobj");
    sb.Append($"4 0 obj << /Type /XObject /Subtype /Image /Width {width} /Height {height} /BitsPerComponent 8 /ColorSpace /DeviceRGB /Length {pixelData.Length} >>\nstream\n");

    var headerBytes = System.Text.Encoding.Latin1.GetBytes(sb.ToString());
    var trailerStr = "\nendstream\nendobj\n%%EOF\n";
    var trailerBytes = System.Text.Encoding.Latin1.GetBytes(trailerStr);

    var result = new byte[headerBytes.Length + pixelData.Length + trailerBytes.Length];
    headerBytes.CopyTo(result, 0);
    pixelData.CopyTo(result, headerBytes.Length);
    trailerBytes.CopyTo(result, headerBytes.Length + pixelData.Length);
    return result;
  }

  // Minimal JPEG: SOI + APP0 + minimal scan + EOI
  private static byte[] MakeMinimalJpeg() => [
    0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
    0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
  ];

  // ── Tests ─────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Read_DetectsJpegImage() {
    var jpeg = MakeMinimalJpeg();
    var pdf = BuildPdfWithJpeg(jpeg);
    using var ms = new MemoryStream(pdf);

    var r = new PdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Does.EndWith(".jpg"));
    Assert.That(r.Entries[0].Filter, Is.EqualTo("/DCTDecode"));
  }

  [Test, Category("HappyPath")]
  public void Extract_JpegImage_ReturnsRawJpegData() {
    var jpeg = MakeMinimalJpeg();
    var pdf = BuildPdfWithJpeg(jpeg);
    using var ms = new MemoryStream(pdf);

    var r = new PdfReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(jpeg));
  }

  [Test, Category("HappyPath")]
  public void Read_RawImage_DetectsEntry() {
    var pixels = new byte[2 * 2 * 3]; // 2x2 RGB
    Random.Shared.NextBytes(pixels);
    var pdf = BuildPdfWithRawImage(pixels, 2, 2);
    using var ms = new MemoryStream(pdf);

    var r = new PdfReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Width, Is.EqualTo(2));
    Assert.That(r.Entries[0].Height, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Extract_RawImage_ReturnsPixelData() {
    var pixels = new byte[4 * 4 * 3]; // 4x4 RGB
    for (var i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i & 0xFF);
    var pdf = BuildPdfWithRawImage(pixels, 4, 4);
    using var ms = new MemoryStream(pdf);

    var r = new PdfReader(ms);
    var extracted = r.Extract(r.Entries[0]);
    Assert.That(extracted, Is.EqualTo(pixels));
  }

  [Test, Category("HappyPath")]
  public void Read_EntryProperties() {
    var jpeg = MakeMinimalJpeg();
    var pdf = BuildPdfWithJpeg(jpeg);
    using var ms = new MemoryStream(pdf);

    var r = new PdfReader(ms);
    var entry = r.Entries[0];
    Assert.That(entry.ObjectNumber, Is.EqualTo(4));
    Assert.That(entry.Width, Is.EqualTo(2));
    Assert.That(entry.Height, Is.EqualTo(2));
  }

  [Test, Category("HappyPath")]
  public void Read_NoImages_EmptyEntries() {
    // PDF with no image objects
    var pdfText = "%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n" +
      "2 0 obj << /Type /Pages /Kids [] /Count 0 >> endobj\n%%EOF\n";
    using var ms = new MemoryStream(System.Text.Encoding.Latin1.GetBytes(pdfText));

    var r = new PdfReader(ms);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new PdfFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Pdf"));
    Assert.That(desc.Extensions, Does.Contain(".pdf"));
    Assert.That(desc.MagicSignatures[0].Bytes,
      Is.EqualTo(new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-' }));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByExtension() {
    var format = Compression.Lib.FormatDetector.DetectByExtension("document.pdf");
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Pdf));
  }

  [Test, Category("HappyPath")]
  public void Detect_ByMagic() {
    var jpeg = MakeMinimalJpeg();
    var pdf = BuildPdfWithJpeg(jpeg);
    var format = Compression.Lib.FormatDetector.DetectByMagic(pdf);
    Assert.That(format, Is.EqualTo(Compression.Lib.FormatDetector.Format.Pdf));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_ViaInterface() {
    var jpeg = MakeMinimalJpeg();
    var pdf = BuildPdfWithJpeg(jpeg);
    using var ms = new MemoryStream(pdf);

    var desc = new PdfFormatDescriptor();
    var entries = desc.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Does.EndWith(".jpg"));
  }

  [Test, Category("ErrorHandling")]
  public void Extract_NullEntry_Throws() {
    var pdf = BuildPdfWithJpeg(MakeMinimalJpeg());
    using var ms = new MemoryStream(pdf);
    var r = new PdfReader(ms);
    Assert.Throws<ArgumentNullException>(() => r.Extract(null!));
  }

  // ── WORM creation (file attachments) ───────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsWormCapability() {
    var d = new PdfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_SingleAttachment_RoundTrips() {
    var payload = "hello from pdf attachment"u8.ToArray();
    var w = new PdfWriter();
    w.AddFile("readme.txt", payload);
    using var ms = new MemoryStream();
    w.WriteTo(ms);

    // Verify valid PDF header
    var bytes = ms.ToArray();
    Assert.That(System.Text.Encoding.ASCII.GetString(bytes[..5]), Is.EqualTo("%PDF-"));

    ms.Position = 0;
    var r = new PdfReader(ms);
    var attach = r.Entries.FirstOrDefault(e => e.Name == "readme.txt");
    Assert.That(attach, Is.Not.Null);
    Assert.That(r.Extract(attach!), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_MultipleAttachments_AllRoundTrip() {
    var p1 = "first attachment"u8.ToArray();
    var p2 = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0xFF };

    var w = new PdfWriter();
    w.AddFile("a.txt", p1);
    w.AddFile("b.bin", p2);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new PdfReader(ms);
    var byName = r.Entries.Where(e => e.Filter == "EmbeddedFile").ToDictionary(e => e.Name);
    Assert.That(byName, Has.Count.EqualTo(2));
    Assert.That(r.Extract(byName["a.txt"]), Is.EqualTo(p1));
    Assert.That(r.Extract(byName["b.bin"]), Is.EqualTo(p2));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_RoundTrips() {
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "pdf descriptor test"u8.ToArray());
      var d = new PdfFormatDescriptor();
      using var ms = new MemoryStream();
      ((Compression.Registry.IArchiveFormatOperations)d).Create(
        ms,
        [new Compression.Registry.ArchiveInputInfo(tmp, "test.txt", false)],
        new Compression.Registry.FormatCreateOptions());
      ms.Position = 0;
      var entries = d.List(ms, null);
      Assert.That(entries.Select(e => e.Name), Has.Member("test.txt"));
    } finally {
      File.Delete(tmp);
    }
  }
}
