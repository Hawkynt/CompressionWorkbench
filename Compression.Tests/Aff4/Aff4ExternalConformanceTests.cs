using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileFormat.Aff4;

namespace Compression.Tests.Aff4;

/// <summary>
/// Our AFF4-L volumes and reader checked by independent implementations:
/// <list type="bullet">
///   <item><description>pyaff4, the AFF4 reference implementation, opens what we write, reads every
///   file byte for byte and verifies every stored aff4:hash with its own hasher;</description></item>
///   <item><description>rdflib parses information.turtle and the hashes file, and every ZipSegment
///   is bound to a ZIP member of the recorded size and SHA-256;</description></item>
///   <item><description>our reader lists and extracts pyaff4's own AFF4-L reference image
///   <c>unicode.aff4</c> (14 MB, so fetched rather than committed) exactly as pyaff4 does.</description></item>
/// </list>
/// Each test skips when its tool or file is missing.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class Aff4ExternalConformanceTests {
  private const string RdflibChecker = """
import sys, zipfile, hashlib, rdflib
AFF4 = rdflib.Namespace("http://aff4.org/Schema#")
z = zipfile.ZipFile(sys.argv[1])
assert z.testzip() is None, "zip CRC"
names = z.namelist()
assert names[0] == "container.description", names[0]
volume = z.read("container.description").decode()
assert z.comment.decode() == volume, "zip comment"
turtle = z.read("information.turtle")
g = rdflib.Graph(); g.parse(data=turtle, format="turtle")
hashes = rdflib.Graph(); hashes.parse(data=z.read("information.turtle.hashes"), format="turtle")
recorded = [str(o) for o in hashes.objects(None, AFF4.hash)]
assert hashlib.sha256(turtle).hexdigest() in recorded, "turtle hash"
count = 0
for s in g.subjects(rdflib.RDF.type, AFF4.ZipSegment):
    assert (s, rdflib.RDF.type, AFF4.FileImage) in g
    data = z.read(str(s))
    assert int(g.value(s, AFF4.size)) == len(data), str(s)
    assert str(g.value(s, AFF4.hash)) == hashlib.sha256(data).hexdigest(), str(s)
    assert g.value(s, AFF4.originalPathName) is not None and g.value(s, AFF4.fileName) is not None
    count += 1
folders = len(list(g.subjects(rdflib.RDF.type, AFF4.Folder)))
print("OK", count, folders)
""";

  // pyaff4's own extraction (aff4.py extractAllFromVolume) and verification (LinearHasher2) code
  // paths, run directly so that the AFF4-L version 2.1 label does not route the volume to pyaff4's
  // pre-standard winpmem branch: pyaff4 predates the AFF4-L draft and only treats 1.1 as logical.
  private const string Pyaff4Oracle = """
import hashlib, sys
from pyaff4 import aff4, container, lexicon, linear_hasher, rdfvalue

class Listener(object):
    def __init__(self):
        self.ok = 0
        self.bad = []
    def onValidHash(self, typ, value, urn):
        self.ok += 1
    def onInvalidHash(self, typ, stored, calculated, urn):
        self.bad.append("%s %s" % (urn, typ))

with container.Container.openURNtoContainer(rdfvalue.URN.FromFileName(sys.argv[1])) as volume:
    resolver = volume.resolver
    listener = Listener()
    hasher = linear_hasher.LinearHasher2(resolver, listener)
    for urn in resolver.QueryPredicateObject(volume.urn, lexicon.AFF4_TYPE, lexicon.standard11.FileImage):
        path = next(resolver.QuerySubjectPredicate(volume.urn, urn, lexicon.standard11.pathName)).value
        with resolver.AFF4FactoryOpen(urn) as stream:
            data = stream.read(1 << 30)
        sys.stdout.buffer.write(("FILE\t%s\t%d\t%s\n" % (path, len(data), hashlib.sha256(data).hexdigest())).encode("utf-8"))
        hasher.hash(aff4.LogicalImage(volume, resolver, volume.urn, urn, path))
    sys.stdout.buffer.write(("HASHES\t%d\t%d\n" % (listener.ok, len(listener.bad))).encode("utf-8"))
    for failure in listener.bad:
        sys.stdout.buffer.write(("BAD\t%s\n" % failure).encode("utf-8"))
""";

  private static readonly ArchiveInputInfo[] Inputs = BuildInputs();

  private static ArchiveInputInfo[] BuildInputs() {
    var random = new byte[200_000];
    new Random(416).NextBytes(random);
    return [
      ArchiveInputInfo.InMemory("evidence/random.bin", random),
      ArchiveInputInfo.InMemory("evidence/deep/note \"quoted\".txt", "note"u8),
      ArchiveInputInfo.InMemory("evidence/Grüße ネコ.txt", "umlaut ü\n"u8),
      ArchiveInputInfo.InMemory("tab\tname.txt", "tab"u8),
      ArchiveInputInfo.InMemory("empty.bin", ReadOnlySpan<byte>.Empty),
      new ArchiveInputInfo("emptydir", "emptydir", IsDirectory: true),
    ];
  }

  // ── pyaff4 ────────────────────────────────────────────────────────────────

  [TestCase("deflate", "sha256")]
  [TestCase("stored", "sha256")]
  [TestCase("deflate", "md5,sha1,sha256,sha512")]
  public void GivenOurVolume_WhenPyaff4ReadsIt_ThenEveryFileIsIdenticalAndEveryHashVerifies(string method, string hashes) {
    var python = Pyaff4Python.Require();
    var dir = NewTempDir();
    try {
      var volume = Path.Combine(dir, "ours.aff4");
      using (var fs = File.Create(volume))
        new Aff4FormatDescriptor().Create(fs, Inputs, new FormatCreateOptions(method) {
          FormatSpecific = new(StringComparer.OrdinalIgnoreCase) { ["Hashes"] = hashes },
        });

      var (files, hashOk, hashBad, output) = python.Read(dir, volume);
      var expectedFiles = Inputs.Where(i => !i.IsDirectory).ToList();
      Assert.That(files, Has.Count.EqualTo(expectedFiles.Count), output);
      foreach (var input in expectedFiles) {
        // Control characters are %XX-escaped in the legacy name (AFF4-L section 5).
        var name = input.ArchiveName.Replace("\t", "%09", StringComparison.Ordinal);
        Assert.That(files, Does.ContainKey(name), output);
        var data = input.InMemoryContent!;
        Assert.That(files[name].Size, Is.EqualTo(data.LongLength), name);
        Assert.That(files[name].Sha256, Is.EqualTo(Sha256(data)), name);
      }
      Assert.That(hashBad, Is.Zero, output);
      Assert.That(hashOk, Is.EqualTo(expectedFiles.Count * hashes.Split(',').Length), output);
    } finally {
      TryDelete(dir);
    }
  }

  [Test]
  public void GivenPyaff4ReferenceImage_WhenWeAndPyaff4ReadIt_ThenBothSeeTheSameFiles() {
    var python = Pyaff4Python.Require();
    var image = Aff4ReferenceDownloads.RequireUnicodeImage();
    var dir = NewTempDir();
    try {
      var (files, _, _, output) = python.Read(dir, image);
      AssertOurReadMatches(image, files.ToDictionary(kv => kv.Key, kv => kv.Value), dir, output);
    } finally {
      TryDelete(dir);
    }
  }

  // ── reference image without pyaff4 ────────────────────────────────────────

  /// <summary>pyaff4 master 6a911586 reading unicode.aff4 (captured; see ReferenceVectors/README.md).</summary>
  private static readonly Dictionary<string, (long Size, string Sha256)> UnicodeImageByPyaff4 = new(StringComparer.Ordinal) {
    ["test_images/AFF4Std/Base-Linear-ReadError.aff4"] = (2143595, "0b1c2edd6bdf37f2efe9c6fa274dd3c100de3fc5152d8a1fd82fb61f41c68e12"),
    ["test_images/AFF4Std/Base-Allocated.aff4"] = (3076183, "df6c705c15339a53cf86b221858f2cd6b85c56f7078287ae99273145efe567c1"),
    ["test_images/AFF4Std/Striped/Base-Linear_2.aff4"] = (1331311, "0d46baa88def85b784caf54a3a6c561e08019fbc22424a21f117d00b90c94505"),
    ["test_images/AFF4Std/Base-Linear-AllHashes.aff4"] = (3198879, "d8f098b1bb51eceb1e913c2389d3fb0a8543323d1db01bf8957e21bdccb1846d"),
    ["test_images/AFF4Std/README.txt"] = (677, "01de8cd1b8b574b0d6a2af35af2251eb90f01cceb8ce792843d730fa0311a370"),
    ["test_images/AFF4Std/Base-Linear.aff4"] = (3177529, "bcde3297ae95cd9df214bfb79821334628dad08f21ef38374a2c091481e391c0"),
    ["test_images/AFF4Std/Striped/Base-Linear_1.aff4"] = (1976635, "56fea0e0b4c94fb7ce780a39129054fe869ee2fcfa77ee7c6ede4830b035c8c8"),
  };

  [Test]
  public void GivenPyaff4ReferenceImage_WhenWeReadIt_ThenFilesMatchPyaff4sCapturedReading() {
    var image = Aff4ReferenceDownloads.RequireUnicodeImage();
    var dir = NewTempDir();
    try {
      AssertOurReadMatches(image, UnicodeImageByPyaff4.ToDictionary(kv => "./" + kv.Key, kv => kv.Value), dir, "captured pyaff4 output");
    } finally {
      TryDelete(dir);
    }
  }

  private static void AssertOurReadMatches(string image, Dictionary<string, (long Size, string Sha256)> theirs, string dir, string context) {
    var expected = theirs.ToDictionary(kv => Aff4OracleNames.Normalize(kv.Key), kv => kv.Value);
    using var fs = File.OpenRead(image);
    var listed = new Aff4FormatDescriptor().List(fs, null).Where(e => !e.IsDirectory && e.Kind == "file").ToDictionary(e => e.Name, e => e.OriginalSize);
    Assert.That(listed.Keys, Is.EquivalentTo(expected.Keys), context);
    var outDir = Path.Combine(dir, "ours");
    new Aff4FormatDescriptor().Extract(fs, outDir, null, null);
    foreach (var (name, (size, sha256)) in expected) {
      Assert.That(listed[name], Is.EqualTo(size), name);
      var data = File.ReadAllBytes(Path.Combine(outDir, name.Replace('/', Path.DirectorySeparatorChar)));
      Assert.That(Sha256(data), Is.EqualTo(sha256), name);
    }
  }

  // ── rdflib ────────────────────────────────────────────────────────────────

  private static void RequireRdflib() {
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed; the rdflib check runs on Linux.");
    var probe = FsInteropToolbox.RunWsl("python3 -c 'import rdflib'");
    if (probe.ExitCode != 0)
      Assert.Ignore("python3 with rdflib is not available. Run (inside WSL on Windows): `sudo apt install -y python3-rdflib`.");
  }

  [TestCase("deflate")]
  [TestCase("stored")]
  public void OurVolume_GraphAndSegmentsCheckedByRdflib(string method) {
    RequireRdflib();
    var dir = NewTempDir();
    try {
      var volume = Path.Combine(dir, "ours.aff4");
      using (var fs = File.Create(volume))
        new Aff4FormatDescriptor().Create(fs, Inputs, new FormatCreateOptions(method));
      var script = Path.Combine(dir, "check.py");
      File.WriteAllText(script, RdflibChecker);
      var r = FsInteropToolbox.RunWsl($"python3 {FsInteropToolbox.WinToWsl(script)} {FsInteropToolbox.WinToWsl(volume)} 2>&1");
      Assert.That(r.ExitCode, Is.Zero, $"rdflib/zipfile rejected our volume:\n{r.StdOut}\n{r.StdErr}");
      // 5 files; folders evidence, evidence/deep and emptydir.
      Assert.That(r.StdOut, Does.Contain("OK 5 3"));
    } finally {
      TryDelete(dir);
    }
  }

  // ── helpers ───────────────────────────────────────────────────────────────

  internal static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

  private static string NewTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_aff4ext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryDelete(string dir) {
    try { Directory.Delete(dir, true); } catch { /* best effort */ }
  }

  /// <summary>
  /// A Python with pyaff4 importable: <c>CWB_PYAFF4_PYTHON</c> (a host interpreter path) when set,
  /// else <c>python3</c> on Linux or inside WSL. pyaff4's pinned dependencies do not install on
  /// current Pythons, so the usual setup is a dedicated environment, e.g. Python 3.8 with
  /// <c>pip install --no-deps git+https://github.com/aff4/pyaff4</c> plus its requirements.
  /// </summary>
  private sealed class Pyaff4Python {
    private readonly string? _hostPython;

    private Pyaff4Python(string? hostPython) => this._hostPython = hostPython;

    public static Pyaff4Python Require() {
      var host = Environment.GetEnvironmentVariable("CWB_PYAFF4_PYTHON");
      if (!string.IsNullOrWhiteSpace(host)) {
        var candidate = new Pyaff4Python(host);
        var (_, err, code) = candidate.RunHost(["-c", "import pyaff4.container"]);
        if (code != 0) Assert.Ignore($"CWB_PYAFF4_PYTHON={host} cannot import pyaff4: {err}");
        return candidate;
      }
      if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable)
        Assert.Ignore("pyaff4 not found: set CWB_PYAFF4_PYTHON to a Python with pyaff4, or install it in WSL.");
      if (FsInteropToolbox.RunWsl("python3 -c 'import pyaff4.container'").ExitCode != 0)
        Assert.Ignore("pyaff4 is not importable by python3; set CWB_PYAFF4_PYTHON to a Python that has it.");
      return new Pyaff4Python(null);
    }

    public (Dictionary<string, (long Size, string Sha256)> Files, int HashOk, int HashBad, string Output) Read(string workDir, string volume) {
      var script = Path.Combine(workDir, "pyaff4_oracle.py");
      File.WriteAllText(script, Pyaff4Oracle);
      var (stdout, stderr, code) = this._hostPython != null
        ? this.RunHost([script, volume])
        : FsInteropToolbox.RunWsl($"python3 {FsInteropToolbox.WinToWsl(script)} {FsInteropToolbox.WinToWsl(volume)}");
      var output = stdout + "\n" + stderr;
      Assert.That(code, Is.Zero, $"pyaff4 failed to read {Path.GetFileName(volume)}:\n{output}");
      var files = new Dictionary<string, (long, string)>(StringComparer.Ordinal);
      int ok = -1, bad = -1;
      foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
        var cols = line.TrimEnd('\r').Split('\t');
        if (cols[0] == "FILE" && cols.Length == 4) files[Aff4OracleNames.Normalize(cols[1])] = (long.Parse(cols[2]), cols[3]);
        else if (cols[0] == "HASHES" && cols.Length == 3) { ok = int.Parse(cols[1]); bad = int.Parse(cols[2]); }
      }
      return (files, ok, bad, output);
    }

    private (string StdOut, string StdErr, int ExitCode) RunHost(IEnumerable<string> args) {
      var psi = new ProcessStartInfo(this._hostPython!) {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        StandardOutputEncoding = Encoding.UTF8,
      };
      psi.Environment["PYTHONUTF8"] = "1";
      psi.ArgumentList.Add("-W");
      psi.ArgumentList.Add("ignore");
      foreach (var arg in args) psi.ArgumentList.Add(arg);
      using var process = Process.Start(psi)!;
      var stderrTask = process.StandardError.ReadToEndAsync();
      var stdout = process.StandardOutput.ReadToEnd();
      process.WaitForExit(300_000);
      return (stdout, stderrTask.Result, process.ExitCode);
    }
  }
}

