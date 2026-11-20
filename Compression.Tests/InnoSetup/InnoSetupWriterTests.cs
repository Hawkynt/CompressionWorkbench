using System.Diagnostics;
using Compression.Registry;
using FileFormat.InnoSetup;

namespace Compression.Tests.InnoSetup;

/// <summary>
/// The Inno Setup writer, built on the stubs of an installed Inno Setup 6.0.x (INNOSETUPDIR, or
/// "Inno Setup 6" under Program Files). Oracles: the installer itself, run with
/// <c>/VERYSILENT /DIR=</c> (under wine when CWB_INNO_WINEPREFIX names a WSL wine prefix, natively
/// only when CWB_INNO_RUN_NATIVE=1, otherwise skipped), innoextract, and our reader.
/// </summary>
[TestFixture]
public class InnoSetupWriterTests {
  private static readonly DateTime When = new(2021, 3, 4, 5, 6, 8, DateTimeKind.Utc);

  private static List<InnoSetupInstallerFile> Payload() {
    var noise = new byte[70_000];
    new Random(5).NextBytes(noise);
    return [
      new("readme.txt", "Hello from an Inno Setup installer.\r\n"u8.ToArray(), When),
      new("empty.dat", [], When),
      new("bin/noise.bin", noise, When),
      new("bin/runs.bin", [.. Enumerable.Repeat((byte)0xAB, 50_000), .. "tail"u8], When),
      new("docs/deep/nested/notes {v1}.txt", "braces and spaces\n"u8.ToArray(), When),
      new("docs/Ünïcödé ✓.txt", "non-ASCII name\n"u8.ToArray(), When),
      new("empty dir", null, When),
    ];
  }

  private static Dictionary<string, byte[]> Expected()
    => Payload().Where(f => f.Data != null).ToDictionary(f => f.Path, f => f.Data!);

  private static InnoSetupStubs Stubs() {
    var stubs = InnoSetupStubs.Load();
    if (stubs == null) {
      if (Environment.GetEnvironmentVariable("CWB_REQUIRE_INNO_STUBS") == "1")
        Assert.Fail("Inno Setup 6.0.x is required (CWB_REQUIRE_INNO_STUBS=1) but none was found.");
      Assert.Ignore("No Inno Setup 6.0.x installation (set INNOSETUPDIR).");
    }
    return stubs!;
  }

  private static byte[] Build(InnoSetupStubs stubs, InnoSetupWriterCompression compression, IReadOnlyList<InnoSetupInstallerFile>? files = null) {
    using var ms = new MemoryStream();
    InnoSetupWriter.Write(ms, stubs, files ?? Payload(), new InnoSetupInstallerOptions { AppName = "CWB Test", Compression = compression });
    return ms.ToArray();
  }

