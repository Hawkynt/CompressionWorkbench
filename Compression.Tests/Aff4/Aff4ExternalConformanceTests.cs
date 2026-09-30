using System.IO.Compression;
using Compression.Registry;
using FileFormat.Aff4;

namespace Compression.Tests.Aff4;

/// <summary>
/// Our AFF4-L volumes checked by independent parsers: rdflib must parse information.turtle as
/// Turtle and find, for every aff4:FileImage/aff4:ZipSegment, a ZIP member named by its ARN whose
/// size and SHA-256 match the recorded aff4:size and aff4:hash, plus a correct metadata hash. No
/// AFF4-L 2.1 reader is available as an oracle (pyaff4 implements v1.1), so this validates the
/// metadata graph and its bindings to the ZIP layer, not end-to-end acceptance by a forensic
/// suite. Skips unless python3 with rdflib is present (Debian/Ubuntu: python3-rdflib).
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public sealed class Aff4ExternalConformanceTests {
  private const string Checker = """
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
    var dir = Path.Combine(Path.GetTempPath(), "cwb_aff4ext_" + Guid.NewGuid().ToString("N")[..10]);
    Directory.CreateDirectory(dir);
    try {
      var random = new byte[20_000];
      new Random(4).NextBytes(random);
      var inputs = new[] {
        ArchiveInputInfo.InMemory("evidence/random.bin", random),
        ArchiveInputInfo.InMemory("evidence/deep/note \"quoted\".txt", "note"u8),
        ArchiveInputInfo.InMemory("tab\tname.txt", "tab"u8),
        ArchiveInputInfo.InMemory("empty.bin", ReadOnlySpan<byte>.Empty),
      };
      var volume = Path.Combine(dir, "ours.aff4");
      using (var fs = File.Create(volume))
        new Aff4FormatDescriptor().Create(fs, inputs, new FormatCreateOptions(method));
      var script = Path.Combine(dir, "check.py");
      File.WriteAllText(script, Checker);
      var r = FsInteropToolbox.RunWsl($"python3 {FsInteropToolbox.WinToWsl(script)} {FsInteropToolbox.WinToWsl(volume)} 2>&1");
      Assert.That(r.ExitCode, Is.Zero, $"rdflib/zipfile rejected our volume:\n{r.StdOut}\n{r.StdErr}");
      Assert.That(r.StdOut, Does.Contain("OK 4 2"));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
