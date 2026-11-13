using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Lib;
using FileFormat.StuffIt;

namespace Compression.Tests.StuffIt;

/// <summary>
/// A classic StuffIt archive inside MacBinary (the form that keeps its resource fork, and with it
/// the password check) must be detected as StuffIt, not as a generic MacBinary file, through the
/// registry and through <c>cwb list</c>/<c>cwb extract</c>.
/// </summary>
[TestFixture]
public class StuffItMacBinaryDetectionTests {
  private const string Vector = "testfile.stuffit45_dlx.mac9.password.sit.bin";
  private const string TxtSha = "b645efee0ed710034959eae942277a750d08687c30bcf0e9ec6ea7641527462f";

  private static byte[] Load(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("StuffItVectors." + name) ?? throw new FileNotFoundException(name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static string TempDir() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_sitbin_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
  }

  [TestCase("archive.sit.bin"), TestCase("archive.bin"), TestCase("archive")]
  [Category("HappyPath")]
  public void GivenMacBinaryWrappedStuffIt_WhenDetected_ThenItIsStuffIt(string name) {
    var dir = TempDir();
    try {
      var path = Path.Combine(dir, name);
      File.WriteAllBytes(path, Load(Vector));
      Assert.That(FormatDetector.Detect(path).ToString(), Is.EqualTo("StuffIt"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("HappyPath")]
  public void GivenOurEncryptedClassicArchive_WhenDetected_ThenItIsStuffIt() {
    var dir = TempDir();
    try {
      var path = Path.Combine(dir, "ours.sit.bin");
      using (var f = File.Create(path))
      using (var w = new StuffItWriter(f, StuffItCompression.Smallest, "secret", leaveOpen: true))
        w.AddFile("a.txt", "abc"u8.ToArray());
      Assert.That(FormatDetector.Detect(path).ToString(), Is.EqualTo("StuffIt"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Boundary")]
  public void GivenMacBinaryOfAnythingElse_WhenDetected_ThenItStaysMacBinary() {
    var dir = TempDir();
    try {
      var path = Path.Combine(dir, "note.macbin");
      using (var f = File.Create(path))
        FileFormat.MacBinary.MacBinaryWriter.Write(f, "note.txt", "plain text, not an archive"u8.ToArray(), null, "TEXT", "ttxt");
      Assert.That(FormatDetector.Detect(path).ToString(), Is.EqualTo("MacBinary"));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  // ── cwb ──────────────────────────────────────────────────────────────────

  private static string LocateCli() {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("The CLI project builds the win-x64 host executable.");
#if DEBUG
    const string configuration = "Debug";
#else
    const string configuration = "Release";
#endif
    var root = Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", ".."));
    var output = Path.Combine(root, "Compression.CLI", "bin", configuration, "net10.0");
    var path = new[] { Path.Combine(output, "cwb.exe"), Path.Combine(output, "win-x64", "cwb.exe") }.FirstOrDefault(File.Exists);
    if (path == null) Assert.Ignore($"CLI executable was not built under {output}.");
    return path!;
  }

  private static (int Code, string Output) Cli(string cli, string cwd, params string[] args) {
    var psi = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = cwd };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, output);
  }

  [Test, Category("End2End")]
  public void GivenMacBinaryWrappedEncryptedStuffIt_WhenListedAndExtractedByTheCli_ThenTheEntriesAndBytesComeOut() {
    var cli = LocateCli();
    var dir = TempDir();
    try {
      File.WriteAllBytes(Path.Combine(dir, "sources.sit.bin"), Load(Vector));
      var list = Cli(cli, dir, "list", "sources.sit.bin");
      Assert.That(list.Code, Is.Zero, list.Output);
      Assert.That(list.Output, Does.Contain("testfile.txt").And.Contain("Test Image"));

      var extract = Cli(cli, dir, "extract", "sources.sit.bin", "-o", "out", "-p", "password");
      Assert.That(extract.Code, Is.Zero, extract.Output);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "out", "testfile.txt")))), Is.EqualTo(TxtSha));

      var wrong = Cli(cli, dir, "extract", "sources.sit.bin", "-o", "bad", "-p", "wrong");
      Assert.That(wrong.Code, Is.Not.Zero, "a wrong password must fail");
    } finally {
      Directory.Delete(dir, true);
    }
  }
}