/// <summary>
/// pyaff4's AFF4-L reference image <c>unicode.aff4</c>, from a directory named by
/// <c>CWB_AFF4_REFERENCE_DIR</c>, or downloaded at a pinned revision when
/// <c>CWB_DOWNLOAD_AFF4_REFERENCE=1</c>. Its SHA-256 is checked either way.
/// </summary>
internal static class Aff4ReferenceDownloads {
  private const string Revision = "6a91158661edec6ed8a865a09e28dbf30d487e38";
  private const string UnicodeSha256 = "0170e3c7dab593274d23f6f46f0bc53f7b8e28c96dce1d3cf57a4e1e0954f504";

  public static string RequireUnicodeImage() {
    var dir = Environment.GetEnvironmentVariable("CWB_AFF4_REFERENCE_DIR");
    var path = !string.IsNullOrWhiteSpace(dir)
      ? Path.Combine(dir, "unicode.aff4")
      : Path.Combine(Path.GetTempPath(), "cwb-aff4-reference", Revision, "unicode.aff4");
    if (!File.Exists(path)) {
      if (Environment.GetEnvironmentVariable("CWB_DOWNLOAD_AFF4_REFERENCE") != "1")
        Assert.Ignore("unicode.aff4 not available: set CWB_AFF4_REFERENCE_DIR to a folder holding it, or CWB_DOWNLOAD_AFF4_REFERENCE=1 to fetch it.");
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var client = new HttpClient();
      var bytes = client.GetByteArrayAsync($"https://raw.githubusercontent.com/aff4/pyaff4/{Revision}/test_images/AFF4-L/unicode.aff4").GetAwaiter().GetResult();
      File.WriteAllBytes(path, bytes);
    }
    var actual = Aff4ExternalConformanceTests.Sha256(File.ReadAllBytes(path));
    Assert.That(actual, Is.EqualTo(UnicodeSha256), $"{path} is not pyaff4 {Revision[..8]}'s unicode.aff4");
    return path;
  }
}
