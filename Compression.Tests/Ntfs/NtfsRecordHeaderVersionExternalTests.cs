#pragma warning disable CS1591
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// External-tool gate for the two FILE record header layouts.
/// </summary>
/// <remarks>
/// Our own reader liking a volume proves only that the two halves of this repository
/// agree. These cases hand both layouts to the ntfs-3g userspace tools and, separately,
/// read the header a real <c>mkfs.ntfs</c> writes — which is what says the NTFS 3.1
/// records we now emit are the shape NTFS actually uses. Skips cleanly when the tooling
/// is absent.
/// </remarks>
[TestFixture]
[Category("ExternalConformance")]
public class NtfsRecordHeaderVersionExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_ver_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static void RequireWslTool(string tool, string aptPackage = "ntfs-3g") {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not installed. Run `wsl --install` in Admin PowerShell and reboot, " +
                    "then `sudo apt install -y ntfs-3g` inside the Linux shell.");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"WSL is present but '{tool}' is not installed in the distro. " +
                    $"Run inside WSL: `sudo apt install -y {aptPackage}`.");
  }

  private string BuildImage(byte minorVersion, string fileName) {
    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsMinorVersion(minorVersion);
    w.AddFile("hello.txt", "Hello from the versioned NTFS writer."u8.ToArray());
    w.AddFile("payload.bin", Payload(20_000));
    var path = Path.Combine(this._tmpDir, fileName);
    File.WriteAllBytes(path, w.Build(16 * 1024 * 1024));
    return path;
  }

  private static byte[] Payload(int length) {
    var data = new byte[length];
    new Random(31).NextBytes(data);
    return data;
  }

  private static string VersionText(byte minorVersion) => minorVersion == 1 ? "3.1" : "3.0";

  // ── Both layouts must satisfy the reference tooling ─────────────────────

  [Test, CancelAfter(60_000)]
  [TestCase((byte)1)]
  [TestCase((byte)0)]
  public void Created_EitherLayout_NtfsfixAcceptsAndNamesTheVersion(byte minorVersion) {
    RequireWslTool("ntfsfix");
    var image = this.BuildImage(minorVersion, $"ntfsfix_{minorVersion}.img");

    var result = FsInteropToolbox.RunWsl($"ntfsfix --no-action {FsInteropToolbox.WinToWsl(image)}");
    TestContext.Out.WriteLine($"exit={result.ExitCode}\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");

    Assert.Multiple(() => {
      Assert.That(result.ExitCode, Is.Zero, "ntfsfix rejected the volume");
      Assert.That(result.StdOut, Does.Contain("Processing of $MFT and $MFTMirr completed successfully"));
      Assert.That(result.StdOut, Does.Contain($"NTFS volume version is {VersionText(minorVersion)}"),
        "ntfsfix must read back the version the volume was created as");
    });
  }

  [Test, CancelAfter(60_000)]
  [TestCase((byte)1)]
  [TestCase((byte)0)]
  public void Created_EitherLayout_NtfsinfoAndNtfslsWalkTheMft(byte minorVersion) {
    RequireWslTool("ntfsinfo");
    RequireWslTool("ntfsls");
    var image = this.BuildImage(minorVersion, $"ntfsinfo_{minorVersion}.img");
    var wsl = FsInteropToolbox.WinToWsl(image);

    var info = FsInteropToolbox.RunWsl($"ntfsinfo -m {wsl}");
    var list = FsInteropToolbox.RunWsl($"ntfsls {wsl}");
    TestContext.Out.WriteLine($"ntfsinfo exit={info.ExitCode}\n{info.StdOut}\n{info.StdErr}");
    TestContext.Out.WriteLine($"ntfsls exit={list.ExitCode}\n{list.StdOut}\n{list.StdErr}");

    Assert.Multiple(() => {
      Assert.That(info.ExitCode, Is.Zero, "ntfsinfo rejected the volume");
      Assert.That(info.StdOut, Does.Contain($"Volume Version: {VersionText(minorVersion)}"));
      Assert.That(list.ExitCode, Is.Zero, "ntfsls rejected the volume");
      Assert.That(list.StdOut, Does.Contain("hello.txt"));
      Assert.That(list.StdOut, Does.Contain("payload.bin"));
    });
  }

  [Test, CancelAfter(90_000)]
  [TestCase((byte)1)]
  [TestCase((byte)0)]
  public void InPlaceAdd_EitherLayout_StaysAcceptableToNtfsfix(byte minorVersion) {
    RequireWslTool("ntfsfix");
    RequireWslTool("ntfscat");

    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsMinorVersion(minorVersion);
    w.AddFile("seed.txt", "seed"u8.ToArray());
    var image = w.Build(16 * 1024 * 1024);

    var added = "added in place, in the layout the volume already used"u8.ToArray();
    NtfsInPlaceAdder.AddFile(image, "added.txt", added);

    var path = Path.Combine(this._tmpDir, $"inplace_{minorVersion}.img");
    File.WriteAllBytes(path, image);
    var wsl = FsInteropToolbox.WinToWsl(path);

    var fix = FsInteropToolbox.RunWsl($"ntfsfix --no-action {wsl}");
    var cat = FsInteropToolbox.RunWsl($"ntfscat {wsl} /added.txt");
    TestContext.Out.WriteLine($"ntfsfix exit={fix.ExitCode}\n{fix.StdOut}\n{fix.StdErr}");
    TestContext.Out.WriteLine($"ntfscat exit={cat.ExitCode}\n{cat.StdErr}");

    Assert.Multiple(() => {
      Assert.That(fix.ExitCode, Is.Zero, "ntfsfix rejected the volume after an in-place add");
      Assert.That(fix.StdOut, Does.Contain($"NTFS volume version is {VersionText(minorVersion)}"),
        "an in-place add must not change the volume's version or its record layout");
      Assert.That(cat.ExitCode, Is.Zero, "ntfscat could not read the added file");
      Assert.That(cat.StdOut.TrimEnd('\n', '\r'), Is.EqualTo(System.Text.Encoding.UTF8.GetString(added)));
    });
  }

  /// <summary>
  /// The outside evidence that 48/44 is NTFS rather than our own invention: a volume
  /// formatted by mkfs.ntfs puts its update-sequence array at 48 and names each record
  /// at 44, which is exactly what we now write for a 3.1 volume.
  /// </summary>
  [Test, CancelAfter(90_000)]
  public void ReferenceFormatter_WritesTheExtendedHeaderWeNowMatch() {
    RequireWslTool("mkfs.ntfs", "ntfs-3g");

    var path = Path.Combine(this._tmpDir, "reference.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var made = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wsl} bs=1M count=16 status=none && mkfs.ntfs --fast -F -s 512 {wsl}");
    Assert.That(made.ExitCode, Is.Zero, $"mkfs.ntfs failed:\n{made.StdOut}\n{made.StdErr}");

    var reference = File.ReadAllBytes(path);
    var ours = new NtfsWriter("VERSIONED");
    ours.SetNtfsMinorVersion(1);
    ours.AddFile("hello.txt", "hello"u8.ToArray());
    var mine = ours.Build(16 * 1024 * 1024);

    Assert.Multiple(() => {
      for (uint rec = 0; rec < 6; ++rec) {
        var theirs = MftInspector.ReadRawRecord(reference, rec);
        Assert.That(MftInspector.UpdateSequenceOffset(theirs), Is.EqualTo(48),
          $"reference record {rec} should use the NTFS 3.1 header");
        Assert.That(MftInspector.RecordNumberField(theirs), Is.EqualTo(rec),
          $"reference record {rec} should name itself at offset 44");

        var raw = MftInspector.ReadRawRecord(mine, rec);
        Assert.That(MftInspector.UpdateSequenceOffset(raw), Is.EqualTo(MftInspector.UpdateSequenceOffset(theirs)));
        Assert.That(MftInspector.RecordNumberField(raw), Is.EqualTo(MftInspector.RecordNumberField(theirs)));
      }
    });
  }
}
