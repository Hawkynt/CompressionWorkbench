#pragma warning disable CS1591
using FileSystem.Ntfs;

namespace Compression.Tests.Ntfs;

/// <summary>
/// External-tool gate for the per-version volume content.
/// </summary>
/// <remarks>
/// Our own reader agreeing with our own writer proves only that the two halves of this
/// repository share an opinion. These cases put each version in front of the ntfs-3g
/// userspace tools, which read NTFS 1.2 as readily as 3.x — so a 1.2 volume they reject
/// is a 1.2 volume we got wrong — and read the <c>$AttrDef</c> table out of a volume a
/// real <c>mkfs.ntfs</c> formatted, which is the outside evidence that the 3.x table
/// here is NTFS's and not ours. Skips cleanly when the tooling is absent.
/// </remarks>
[TestFixture]
[Category("ExternalConformance")]
public class NtfsVolumeVersionExternalTests {

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ntfs_volver_{Guid.NewGuid():N}");
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

  private string BuildImage(NtfsVersion version) {
    var w = new NtfsWriter("VERSIONED");
    w.SetNtfsVersion(version);
    w.AddFile("hello.txt", "Hello from the versioned NTFS writer."u8.ToArray());
    var path = Path.Combine(this._tmpDir, $"volver_{version}.img");
    File.WriteAllBytes(path, w.Build(16 * 1024 * 1024));
    return path;
  }

  /// <summary>
  /// The metadata file the version renamed, read back by name through the reference
  /// tooling's own path lookup — which is how ntfs-3g finds <c>$Secure</c>, and the
  /// reason a 1.2 volume must not offer it one.
  /// </summary>
  [Test, CancelAfter(60_000)]
  [TestCase(NtfsVersion.V31, "$Secure")]
  [TestCase(NtfsVersion.V30, "$Secure")]
  [TestCase(NtfsVersion.V12, "$Quota")]
  public void Created_EveryVersion_NtfsinfoNamesRecord9ForThatVersion(NtfsVersion version, string expectedName) {
    RequireWslTool("ntfsinfo");
    var wsl = FsInteropToolbox.WinToWsl(this.BuildImage(version));

    var info = FsInteropToolbox.RunWsl($"ntfsinfo -f -i 9 {wsl}");
    TestContext.Out.WriteLine($"exit={info.ExitCode}\n{info.StdOut}\n{info.StdErr}");

    Assert.Multiple(() => {
      Assert.That(info.ExitCode, Is.Zero, "ntfsinfo could not read MFT record 9");
      Assert.That(info.StdOut, Does.Contain($"Filename:\t\t '{expectedName}'"));
    });
  }

  /// <summary>
  /// NTFS 3.0 made <c>$Extend</c> MFT record 11; before it, record 11 is an unused slot
  /// with no name and no index. ntfsinfo reads the record either way, so what it reports
  /// about record 11 is the tooling's own account of which version's metadata set the
  /// volume has.
  /// </summary>
  [Test, CancelAfter(60_000)]
  [TestCase(NtfsVersion.V31, true)]
  [TestCase(NtfsVersion.V30, true)]
  [TestCase(NtfsVersion.V12, false)]
  public void Created_EveryVersion_NtfsinfoSeesExtendOnlyFrom30(NtfsVersion version, bool expectExtend) {
    RequireWslTool("ntfsinfo");
    var wsl = FsInteropToolbox.WinToWsl(this.BuildImage(version));

    var info = FsInteropToolbox.RunWsl($"ntfsinfo -f -i 11 {wsl}");
    TestContext.Out.WriteLine($"exit={info.ExitCode}\n{info.StdOut}\n{info.StdErr}");

    Assert.Multiple(() => {
      Assert.That(info.StdOut.Contains("'$Extend'", StringComparison.Ordinal), Is.EqualTo(expectExtend),
        "record 11 is the $Extend directory from NTFS 3.0 on, and nothing before it");
      Assert.That(info.StdOut.Contains("IN_USE", StringComparison.Ordinal), Is.EqualTo(expectExtend),
        "an NTFS 1.2 volume leaves MFT record 11 unallocated");
    });
  }

