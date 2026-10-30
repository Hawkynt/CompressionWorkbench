using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.WebArchive;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.WebArchive;

/// <summary>
/// Safari web archives. Third-party material: two archives saved by Safari and shipped as test data by
/// permissively licensed readers (dwlnetnl/webarchive, BSD-2-Clause; ticky/webarchive, MIT/Apache-2.0),
/// one written by CPython's <c>plistlib</c> with subframes and every remaining plist type, and
/// <c>plistlib-listing.txt</c>, plistlib's view of all three. pywebarchive (MIT), an independent
/// web archive reader, is asked about our output where it is installed. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class WebArchiveTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"WebArchiveVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded web archive vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static readonly WebArchiveFormatDescriptor Descriptor = new();

  private static byte[] Create(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    Descriptor.Create(ms, [.. files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data))], new FormatCreateOptions());
    return ms.ToArray();
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [TestCase(0), TestCase(4096)]
  public void GivenAMandatoryMainResource_WhenPurged_ThenRefusedWithoutChangingTheArchive(int size) {
    var main = Enumerable.Range(0, size).Select(i => (byte)(i * 17)).ToArray();
    var original = Create(("index.html", main), ("asset.bin", [1, 2, 3]));
    using var archive = new MemoryStream(original.ToArray());
    IArchivePurgeable purgeable = Descriptor;

    Assert.Throws<NotSupportedException>(() => purgeable.Purge(archive));
    Assert.That(purgeable.CanPurgeToEmpty, Is.False);
    Assert.That(archive.ToArray(), Is.EqualTo(original));
    Assert.That(new WebArchiveDocument(archive.ToArray()).Resources.Single(r => r.IsMain).Data, Is.EqualTo(main));
  }

  [TestCase("dwlnetnl-example.webarchive", new[] { "index.html" })]
  [TestCase("ticky-crouton.webarchive", new[] { "index.html", "crouton.png" })]
  [TestCase("plistlib-frames.webarchive", new[] { "site/index.html", "site/style.css", "cdn.example.net/img/döt.png_v=1", "site/frame.html", "site/img/dot.png" })]
  [Category("ThirdPartyVector")]
  public void Given_ThirdPartyArchive_When_Read_Then_ResourcesMatchPlistlib(string vector, string[] names) {
    var expected = Encoding.ASCII.GetString(Vector("plistlib-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '))
      .Where(f => f[0] == vector)
      .ToList();
    var doc = new WebArchiveDocument(Vector(vector));
    Assert.That(doc.Resources.Select(r => r.Name), Is.EqualTo(names));
    Assert.That(doc.Resources.Select(r => r.Url), Is.EqualTo(expected.Select(f => f[1])));
    Assert.That(doc.Resources.Select(r => $"{r.Data.Length} {Sha(r.Data)}"), Is.EqualTo(expected.Select(f => $"{f[2]} {f[3]}")));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PlistlibArchive_When_Rewritten_Then_ByteIdenticalToPlistlib() {
    var bytes = Vector("plistlib-frames.webarchive");
    Assert.That(BinaryPlist.Write(BinaryPlist.Read(bytes)), Is.EqualTo(bytes));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_PlistlibArchive_When_Read_Then_EveryPlistTypeDecodes() {
    var root = (OrderedDictionary<string, object?>)BinaryPlist.Read(Vector("plistlib-frames.webarchive"))!;
    var values = (List<object?>)root["XTestValues"]!;
    Assert.That(values[0], Is.EqualTo("Grüße – ünïcödé"));
    Assert.That(values[1], Is.EqualTo(70000L));
    Assert.That(values[2], Is.EqualTo(-5L));
    Assert.That(values[3], Is.EqualTo(1L << 40));
    Assert.That(values[4], Is.EqualTo((Int128)(1UL << 63) + 7));
    Assert.That(values[5], Is.EqualTo(2.5));
    Assert.That(values[6], Is.EqualTo(true));
    Assert.That(values[7], Is.EqualTo(false));
    Assert.That(values[8], Is.EqualTo(new DateTime(2026, 10, 26, 12, 30, 15, DateTimeKind.Utc)));
    Assert.That(values[9], Is.EqualTo(new List<object?> { "nested", 1L }));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_SafariArchive_When_SubresourceRemoved_Then_ResponseAndOtherKeysSurvive() {
    using var ms = new MemoryStream();
    ms.Write(Vector("ticky-crouton.webarchive"));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("extra.css", "p{}"u8.ToArray())]);
    var doc = new WebArchiveDocument(ms.ToArray());
    Assert.That(doc.Resources.Select(r => r.Name), Is.EqualTo(new[] { "index.html", "crouton.png", "extra.css" }));
    Assert.That(doc.Resources[2].Url, Is.EqualTo("https://crouton.net/extra.css"));
    var original = new WebArchiveDocument(Vector("ticky-crouton.webarchive"));
    Assert.That(doc.Resources[1].Dictionary["WebResourceResponse"], Is.EqualTo(original.Resources[1].Dictionary["WebResourceResponse"]));
    Descriptor.Remove(ms, ["extra.css"]);
    var back = new WebArchiveDocument(ms.ToArray());
    Assert.That(back.Resources.Select(r => r.Name), Is.EqualTo(new[] { "index.html", "crouton.png" }));
    Assert.That(back.Resources.Select(r => r.Data), Is.EqualTo(original.Resources.Select(r => r.Data)));
    Assert.That(back.Resources[0].Dictionary.Keys, Is.EqualTo(original.Resources[0].Dictionary.Keys));
  }

  [Test, Category("RoundTrip")]
  public void Given_Files_When_Created_Then_FirstHtmlIsMainAndOthersAreSubresources() {
    var html = "<img src=img/a%20b.png>"u8.ToArray();
    var png = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
    var doc = new WebArchiveDocument(Create(("img/a b.png", png), ("index.html", html)));
    Assert.That(doc.Resources.Select(r => r.Name), Is.EqualTo(new[] { "index.html", "img/a b.png" }));
    Assert.That(doc.Resources[0].IsMain, Is.True);
    Assert.That(doc.Resources[0].MimeType, Is.EqualTo("text/html"));
    Assert.That(doc.Resources[0].Dictionary["WebResourceTextEncodingName"], Is.EqualTo("UTF-8"));
    Assert.That(doc.Resources[1].Url, Is.EqualTo("https://webarchive.invalid/img/a%20b.png"));
    Assert.That(doc.Resources[1].Data, Is.EqualTo(png));
    Assert.That(doc.Resources[0].Data, Is.EqualTo(html));
  }

  [Test, Category("RoundTrip")]
  public void Given_ExistingName_When_Added_Then_DataReplacedInPlace() {
    using var ms = new MemoryStream();
    ms.Write(Create(("index.html", [1]), ("a.css", [2])));
    Descriptor.Add(ms, [ArchiveInputInfo.InMemory("a.css", [7, 7]), ArchiveInputInfo.InMemory("index.html", [8])]);
    var doc = new WebArchiveDocument(ms.ToArray());
    Assert.That(doc.Resources.Select(r => r.Data), Is.EqualTo(new[] { new byte[] { 8 }, [7, 7] }));
  }

  [Test, Category("EdgeCase")]
  public void Given_MainResource_When_Removed_Then_RefusedAndUntouched() {
    var original = Vector("plistlib-frames.webarchive");
    using var ms = new MemoryStream();
    ms.Write(original);
    Assert.Throws<NotSupportedException>(() => Descriptor.Remove(ms, ["site/frame.html"]));
    Assert.Throws<NotSupportedException>(() => Descriptor.Remove(ms, ["site/index.html", "site/style.css"]));
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Given_SubframeSubresource_When_Removed_Then_OnlyItGoes() {
    using var ms = new MemoryStream();
    ms.Write(Vector("plistlib-frames.webarchive"));
    Descriptor.Remove(ms, ["site/img/dot.png"]);
    var doc = new WebArchiveDocument(ms.ToArray());
    Assert.That(doc.Resources.Select(r => r.Name), Is.EqualTo(new[] { "site/index.html", "site/style.css", "cdn.example.net/img/döt.png_v=1", "site/frame.html" }));
  }

  [Test, Category("EdgeCase")]
  public void Given_NoFiles_When_Created_Then_Throws()
    => Assert.Throws<ArgumentException>(() => Create());

  [Test, Category("EdgeCase")]
  public void Given_EmptyMainResource_When_Created_Then_ReadsBackEmpty()
    => Assert.That(Descriptor.List(new MemoryStream(Create(("index.html", []))), null).Single().OriginalSize, Is.Zero);

  [Test, Category("EdgeCase")]
  public void Given_PlistWithoutMainResource_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new WebArchiveDocument(BinaryPlist.Write(new OrderedDictionary<string, object?> { ["x"] = 1L })));

  [Test, Category("EdgeCase")]
  public void Given_XmlPlist_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new WebArchiveDocument("<?xml version=\"1.0\"?><plist version=\"1.0\"><dict/></plist>"u8.ToArray()));

  [Test, Category("EdgeCase")]
  public void Given_TruncatedArchive_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => _ = new WebArchiveDocument(Vector("ticky-crouton.webarchive")[..2000]));

  [Test, Category("EdgeCase")]
  public void Given_OffsetTablePointingPastObjects_When_Read_Then_InvalidData() {
    var bytes = Vector("dwlnetnl-example.webarchive");
    bytes[^1] = 0xFF;
    Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_SelfReferencingArray_When_Read_Then_InvalidData() {
    // bplist00, one object: array of one ref -> object 0 (itself); offset table [8]; trailer.
    byte[] bytes = [.. "bplist00"u8, 0xA1, 0x00, 0x08, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 10];
    Assert.Throws<InvalidDataException>(() => BinaryPlist.Read(bytes));
  }

  [TestCase(14), TestCase(15), TestCase(255), TestCase(256), TestCase(65536)]
  [Category("EdgeCase")]
  public void Given_LengthAtSizeFieldBoundary_When_RoundTripped_Then_Identical(int length) {
    var data = Enumerable.Range(0, length).Select(i => (byte)(i * 7)).ToArray();
    var text = new string('x', length);
    var plist = new OrderedDictionary<string, object?> { ["d"] = data, ["s"] = text, ["a"] = Enumerable.Range(0, Math.Min(length, 300)).Select(i => (object?)(long)i).ToList() };
    var back = (OrderedDictionary<string, object?>)BinaryPlist.Read(BinaryPlist.Write(plist))!;
    Assert.That(back["d"], Is.EqualTo(data));
    Assert.That(back["s"], Is.EqualTo(text));
    Assert.That(back["a"], Is.EqualTo(plist["a"]));
  }

  // ── pywebarchive (MIT) on our output ────────────────────────────────

  private const string PyWebArchiveScript = """
    import hashlib, os, sys
    sys.path[:0] = [p for p in os.environ.get('CWB_PYTHONPATH', '').split(os.pathsep) if p]
    import webarchive
    def show(a):
        for r in [a.main_resource] + list(a.subresources):
            print(r.url, r.mime_type, hashlib.sha256(bytes(r.data)).hexdigest())
        for f in a.subframe_archives:
            show(f)
    with webarchive.open(sys.argv[1]) as a:
        show(a)
        a.extract(sys.argv[2])
    """;

  [Test, Category("ArchiveExternal")]
  public void Given_CreatedAndEditedArchive_When_ReadByPyWebArchive_Then_ResourcesMatch() {
    string? python = null;
    foreach (var candidate in new[] { "python3", "python" })
      try {
        if (Run(candidate, ["-c", "import os, sys; sys.path[:0] = [p for p in os.environ.get('CWB_PYTHONPATH', '').split(os.pathsep) if p]; import webarchive"]).ExitCode == 0) { python = candidate; break; }
      } catch (Win32Exception) {
        // not on the PATH
      }
    if (python == null) { Assert.Ignore("No Python with pywebarchive importable (pip install pywebarchive, or point CWB_PYTHONPATH at it)."); return; }

    var tmp = Path.Combine(Path.GetTempPath(), "cwb_webarchive_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(tmp);
    try {
      var html = "<html><head><link rel=stylesheet href=\"css/a.css\"></head><body>Grüße</body></html>"u8.ToArray();
      var css = "body{}"u8.ToArray();
      var png = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
      var path = Path.Combine(tmp, "page.webarchive");
      File.WriteAllBytes(path, Create(("index.html", html), ("css/a.css", css)));
      using (var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        Descriptor.Add(fs, [ArchiveInputInfo.InMemory("img/x.png", png)]);
      var script = Path.Combine(tmp, "show.py");
      File.WriteAllText(script, PyWebArchiveScript);
      var (exit, stdout, stderr) = Run(python, [script, path, Path.Combine(tmp, "out.html")]);
      Assert.That(exit, Is.Zero, stderr);
      Assert.That(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Is.EqualTo(new[] {
        $"https://webarchive.invalid/index.html text/html {Sha(html)}",
        $"https://webarchive.invalid/css/a.css text/css {Sha(css)}",
        $"https://webarchive.invalid/img/x.png image/png {Sha(png)}",
      }));
      Assert.That(File.Exists(Path.Combine(tmp, "out.html")), Is.True);
    } finally {
      try { Directory.Delete(tmp, recursive: true); } catch { /* best effort */ }
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string file, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(file) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
      StandardOutputEncoding = Encoding.UTF8,
    };
    start.Environment["PYTHONUTF8"] = "1";
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }
}
