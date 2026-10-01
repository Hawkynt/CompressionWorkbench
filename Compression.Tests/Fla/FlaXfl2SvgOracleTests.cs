#pragma warning disable CS1591
using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileFormat.Fla;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.Fla;

/// <summary>
/// FLA reading and writing judged by a third-party XFL reader: xfl2svg
/// (https://github.com/PluieElectrique/xfl2svg, MIT), which opens a compressed FLA or an XFL
/// folder, parses the document, the scenes and the library symbols, and renders every frame. The
/// report compares what it saw: every member (via Python's <c>zipfile</c>), the stage, each
/// timeline's frame count and the hash of its rendered SVG, and every media item's data. Adobe
/// Animate itself is not available to any runner, so this is the strongest reader we can ask; it
/// does not prove Animate opens our files. The tests run wherever a Python with xfl2svg installed
/// is on the PATH and are ignored elsewhere.
/// </summary>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class FlaXfl2SvgOracleTests {

  private string _tmp = null!;

  [SetUp]
  public void SetUp() {
    this._tmp = Path.Combine(Path.GetTempPath(), "cwb_flaoracle_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmp, recursive: true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string?> Python = new(FindPythonWithXfl2Svg);

  private static string? FindPythonWithXfl2Svg() {
    foreach (var candidate in new[] { "python3", "python" }) {
      try {
        if (Run(candidate, ["-c", "import xfl2svg.xfl_reader, xfl2svg.svg_renderer"]).ExitCode == 0) return candidate;
      } catch (Win32Exception) {
        // not on the PATH
      }
    }
    return null;
  }

  private static string RequirePython() {
    var python = Python.Value;
    if (python is null) Assert.Ignore("No Python with xfl2svg on the PATH (see Fla/ReferenceVectors/README.md).");
    return python!;
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

  /// <summary>
  /// The report. Three shims adapt xfl2svg to the files and hosts here without touching how it
  /// parses or renders: member paths are joined with '/' (it uses <c>os.path</c>, which breaks
  /// symbol lookups inside a zip on Windows); Flash's central-directory size, overstated by 54,
  /// is corrected on a copy exactly as xfl2svg corrects it (its own interception only works on
  /// Python 3.9); and the 550x400 stage Flash omits when it is the default is filled in.
  /// </summary>
  private const string ReportScript = """
    import hashlib, io, os, posixpath, sys, types, warnings, zipfile
    import xml.etree.ElementTree as ET
    import xfl2svg.xfl_reader as xr
    from xfl2svg.svg_renderer import SvgRenderer

    warnings.simplefilter("ignore")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    xr.os = types.SimpleNamespace(path=posixpath)

    def open_fla(filename):
        data = bytearray(open(filename, "rb").read())
        eocd = data.rfind(b"PK\x05\x06")
        size = int.from_bytes(data[eocd + 12:eocd + 16], "little")
        offset = int.from_bytes(data[eocd + 16:eocd + 20], "little")
        if size - (eocd - offset) == 54:
            data[eocd + 12:eocd + 16] = (eocd - offset).to_bytes(4, "little")
        return zipfile.ZipFile(io.BytesIO(bytes(data))), io.BytesIO()

    xr.open_fla = open_fla

    def with_stage_defaults(load):
        def wrapped(self):
            document = load(self)
            document.attrib.setdefault("width", "550")
            document.attrib.setdefault("height", "400")
            return document
        return wrapped

    for cls in (xr._ZippedXflReader, xr._UnzippedXflReader):
        cls.get_dom_document = with_stage_defaults(cls.get_dom_document)

    def sha(data):
        return hashlib.sha256(data).hexdigest()

    path = sys.argv[1]
    reader = xr.XflReader(path)
    if os.path.isdir(path):
        members = {}
        for root, _, names in os.walk(path):
            for name in names:
                full = os.path.join(root, name)
                members[os.path.relpath(full, path).replace(os.sep, "/")] = open(full, "rb").read()
    else:
        archive = reader.xfl.zip
        infos = [i for i in archive.infolist() if not i.is_dir()]
        members = {i.filename: archive.read(i) for i in infos}
        names = [i.filename for i in infos]
        if "mimetype" in members:
            info = archive.getinfo("mimetype")
            where = "first" if names[0] == "mimetype" else "last" if names[-1] == "mimetype" else "middle"
            print(f"mimetype\t{where}\tmethod={info.compress_type}\textra={len(info.extra)}\t{members['mimetype'].decode('ascii')}")
        else:
            print("mimetype\tabsent")
    for name in sorted(n for n in members if n != "mimetype"):
        print(f"member\t{name}\t{len(members[name])}\t{sha(members[name])}")

    renderer = SvgRenderer(reader)
    print(f"stage\t{reader.stage_width}x{reader.stage_height}\t{reader.stage_color}")
    for kind, timelines in (("scene", reader.get_scene_names()), ("symbol", sorted(reader.get_symbol_names()))):
        for name in timelines:
            timeline = reader.get_timeline(name, kind)
            last = timeline.last_frame if timeline.last_frame is not None else -1
            digest = hashlib.sha256()
            for frame in range(last + 1):
                digest.update(ET.tostring(renderer.render(name, frame, reader.stage_width, reader.stage_height, kind, copy=True).getroot()))
            print(f"{kind}\t{name}\tframes={last + 1}\tsvg={digest.hexdigest()}")
    for item in reader.document.iterfind(".//{*}media/*"):
        data_href = item.get("bitmapDataHRef") or item.get("soundDataHRef")
        blob = members.get(f"bin/{data_href}") if data_href else None
        print(f"media\t{item.tag.split('}')[1]}\t{item.get('name')}\t{data_href}\t{sha(blob) if blob is not None else 'missing'}")
    reader.close()
    """;

  private static string[] Report(string python, string path) {
    var (code, stdout, stderr) = Run(python, ["-c", ReportScript, path]);
    Assert.That(code, Is.EqualTo(0), $"xfl2svg could not read {Path.GetFileName(path)}:\n{stderr}");
    return stdout.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
  }

  private static string[] WithoutMimetype(string[] report) => [.. report.Where(static l => !l.StartsWith("mimetype\t", StringComparison.Ordinal))];

  private static IEnumerable<string> AdobeFiles => FlaReferenceVectors.AdobeFlaFiles;

  private string SaveVector(string vector) {
    var path = Path.Combine(this._tmp, vector);
    File.WriteAllBytes(path, FlaReferenceVectors.Bytes(vector));
    return path;
  }

  /// <summary>Our extraction minus the two files the reader adds of its own.</summary>
  private string ExtractOurs(string fla, string folder) {
    var root = Path.Combine(this._tmp, folder);
    using (var stream = File.OpenRead(fla))
      new FlaFormatDescriptor().Extract(stream, root, null, null);
    File.Delete(Path.Combine(root, "FULL.fla"));
    File.Delete(Path.Combine(root, "metadata.ini"));
    return root;
  }

  private string CreateOurs(string projectRoot, string name, string? method = null) {
    var path = Path.Combine(this._tmp, name);
    using (var output = File.Create(path))
      new FlaFormatDescriptor().Create(output, FlaReferenceVectors.InputsFromFolder(projectRoot), new FormatCreateOptions(method));
    return path;
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenAFlaSavedByFlash_WhenXfl2SvgReadsIt_ThenItSeesWhatTheCapturedReportRecords(string vector) {
    var python = RequirePython();
    Assert.That(Report(python, this.SaveVector(vector)), Is.EqualTo(FlaReferenceVectors.Report(vector)));
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenAFlaSavedByFlash_WhenWeExtractIt_ThenXfl2SvgSeesTheSameProjectInOurFolder(string vector) {
    var python = RequirePython();
    var fla = this.SaveVector(vector);
    var folder = this.ExtractOurs(fla, "extracted");
    File.Delete(Path.Combine(folder, "mimetype"));
    Assert.That(Report(python, folder), Is.EqualTo(WithoutMimetype(Report(python, fla))));
  }

  [Test]
  public void GivenAFlaSavedByFlash_WhenWeListIt_ThenOurMembersAreTheOnesXfl2SvgsZipfileFinds() {
    var python = RequirePython();
    foreach (var vector in FlaReferenceVectors.AdobeFlaFiles) {
      var fla = this.SaveVector(vector);
      var theirs = Report(python, fla).Where(static l => l.StartsWith("member\t", StringComparison.Ordinal)).Select(static l => l.Split('\t')[1]).Append("mimetype");
      using var stream = File.OpenRead(fla);
      var ours = new FlaFormatDescriptor().List(stream, null)
        .Where(static e => !e.IsDirectory && e.Name is not ("FULL.fla" or "metadata.ini")).Select(static e => e.Name);
      Assert.That(ours, Is.EquivalentTo(theirs), vector);
    }
  }

  [TestCaseSource(nameof(AdobeFiles))]
  public void GivenTheProjectInAFlaSavedByFlash_WhenWeRepackIt_ThenXfl2SvgSeesTheIdenticalDocument(string vector) {
    var python = RequirePython();
    var original = this.SaveVector(vector);
    var ours = this.CreateOurs(this.ExtractOurs(original, "project"), "ours.fla");
    Assert.That(Report(python, ours), Is.EqualTo(Report(python, original)));
  }

  [TestCase(null)]
  [TestCase("stored")]
  public void GivenAnXflFolderWithBinaryMedia_WhenWePackIt_ThenXfl2SvgSeesTheFolderAndTheMediaUnchanged(string? method) {
    var python = RequirePython();
    var root = Path.Combine(this._tmp, "test_images");
    FlaReferenceVectors.WriteProject(FlaReferenceVectors.Project("test_images"), root);
    var ours = this.CreateOurs(root, "test_images.fla", method);

    var report = Report(python, ours);
    Assert.That(report[0], Is.EqualTo("mimetype\tlast\tmethod=0\textra=0\tapplication/vnd.adobe.xfl"));
    Assert.That(WithoutMimetype(report), Is.EqualTo(Report(python, root)));
    Assert.That(report.Count(static l => l.StartsWith("media\tDOMBitmapItem\t", StringComparison.Ordinal) && !l.EndsWith("\tmissing", StringComparison.Ordinal)), Is.EqualTo(2));
  }

  [Test]
  public void GivenALibrarySymbolInNestedFoldersWithANonAsciiName_WhenWePackTheProject_ThenXfl2SvgFindsAndRendersIt() {
    var python = RequirePython();
    const string symbol = "Ordner/Unterordner/Ünïcødé 按钮";
    var project = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    using (var source = new System.IO.Compression.ZipArchive(new MemoryStream(FlaReferenceVectors.Bytes("run_as2.fla")), System.IO.Compression.ZipArchiveMode.Read))
      foreach (var entry in source.Entries.Where(static e => !e.FullName.EndsWith('/'))) {
        using var s = entry.Open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        project[entry.FullName] = m.ToArray();
      }
    // Move Flash's 'myButton' into a nested library folder under a non-ASCII name: the file, the
    // document's Include, the instance on the stage and the symbol's own name all follow it.
    static byte[] Rename(byte[] xml, params (string From, string To)[] edits) {
      var text = Encoding.UTF8.GetString(xml);
      foreach (var (from, to) in edits) {
        Assert.That(text, Does.Contain(from));
        text = text.Replace(from, to, StringComparison.Ordinal);
      }
      return Encoding.UTF8.GetBytes(text);
    }
    project["DOMDocument.xml"] = Rename(project["DOMDocument.xml"],
      ("<Include href=\"myButton.xml\"", $"<Include href=\"{symbol}.xml\""),
      ("libraryItemName=\"myButton\"", $"libraryItemName=\"{symbol}\""));
    project[$"LIBRARY/{symbol}.xml"] = Rename(project["LIBRARY/myButton.xml"], ("name=\"myButton\"", $"name=\"{symbol}\""));
    project.Remove("LIBRARY/myButton.xml");
    project.Remove("mimetype");
    var root = Path.Combine(this._tmp, "renamed");
    FlaReferenceVectors.WriteProject(project, root);

    var report = Report(python, this.CreateOurs(root, "renamed.fla"));
    Assert.That(WithoutMimetype(report), Is.EqualTo(Report(python, root)));
    Assert.That(report, Has.Some.StartsWith($"symbol\t{symbol}\tframes=1\t"));
  }
}