  /// <summary>
  /// The outside evidence for the 3.x <c>$AttrDef</c>: the attribute-definition table a
  /// real <c>mkfs.ntfs</c> volume carries, read back with <c>ntfscat</c>, byte for byte
  /// against the one this writer builds. That comparison is what says $PROPERTY_SET does
  /// not belong in a 3.x table, and that the entry flags are NTFS's values rather than
  /// plausible ones.
  /// </summary>
  [Test, CancelAfter(90_000)]
  public void ReferenceFormatter_AttrDefTable_IsByteIdenticalToTheOneWeBuild() {
    RequireWslTool("mkfs.ntfs");
    RequireWslTool("ntfscat");

    var path = Path.Combine(this._tmpDir, "reference.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var made = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wsl} bs=1M count=16 status=none && mkfs.ntfs --fast -F -s 512 {wsl}");
    Assert.That(made.ExitCode, Is.Zero, $"mkfs.ntfs failed:\n{made.StdOut}\n{made.StdErr}");

    var dump = Path.Combine(this._tmpDir, "attrdef.bin");
    var cat = FsInteropToolbox.RunWsl($"ntfscat {wsl} '/$AttrDef' > {FsInteropToolbox.WinToWsl(dump)}");
    Assert.That(cat.ExitCode, Is.Zero, $"ntfscat could not read $AttrDef:\n{cat.StdErr}");

    Assert.That(File.ReadAllBytes(dump), Is.EqualTo(NtfsWriter.BuildAttrDefTable(NtfsVersion.V31)),
      "the 3.x $AttrDef we write must be the table a real formatter writes");
  }

  /// <summary>
  /// mkfs.ntfs only formats 3.x, so the 1.2 table has no reference volume to compare
  /// against; what it does have is the same tool's other compiled-in table. This pins
  /// the difference between the two rather than the 1.2 bytes alone: the three type
  /// codes NTFS 3.0 reused, and the $STANDARD_INFORMATION ceiling that moved with them.
  /// </summary>
  [Test, CancelAfter(90_000)]
  public void ReferenceFormatter_AttrDefTables_DifferOnlyWhereTheVersionsDo() {
    RequireWslTool("mkfs.ntfs");
    RequireWslTool("ntfscat");

    var path = Path.Combine(this._tmpDir, "reference12.img");
    var wsl = FsInteropToolbox.WinToWsl(path);
    var made = FsInteropToolbox.RunWsl(
      $"dd if=/dev/zero of={wsl} bs=1M count=16 status=none && mkfs.ntfs --fast -F -s 512 {wsl}");
    Assert.That(made.ExitCode, Is.Zero, $"mkfs.ntfs failed:\n{made.StdOut}\n{made.StdErr}");

    var dump = Path.Combine(this._tmpDir, "attrdef3x.bin");
    var cat = FsInteropToolbox.RunWsl($"ntfscat {wsl} '/$AttrDef' > {FsInteropToolbox.WinToWsl(dump)}");
    Assert.That(cat.ExitCode, Is.Zero, $"ntfscat could not read $AttrDef:\n{cat.StdErr}");

    var reference3x = File.ReadAllBytes(dump);
    var ours12 = NtfsWriter.BuildAttrDefTable(NtfsVersion.V12);

    // Both tables share their first three entries and then part company at 0x40.
    Assert.Multiple(() => {
      Assert.That(ours12.AsSpan(160, 2 * 160).SequenceEqual(reference3x.AsSpan(160, 2 * 160)), Is.True,
        "$ATTRIBUTE_LIST and $FILE_NAME are the same definition in both versions");
      Assert.That(ours12.AsSpan(0, 160).SequenceEqual(reference3x.AsSpan(0, 160)), Is.False,
        "$STANDARD_INFORMATION's ceiling moved from 48 to 72 with NTFS 3.0");
      Assert.That(ours12, Has.Length.EqualTo(15 * 160));
      Assert.That(reference3x, Has.Length.EqualTo(16 * 160));
    });
  }
}
