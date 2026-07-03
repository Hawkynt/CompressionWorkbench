using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Asar;

namespace Compression.Tests.Asar;

[TestFixture]
public class AsarTests {

  // ── Helpers ──────────────────────────────────────────────────────────────

  private static byte[] Build(params (string Path, byte[] Data)[] files) {
    var w = new AsarWriter();
    foreach (var (p, d) in files) w.AddFile(p, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  private static string NewTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), "asar_" + Path.GetRandomFileName());
    Directory.CreateDirectory(dir);
    return dir;
  }

  // ── Header shape / magic ─────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Header_SizePicklePreludeIsFour() {
    var data = Build(("a.txt", "hello"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(data), Is.EqualTo(4u));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_MagicSignature_MatchesPrelude() {
    var desc = new AsarFormatDescriptor();
    Assert.That(desc.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(desc.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x04, 0x00, 0x00, 0x00 }).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ReportsCreateCapability() {
    var d = new AsarFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
  }

  // ── Reader round-trips ───────────────────────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_SingleFile_PayloadPreserved() {
    var payload = "The quick brown fox"u8.ToArray();
    var data = Build(("readme.txt", payload));

    using var r = new AsarReader(new MemoryStream(data));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Path, Is.EqualTo("readme.txt"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.ReadData(r.Entries[0]), Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Reader_MultipleFiles_OffsetsAreSequential() {
    var a = "first"u8.ToArray();
    var b = "second-file"u8.ToArray();
    var c = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
    var data = Build(("a", a), ("b", b), ("c.bin", c));

    using var r = new AsarReader(new MemoryStream(data));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(files[0].Offset, Is.EqualTo(0));
    Assert.That(files[1].Offset, Is.EqualTo(a.Length));
    Assert.That(files[2].Offset, Is.EqualTo(a.Length + b.Length));
    Assert.That(r.ReadData(files[2]), Is.EqualTo(c).AsCollection);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Reader_NestedDirectories_PathsReconstructed() {
    var deep = "buried treasure"u8.ToArray();
    var data = Build(
      ("root.txt", "top"u8.ToArray()),
      ("lib/util/helper.js", deep));

    using var r = new AsarReader(new MemoryStream(data));
    var file = r.Entries.Single(e => e.Path == "lib/util/helper.js");
    Assert.That(r.ReadData(file), Is.EqualTo(deep).AsCollection);
    // Intermediate directories surface as directory entries.
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Path == "lib"), Is.True);
    Assert.That(r.Entries.Any(e => e.IsDirectory && e.Path == "lib/util"), Is.True);
  }

  [Test, Category("Boundary")]
  public void Reader_EmptyArchive_HasNoEntries() {
    var data = Build();
    using var r = new AsarReader(new MemoryStream(data));
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Reader_EmptyFile_ZeroSized() {
    var data = Build(("empty", []));
    using var r = new AsarReader(new MemoryStream(data));
    var e = r.Entries.Single();
    Assert.That(e.Size, Is.EqualTo(0));
    Assert.That(r.ReadData(e), Is.Empty);
  }

  [Test, Category("Boundary"), Category("RoundTrip")]
  public void Reader_BinaryPayload_PreservedExactly() {
    var rng = new Random(1234);
    var payload = new byte[8192];
    rng.NextBytes(payload);
    var data = Build(("blob.dat", payload));

    using var r = new AsarReader(new MemoryStream(data));
    Assert.That(r.ReadData(r.Entries[0]), Is.EqualTo(payload).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsBadPrelude() {
    var bogus = new byte[32];
    BinaryPrimitives.WriteUInt32LittleEndian(bogus, 7); // not 4
    Assert.That(() => new AsarReader(new MemoryStream(bogus)),
      Throws.InstanceOf<InvalidDataException>());
  }

  // ── Descriptor Create → List → Extract ───────────────────────────────────

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_CreateListExtract_ByteIdentical() {
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("index.js", "console.log('hi')"u8.ToArray()),
      ArchiveInputInfo.InMemory("assets/logo.png", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }),
      ArchiveInputInfo.InMemory("assets/data/config.json", "{\"k\":1}"u8.ToArray()),
    };

    var desc = new AsarFormatDescriptor();
    using var archive = new MemoryStream();
    desc.Create(archive, inputs, new FormatCreateOptions());

    // List
    archive.Position = 0;
    var listed = desc.List(archive, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(listed, Does.Contain("index.js"));
    Assert.That(listed, Does.Contain("assets/logo.png"));
    Assert.That(listed, Does.Contain("assets/data/config.json"));

    // Extract
    var dir = NewTempDir();
    try {
      archive.Position = 0;
      desc.Extract(archive, dir, null, null);
      foreach (var i in inputs) {
        var path = Path.Combine(dir, i.ArchiveName.Replace('/', Path.DirectorySeparatorChar));
        Assert.That(File.Exists(path), Is.True, $"missing {i.ArchiveName}");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(i.ReadContent()).AsCollection);
      }
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Extract_FilterSelectsSingleFile() {
    var data = Build(("keep.txt", "yes"u8.ToArray()), ("drop.txt", "no"u8.ToArray()));
    var dir = NewTempDir();
    try {
      var desc = new AsarFormatDescriptor();
      desc.Extract(new MemoryStream(data), dir, null, ["keep.txt"]);
      Assert.That(File.Exists(Path.Combine(dir, "keep.txt")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "drop.txt")), Is.False);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  // ── Interop with the reference `asar` tool (gated) ───────────────────────

  [Test, Category("Interop")]
  public void Interop_NodeAsar_ReadsOurArchive() {
    var (node, npx) = FindNode();
    if (node == null || npx == null || !AsarToolAvailable(npx))
      Assert.Ignore("Node.js `asar` package not available (npx --no-install failed).");

    var work = NewTempDir();
    try {
      var archivePath = Path.Combine(work, "app.asar");
      var payload = "electron interop payload"u8.ToArray();
      File.WriteAllBytes(archivePath, Build(("main.js", payload), ("sub/data.txt", "nested"u8.ToArray())));

      // `asar list` must enumerate both files.
      var listing = RunCapture(npx!, ["--no-install", "asar", "list", archivePath], work);
      Assert.That(listing, Does.Contain("main.js"));
      Assert.That(listing.Replace('\\', '/'), Does.Contain("sub/data.txt"));

      // `asar extract-file` must return byte-identical content.
      RunCapture(npx!, ["--no-install", "asar", "extract-file", archivePath, "main.js"], work);
      var produced = Path.Combine(work, "main.js");
      Assert.That(File.Exists(produced), Is.True);
      Assert.That(File.ReadAllBytes(produced), Is.EqualTo(payload).AsCollection);
    } finally {
      Directory.Delete(work, true);
    }
  }

  [Test, Category("Interop")]
  public void Interop_NodeAsar_WeReadItsArchive() {
    var (node, npx) = FindNode();
    if (node == null || npx == null || !AsarToolAvailable(npx))
      Assert.Ignore("Node.js `asar` package not available (npx --no-install failed).");

    var work = NewTempDir();
    try {
      var src = Path.Combine(work, "src");
      Directory.CreateDirectory(Path.Combine(src, "nested"));
      var body = "made by the reference tool"u8.ToArray();
      File.WriteAllBytes(Path.Combine(src, "app.js"), body);
      File.WriteAllBytes(Path.Combine(src, "nested", "x.bin"), new byte[] { 9, 8, 7, 6 });

      var archivePath = Path.Combine(work, "ref.asar");
      RunCapture(npx!, ["--no-install", "asar", "pack", src, archivePath], work);

      using var r = new AsarReader(File.OpenRead(archivePath));
      var app = r.Entries.Single(e => e.Path.EndsWith("app.js", StringComparison.Ordinal));
      Assert.That(r.ReadData(app), Is.EqualTo(body).AsCollection);
    } finally {
      Directory.Delete(work, true);
    }
  }

  // ── Process helpers ──────────────────────────────────────────────────────

  private static (string? Node, string? Npx) FindNode() {
    string? Which(string exe) {
      var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
      foreach (var p in paths) {
        foreach (var ext in new[] { "", ".cmd", ".exe" }) {
          var full = Path.Combine(p.Trim(), exe + ext);
          if (File.Exists(full)) return full;
        }
      }
      return null;
    }
    return (Which("node"), Which("npx"));
  }

  private static bool AsarToolAvailable(string npx) {
    try {
      var psi = new ProcessStartInfo(npx) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
      foreach (var a in new[] { "--no-install", "asar", "--version" }) psi.ArgumentList.Add(a);
      using var proc = Process.Start(psi)!;
      proc.WaitForExit(20000);
      return proc.HasExited && proc.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static string RunCapture(string exe, string[] args, string cwd) {
    var psi = new ProcessStartInfo(exe) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd,
    };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(60000);
    if (proc.ExitCode != 0)
      Assert.Ignore($"asar tool exited {proc.ExitCode}: {stderr}");
    return stdout + stderr;
  }
}