  // ── without Inno Setup ────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void GivenCode_WhenCallTransformedAndUndone_ThenItIsTheSame() {
    var rng = new Random(3);
    var code = new byte[20_000];
    rng.NextBytes(code);
    for (var i = 0; i < code.Length - 5; i += rng.Next(5, 40)) {
      code[i] = (byte)(rng.Next(2) == 0 ? 0xE8 : 0xE9);
      code[i + 4] = (byte)(rng.Next(2) == 0 ? 0x00 : 0xFF);
    }
    var transformed = (byte[])code.Clone();
    InnoSetupWriter.TransformCallInstructions(transformed, 0);
    Assert.That(transformed, Is.Not.EqualTo(code));
    InnoCodec.Unfilter(InnoExeFilter.Filter5309, transformed);
    Assert.That(transformed, Is.EqualTo(code));
  }

  [TestCase(0)]
  [TestCase(4)]
  [TestCase(5)]
  [Category("Boundary")]
  public void GivenTinyBuffers_WhenCallTransformed_ThenOnlyFullOperandsChange(int length) {
    var code = Enumerable.Repeat((byte)0xE8, length).ToArray();
    var transformed = (byte[])code.Clone();
    InnoSetupWriter.TransformCallInstructions(transformed, 0);
    if (length < 5) Assert.That(transformed, Is.EqualTo(code));
    InnoCodec.Unfilter(InnoExeFilter.Filter5309, transformed);
    Assert.That(transformed, Is.EqualTo(code));
  }

  [TestCase("../evil.txt")]
  [TestCase("a/../../evil.txt")]
  [TestCase("bad:name.txt")]
  [TestCase("what?.txt")]
  [TestCase("trailing.")]
  [TestCase("")]
  [Category("Exception")]
  public void GivenAPathOutsideTheInstallDirectoryOrInvalid_WhenWritten_ThenRefused(string path)
    => Assert.That(() => InnoSetupWriter.Write(new MemoryStream(), new InnoSetupStubs([], [], "", [], []), [new(path, [1])]),
      Throws.ArgumentException);

  [Test, Category("Exception")]
  public void GivenStubsThatAreNotInnoSetup_WhenWritten_ThenRefused()
    => Assert.That(() => InnoSetupWriter.Write(new MemoryStream(), new InnoSetupStubs(new byte[4096], new byte[4096], "", [], []), [new("a.txt", [1])]),
      Throws.InstanceOf<NotSupportedException>());

  [Test, Category("Exception")]
  public void GivenAPassword_WhenCreatedThroughTheDescriptor_ThenRefused()
    => Assert.That(() => new InnoSetupFormatDescriptor().Create(new MemoryStream(), [ArchiveInputInfo.InMemory("a", [1])], new FormatCreateOptions { Password = "x" }),
      Throws.InstanceOf<NotSupportedException>());

  // ── with Inno Setup 6.0.x ─────────────────────────────────────────────────────

  [TestCase(InnoSetupWriterCompression.Stored)]
  [TestCase(InnoSetupWriterCompression.Lzma2)]
  [Category("RoundTrip")]
  public void GivenFiles_WhenWrittenAndReadByOurReader_ThenEveryFileComesBack(InnoSetupWriterCompression compression) {
    var installer = Build(Stubs(), compression);
    var reader = new InnoSetupReader(new MemoryStream(installer), null);
    Assert.That(reader.AppName, Is.EqualTo("CWB Test"));
    var got = reader.Entries.ToDictionary(e => e.FileName, reader.Extract);
    var expected = Expected().ToDictionary(kv => "app/" + kv.Key, kv => kv.Value);
    Assert.That(got.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (name, data) in expected)
      Assert.That(got[name], Is.EqualTo(data), name);
  }

  [Test, Category("HappyPath")]
  public void GivenLzma2_WhenWritten_ThenSmallerThanStored() {
    var stubs = Stubs();
    Assert.That(Build(stubs, InnoSetupWriterCompression.Lzma2).Length, Is.LessThan(Build(stubs, InnoSetupWriterCompression.Stored).Length));
  }

  [Test, Category("HappyPath")]
  public void GivenTheDescriptor_WhenCreating_ThenTheInstallerListsTheFiles() {
    Stubs();
    using var ms = new MemoryStream();
    new InnoSetupFormatDescriptor().Create(ms, [ArchiveInputInfo.InMemory("x/y.txt", "y"u8.ToArray())], new FormatCreateOptions { MethodName = "stored" });
    Assert.That(new InnoSetupFormatDescriptor().List(new MemoryStream(ms.ToArray()), null).Select(e => e.Name), Is.EqualTo(new[] { "app/x/y.txt" }));
  }

  [TestCase(InnoSetupWriterCompression.Stored)]
  [TestCase(InnoSetupWriterCompression.Lzma2)]
  [Category("ExternalInterop")]
  public void GivenOurInstaller_WhenInnoextractExtractsIt_ThenEveryFileMatches(InnoSetupWriterCompression compression) {
    var installer = Build(Stubs(), compression);
    if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
    if (!FsInteropToolbox.WslHasTool("innoextract")) Assert.Ignore("innoextract is not installed.");
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_innow_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var exe = Path.Combine(tmp, "setup.exe");
      File.WriteAllBytes(exe, installer);
      var outDir = Path.Combine(tmp, "out");
      Directory.CreateDirectory(outDir);
      var run = FsInteropToolbox.RunWsl($"innoextract -q -e -d '{FsInteropToolbox.WinToWsl(outDir)}' '{FsInteropToolbox.WinToWsl(exe)}'");
      Assert.That(run.ExitCode, Is.Zero, $"innoextract failed:\n{run.StdOut}\n{run.StdErr}");
      AssertInstalled(Path.Combine(outDir, "app"), "innoextract");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }

  [TestCase(InnoSetupWriterCompression.Stored)]
  [TestCase(InnoSetupWriterCompression.Lzma2)]
  [Category("EndToEnd")]
  public void GivenOurInstaller_WhenRunVerySilently_ThenItInstallsExactlyTheFiles(InnoSetupWriterCompression compression) {
    var installer = Build(Stubs(), compression);
    var tmp = Path.Combine(Path.GetTempPath(), $"cwb_innorun_{Guid.NewGuid():N}");
    try {
      Directory.CreateDirectory(tmp);
      var exe = Path.Combine(tmp, "setup.exe");
      File.WriteAllBytes(exe, installer);
      var target = Path.Combine(tmp, "target");
      if (Environment.GetEnvironmentVariable("CWB_INNO_WINEPREFIX") is { Length: > 0 } prefix) {
        if (OperatingSystem.IsWindows() && !FsInteropToolbox.WslAvailable) Assert.Ignore("WSL is not available.");
        var linuxExe = OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(exe) : exe;
        var linuxTarget = OperatingSystem.IsWindows() ? FsInteropToolbox.WinToWsl(target) : target;
        // wine faults running a loader from a drvfs (/mnt/c) path, so the installer runs from a Linux temp directory
        var run = FsInteropToolbox.RunWsl(
          $"export WINEPREFIX='{prefix}' WINEDEBUG=-all; t=$(mktemp -d); cp '{linuxExe}' \"$t/setup.exe\"; cd \"$t\"; " +
          "d=$(winepath -w \"$t/target\"); " +
          "timeout 300 wine \"$t/setup.exe\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART \"/DIR=$d\" < /dev/null; " +
          $"echo exit=$?; [ -d \"$t/target\" ] && cp -r \"$t/target\" '{linuxTarget}'; cd /; rm -rf \"$t\"; wineserver -k");
        Assert.That(run.StdOut, Does.Contain("exit=0"), $"the installer failed under wine:\n{run.StdOut}\n{run.StdErr}");
      } else {
        // the setup program is third-party native code: it runs on the host only when asked to
        if (Environment.GetEnvironmentVariable("CWB_INNO_RUN_NATIVE") != "1")
          Assert.Ignore("Set CWB_INNO_WINEPREFIX to run the installer under wine, or CWB_INNO_RUN_NATIVE=1 to run it on this machine.");
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The installer is a Windows program.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in (string[])["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=" + target]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(300_000)) {
          p.Kill(entireProcessTree: true);
          Assert.Fail("The installer did not finish within five minutes.");
        }
        Assert.That(p.ExitCode, Is.Zero, "installer exit code");
      }
      AssertInstalled(target, "the installer");
    } finally {
      try { Directory.Delete(tmp, true); } catch { /* best effort */ }
    }
  }

  /// <summary>Exactly the payload's files and directories, byte for byte, and nothing else.</summary>
  private static void AssertInstalled(string root, string who) {
    Assert.That(Directory.Exists(root), Is.True, $"{who} created no installation directory");
    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes);
    var expected = Expected();
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), $"{who}: files");
    foreach (var (name, data) in expected)
      Assert.That(files[name], Is.EqualTo(data), $"{who}: {name}");
    var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
      .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).ToHashSet();
    Assert.That(dirs, Is.EquivalentTo(new[] { "bin", "docs", "docs/deep", "docs/deep/nested", "empty dir" }), $"{who}: directories");
  }
}
