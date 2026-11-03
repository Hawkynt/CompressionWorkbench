using Compression.Registry;
using FileSystem.HighSierra;

namespace Compression.Tests.HighSierra;

/// <summary>
/// Third-party proof for the High Sierra writer: the Linux <c>isofs</c> driver, which
/// detects High Sierra by its "CDROM" descriptor on its own, mounts what Create writes —
/// through a libguestfs appliance, the WSL kernel having no isofs — and every file it
/// reads back must match byte for byte.
/// </summary>
/// <remarks>
/// Skips (never fails) when <c>guestfish</c> or a working appliance is missing. The sudo
/// password (to make <c>/dev/kvm</c> and <c>/boot/vmlinuz-*</c> readable) defaults to the
/// test environment's and can be set with <c>CWB_SUDO_PASSWORD</c>.
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
[Category("Slow")]
public sealed class HighSierraKernelMountTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_hsf_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Pattern(int size, int seed) {
    var d = new byte[size];
    for (var i = 0; i < size; ++i) d[i] = (byte)(i * 29 + seed + (i >> 11));
    return d;
  }

  [Test]
  public void GivenOurVolume_WhenKernelIsofsMountsIt_ThenTreeAndBytesMatch() {
    RequireAppliance();
    var expected = new Dictionary<string, byte[]> {
      ["README.TXT"] = "high sierra kernel mount\n"u8.ToArray(),
      ["EMPTY.DAT"] = [],
      ["NOEXT"] = Pattern(2048, 1),
      ["DOCS/GUIDE.TXT"] = "guide\n"u8.ToArray(),
      ["DOCS/API/REF.BIN"] = Pattern(300_000, 2),
      ["L1/L2/L3/L4/L5/L6/L7/DEEP.TXT"] = "eight levels\n"u8.ToArray(),
    };
    for (var i = 0; i < 120; ++i) expected[$"MANY/FILE_{i:000}.DAT"] = [(byte)i, (byte)(i >> 8)];

    var img = Path.Combine(this._tmpDir, "ours.hsf");
    using (var fs = File.Create(img))
      new HighSierraFormatDescriptor().Create(fs,
        [.. expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)), new ArchiveInputInfo("", "EMPTYDIR/", true)],
        new FormatCreateOptions());

    var outDir = Path.Combine(this._tmpDir, "out");
    Directory.CreateDirectory(outDir);
    var tar = Path.Combine(outDir, "k.tar");
    var script = Path.Combine(this._tmpDir, "gf.txt");
    File.WriteAllText(script, string.Join('\n', "run", "mount-vfs ro iso9660 /dev/sda /", "find /", $"tar-out / {Unquoted(tar)}", "umount /") + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {Unquoted(img)} -f {Unquoted(script)} 2>&1 " +
                                    $"&& mkdir -p {Unquoted(outDir)}/x && tar -xf {Unquoted(tar)} -C {Unquoted(outDir)}/x");
    Assert.That(r.ExitCode, Is.Zero, $"isofs mount through guestfish:\n{r.StdOut}{r.StdErr}");

    // isofs shows High Sierra names in lower case (map=normal) without the ";1" version.
    var root = Path.Combine(outDir, "x");
    var got = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
      .ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
    Assert.That(got.Keys, Is.EquivalentTo(expected.Keys).Using((IEqualityComparer<string>)StringComparer.OrdinalIgnoreCase), "files the kernel lists");
    foreach (var (name, data) in expected)
      Assert.That(got[name], Is.EqualTo(data), $"{name} through the kernel");
    Assert.That(Directory.Exists(Path.Combine(root, "emptydir")) || Directory.Exists(Path.Combine(root, "EMPTYDIR")), Is.True, "the empty directory");
  }

  private static string Unquoted(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static bool? _capable;

  private static void RequireAppliance() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("'guestfish' not available (install libguestfs-tools).");
    _capable ??= FsInteropToolbox.RunWsl(ApplianceEnv + " d=$(mktemp -d); truncate -s 1M $d/p.img; " +
      "printf 'run\\nmkfs ext2 /dev/sda\\nmount /dev/sda /\\nls /\\numount /\\n' | timeout 600 guestfish -a $d/p.img 2>&1; rm -rf $d")
      .StdOut.Contains("lost+found", StringComparison.Ordinal);
    if (_capable != true)
      Assert.Ignore("the libguestfs appliance does not start here (no kernel image or no KVM).");
  }
}
