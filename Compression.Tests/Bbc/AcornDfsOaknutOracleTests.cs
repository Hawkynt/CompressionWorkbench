using System.Diagnostics;
using Compression.Registry;
using FileSystem.Bbc;
using FileSystem.Mfs1;

namespace Compression.Tests.Bbc;

/// <summary>
/// Third-party proof for the Acorn DFS catalogue editors (BBC DFS and MFS-1, which shares
/// the DFS catalogue), in both directions, against oaknut-disc (MIT, the Acorn disc-image
/// tool): what we create and then edit by directory letter is listed, read back byte for
/// byte and passed by <c>disc validate</c>; and an image oaknut made and then edited by us is
/// still valid to oaknut, with our edits where we put them.
/// </summary>
/// <remarks>
/// Skips unless Python can import <c>oaknut.disc</c>: install it with
/// <c>pip install oaknut-disc</c>, or point <c>CWB_OAKNUT_PYTHONPATH</c> at a
/// <c>pip install --target</c> directory.
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
public sealed class AcornDfsOaknutOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_dfsoak_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  public enum Flavour { BbcDfs, Mfs1 }

  private static IArchiveFormatOperations Ops(Flavour flavour)
    => flavour == Flavour.BbcDfs ? new BbcFormatDescriptor() : new Mfs1FormatDescriptor();

  private static Dictionary<string, byte[]> Files(Stream image, Flavour flavour) {
    image.Position = 0;
    var ops = Ops(flavour);
    var names = ops.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
    var outDir = Directory.CreateTempSubdirectory("cwb_dfsx_").FullName;
    try {
      image.Position = 0;
      ops.Extract(image, outDir, null, null);
      // Files of directory '$' are listed as "$.NAME" (BBC) or "NAME" (MFS-1) and extracted as "NAME".
      static string Bare(string n) => n.StartsWith("$.", StringComparison.Ordinal) ? n[2..] : n;
      return names.ToDictionary(n => Bare(n).ToUpperInvariant(), n => File.ReadAllBytes(Path.Combine(outDir, Bare(n))), StringComparer.Ordinal);
    } finally {
      Directory.Delete(outDir, true);
    }
  }

  /// <summary>DFS names: "D.NAME" for directory D, a bare "NAME" for directory '$'.</summary>
  private static string OaknutPath(string name) => name.Length > 1 && name[1] == '.' ? name : "$." + name;

  [TestCase(Flavour.BbcDfs)]
  [TestCase(Flavour.Mfs1)]
  public void GivenAnImageWeCreatedAndEdited_WhenOaknutReadsIt_ThenItListsAndReadsExactlyOurFiles(Flavour flavour) {
    RequireOaknut();
    var ops = Ops(flavour);
    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, [
      ArchiveInputInfo.InMemory("README", "readme in $"u8.ToArray()),
      ArchiveInputInfo.InMemory("A.TXT", "alpha in A"u8.ToArray()),
      ArchiveInputInfo.InMemory("B.TXT", "beta in B"u8.ToArray()),
      ArchiveInputInfo.InMemory("B.BIG", Pattern(3000, 1)),
    ], new FormatCreateOptions());
    var editable = new MemoryStream();
    editable.Write(image.ToArray());

    ((IArchiveModifiable)ops).Add(editable, [ArchiveInputInfo.InMemory("C.TXT", "gamma in C"u8.ToArray())]);
    ((IArchiveModifiable)ops).Remove(editable, ["B.TXT"]);
    var expected = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["README"] = "readme in $"u8.ToArray(),
      ["A.TXT"] = "alpha in A"u8.ToArray(),
      ["B.BIG"] = Pattern(3000, 1),
      ["C.TXT"] = "gamma in C"u8.ToArray(),
    };
    Assert.That(Files(editable, flavour).Keys, Is.EquivalentTo(expected.Keys), "our reader");

    var ssd = Path.Combine(this._dir, "ours.ssd");
    File.WriteAllBytes(ssd, editable.ToArray());
    var validate = Oaknut("validate", ssd);
    Assert.That(validate.ExitCode, Is.Zero, validate.Output);
    var listed = new List<string>();
    foreach (var dir in new[] { "$", "A", "B", "C" }) {
      var ls = Oaknut("ls", $"{ssd}:{dir}");
      listed.AddRange(ls.Output.Split('\n').Select(l => l.Split('\t')).Where(c => c.Length >= 2 && c[1] == "file")
        .Select(c => dir == "$" ? c[0] : $"{dir}.{c[0]}"));
    }
    Assert.That(listed, Is.EquivalentTo(expected.Keys), "files oaknut lists");
    foreach (var (name, data) in expected)
      Assert.That(this.OaknutGet(ssd, OaknutPath(name)), Is.EqualTo(data), $"{name} as oaknut reads it");
  }

  [TestCase(Flavour.BbcDfs)]
  [TestCase(Flavour.Mfs1)]
  public void GivenAnImageOaknutMade_WhenWeReadAndEditIt_ThenOaknutStillValidatesItAndSeesOurEdits(Flavour flavour) {
    RequireOaknut();
    var ssd = Path.Combine(this._dir, "theirs.ssd");
    Assert.That(Oaknut("create", "--title", "ORACLE", ssd).ExitCode, Is.Zero);
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
      ["HELLO"] = "hello from oaknut"u8.ToArray(),
      ["A.DATA"] = Pattern(5000, 2),
      ["B.DATA"] = "b data"u8.ToArray(),
    };
    foreach (var (name, data) in files) {
      var host = Path.Combine(this._dir, "put.bin");
      File.WriteAllBytes(host, data);
      var put = Oaknut("put", $"{ssd}:{OaknutPath(name)}", host);
      Assert.That(put.ExitCode, Is.Zero, put.Output);
    }

    using (var image = new MemoryStream(File.ReadAllBytes(ssd))) {
      var read = Files(image, flavour);
      Assert.That(read.Keys, Is.EquivalentTo(files.Keys), "our reader on oaknut's image");
      foreach (var (name, data) in files) Assert.That(read[name], Is.EqualTo(data), name);
    }

    using (var image = new MemoryStream()) {
      image.Write(File.ReadAllBytes(ssd));
      var ops = (IArchiveModifiable)Ops(flavour);
      ops.Add(image, [ArchiveInputInfo.InMemory("C.NEW", "added by us"u8.ToArray())]);
      ops.Remove(image, ["A.DATA"]);
      File.WriteAllBytes(ssd, image.ToArray());
    }

    var validate = Oaknut("validate", ssd);
    Assert.That(validate.ExitCode, Is.Zero, validate.Output);
    Assert.Multiple(() => {
      Assert.That(this.OaknutGet(ssd, "C.NEW"), Is.EqualTo("added by us"u8.ToArray()));
      Assert.That(this.OaknutGet(ssd, "B.DATA"), Is.EqualTo("b data"u8.ToArray()));
      Assert.That(this.OaknutGet(ssd, "$.HELLO"), Is.EqualTo("hello from oaknut"u8.ToArray()));
      Assert.That(Oaknut("ls", $"{ssd}:A").Output, Does.Not.Contain("DATA"), "the removed file is gone for oaknut");
    });
  }

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    new Random(seed).NextBytes(d);
    return d;
  }

  private byte[] OaknutGet(string ssd, string inner) {
    var host = Path.Combine(this._dir, $"get_{Guid.NewGuid():N}.bin");
    var r = Oaknut("get", $"{ssd}:{inner}", host);
    Assert.That(r.ExitCode, Is.Zero, $"oaknut get {inner}: {r.Output}");
    return File.ReadAllBytes(host);
  }

  // ── oaknut-disc through Python ─────────────────────────────────────────

  private static bool? _available;

  private static void RequireOaknut() {
    _available ??= Oaknut("--version").ExitCode == 0;
    if (_available != true)
      Assert.Ignore("oaknut-disc is not importable (pip install oaknut-disc, or set CWB_OAKNUT_PYTHONPATH).");
  }

  private static string PythonString(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

  private static (int ExitCode, string Output) Oaknut(params string[] args) {
    var python = Environment.GetEnvironmentVariable("CWB_PYTHON") is { Length: > 0 } configured ? configured
      : OperatingSystem.IsWindows() ? "python" : "python3";
    var psi = new ProcessStartInfo(python) {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
    };
    // Put the package directory on sys.path in the script: an embeddable Python ignores PYTHONPATH.
    var extraPath = Environment.GetEnvironmentVariable("CWB_OAKNUT_PYTHONPATH") is { Length: > 0 } path
      ? $"sys.path.insert(0, {PythonString(path)}); "
      : "";
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add($"import sys; {extraPath}from oaknut.disc.cli import cli; sys.argv[0] = 'disc'; cli()");
    foreach (var a in args) psi.ArgumentList.Add(a);
    psi.Environment["PYTHONIOENCODING"] = "utf-8";
    try {
      using var p = Process.Start(psi)!;
      var stdout = p.StandardOutput.ReadToEndAsync();
      var stderr = p.StandardError.ReadToEndAsync();
      if (!p.WaitForExit(120_000)) {
        p.Kill(true);
        return (-1, "oaknut timed out");
      }
      return (p.ExitCode, stdout.Result.Replace("\r", "") + stderr.Result);
    } catch (System.ComponentModel.Win32Exception e) {
      return (-1, e.Message);
    }
  }
}
