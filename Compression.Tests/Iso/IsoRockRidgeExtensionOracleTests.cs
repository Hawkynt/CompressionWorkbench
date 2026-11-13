#pragma warning disable CS1591
using System.Text;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Third-party proof for the Rock Ridge extensions the writer records — symbolic links (SL),
/// directories deeper than eight levels relocated with RE/CL/PL, and zisofs files (ZF):
/// the Linux kernel's isofs mounts the image and resolves the links, reads the deep paths
/// where they belong and decompresses the zisofs files on the fly; xorriso loads it without a
/// complaint and lists the same tree; isoinfo -R lists links and deep paths; and zisofs-tools'
/// mkzftree decompresses our zisofs data, while we read xorriso's.
/// </summary>
/// <remarks>Skips (never fails) when WSL, a tool or a working libguestfs appliance is missing.</remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
public sealed class IsoRockRidgeExtensionOracleTests {
  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_isorr_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static byte[] Text(int bytes) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("zisofs compresses this line well. ", bytes / 34 + 1)))[..bytes];

  private static readonly string DeepDir = string.Join('/', Enumerable.Range(1, 12).Select(i => $"d{i:D2}"));

  /// <summary>Links (absolute, relative, into the deep tree, dangling), a 12-level tree and zisofs files.</summary>
  private string BuildImage() {
    var w = new IsoWriter { EnableRockRidge = true, EnableJoliet = true, EnableZisofs = true };
    w.AddFile("etc/real.conf", "real\n"u8.ToArray());
    w.AddSymlink("etc/relative.conf", "real.conf");
    w.AddSymlink("abs-link", "/etc/real.conf");
    w.AddSymlink("deep-link", DeepDir + "/deepest.txt");
    w.AddSymlink("dangling", "../nowhere/at/all");
    w.AddFile(DeepDir + "/deepest.txt", "twelve levels down\n"u8.ToArray());
    w.AddFile("d01/d02/d03/d04/d05/d06/d07/d08/d09/at-nine.txt", "nine\n"u8.ToArray());
    w.AddFile("compressed/text.txt", Text(400_000));
    w.AddFile("compressed/zeros.bin", new byte[150_000]);
    var path = Path.Combine(this._dir, "rr.iso");
    File.WriteAllBytes(path, w.Build());
    return path;
  }

  [Test]
  public void GivenOurImage_WhenTheKernelMountsIt_ThenLinksResolveDeepPathsReadAndZisofsDecompresses() {
    RequireAppliance();
    var image = this.BuildImage();
    var script = Path.Combine(this._dir, "gf.txt");
    File.WriteAllText(script, string.Join('\n', new[] {
      "run",
      "mount-vfs ro iso9660 /dev/sda /",
      "readlink /etc/relative.conf",
      "readlink /abs-link",
      "readlink /dangling",
      "cat /etc/relative.conf",
      "cat /abs-link",
      "cat /deep-link",
      $"cat /{DeepDir}/deepest.txt",
      "cat /d01/d02/d03/d04/d05/d06/d07/d08/d09/at-nine.txt",
      "checksum sha256 /compressed/text.txt",
      "checksum sha256 /compressed/zeros.bin",
      "filesize /compressed/text.txt",
      "ls /",
      "umount /",
    }) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {U(image)} -f {U(script)} 2>&1");
    var output = r.StdOut + r.StdErr;
    Assert.That(r.ExitCode, Is.Zero, output);
    var lines = output.Split('\n').Select(l => l.Trim()).ToList();
    Assert.Multiple(() => {
      Assert.That(lines, Does.Contain("real.conf"), "relative link target");
      Assert.That(lines, Does.Contain("/etc/real.conf"), "absolute link target");
      Assert.That(lines, Does.Contain("../nowhere/at/all"), "dangling link target");
      Assert.That(lines.Count(l => l == "real"), Is.EqualTo(2), "both links resolve to the file");
      Assert.That(lines.Count(l => l == "twelve levels down"), Is.EqualTo(2), "the deep file, directly and through a link");
      Assert.That(lines, Does.Contain("nine"));
      Assert.That(lines, Does.Contain(Sha256(Text(400_000))), "zisofs text decompressed by the kernel");
      Assert.That(lines, Does.Contain(Sha256(new byte[150_000])), "zisofs zero blocks decompressed by the kernel");
      Assert.That(lines, Does.Contain("400000"), "the kernel reports the uncompressed size");
      Assert.That(lines, Does.Contain("d01"));
    });
  }

  [Test]
  public void GivenOurImage_WhenXorrisoAndIsoinfoReadIt_ThenTheyShowTheLinksTheDeepTreeAndTheContent() {
    RequireTool("xorriso");
    RequireTool("isoinfo");
    var image = this.BuildImage();

    var find = FsInteropToolbox.RunWsl($"xorriso -abort_on FAILURE -return_with SORRY 32 -indev {Q(image)} -find / -exec lsdl 2>&1");
    Assert.That(find.ExitCode, Is.Zero, find.StdOut + find.StdErr);
    var listing = find.StdOut + find.StdErr;
    Assert.Multiple(() => {
      Assert.That(listing, Does.Not.Contain("SORRY").And.Not.Contain("FAILURE"));
      Assert.That(listing, Does.Contain("'/abs-link' -> '/etc/real.conf'"));
      Assert.That(listing, Does.Contain("'/etc/relative.conf' -> 'real.conf'"));
      Assert.That(listing, Does.Contain($"'/{DeepDir}/deepest.txt'"), "xorriso follows CL to the deep tree");
      Assert.That(listing, Does.Not.Contain("rr_moved/d"), "relocated directories are shown at their real place");
    });

    var outDir = Path.Combine(this._dir, "x");
    var extract = FsInteropToolbox.RunWsl($"xorriso -osirrox on -zisofs by_magic=on -indev {Q(image)} -extract / {Q(outDir)} 2>&1");
    Assert.That(extract.ExitCode, Is.Zero, extract.StdOut + extract.StdErr);
    Assert.Multiple(() => {
      Assert.That(File.ReadAllText(Path.Combine(outDir, DeepDir.Replace('/', Path.DirectorySeparatorChar), "deepest.txt")), Is.EqualTo("twelve levels down\n"));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "compressed", "text.txt")), Is.EqualTo(Text(400_000)), "osirrox decompresses our zisofs");
    });

    // isoinfo walks the primary tree as recorded: d08 (ECMA-119 level 9) lives in rr_moved,
    // and its real parent d07 holds the zero-length CL placeholder of that name.
    var isoinfo = FsInteropToolbox.RunWsl($"isoinfo -R -l -i {Q(image)} 2>&1");
    Assert.Multiple(() => {
      Assert.That(isoinfo.StdOut, Does.Contain("abs-link -> /etc/real.conf"), isoinfo.StdOut);
      Assert.That(isoinfo.StdOut, Does.Contain("Directory listing of /rr_moved/d08/d09/d10/d11/d12/"), isoinfo.StdOut);
      Assert.That(isoinfo.StdOut, Does.Match(@"Directory listing of /d01/d02/d03/d04/d05/d06/d07/\n(?:.*\n){2}drwx\S+\s+\d+\s+\d+\s+\d+\s+0 .*\bd08\b"), "the CL placeholder");
    });
  }

  [Test]
  public void GivenZisofsData_WhenMkzftreeAndWeExchangeIt_ThenEachDecompressesTheOther() {
    RequireTool("mkzftree");
    RequireTool("xorriso");
    // mkzftree works on trees: a folder holding our zisofs form, decompressed into another.
    var data = Text(250_000);
    var ours = Directory.CreateDirectory(Path.Combine(this._dir, "ours")).FullName;
    File.WriteAllBytes(Path.Combine(ours, "file.txt"), Zisofs.Compress(data)!);
    var plain = Path.Combine(this._dir, "plain");
    var unpack = FsInteropToolbox.RunWsl($"mkzftree -u {Q(ours)} {Q(plain)} 2>&1");
    Assert.That(unpack.ExitCode, Is.Zero, unpack.StdOut + unpack.StdErr);
    Assert.That(File.ReadAllBytes(Path.Combine(plain, "file.txt")), Is.EqualTo(data), "mkzftree -u decompresses our zisofs form");

    // mkzftree's form in an image xorriso writes (-z marks pre-compressed files), read by us.
    var tree = Directory.CreateDirectory(Path.Combine(this._dir, "src")).FullName;
    File.WriteAllBytes(Path.Combine(tree, "big.txt"), data);
    var iso = Path.Combine(this._dir, "theirs.iso");
    var make = FsInteropToolbox.RunWsl($"mkzftree {Q(tree)} {Q(Path.Combine(this._dir, "zf"))} && xorriso -as mkisofs -R -z -o {Q(iso)} {Q(Path.Combine(this._dir, "zf"))} 2>&1");
    Assert.That(make.ExitCode, Is.Zero, make.StdOut + make.StdErr);
    var reader = new IsoReader(new MemoryStream(File.ReadAllBytes(iso)));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "big.txt")), Is.EqualTo(data), "we decompress mkzftree's zisofs");
  }

  [Test]
  public void GivenAnImageXorrisoMadeWithLinksAndADeepTree_WhenWeReadIt_ThenWeSeeWhatItHolds() {
    RequireTool("xorriso");
    var src = Directory.CreateDirectory(Path.Combine(this._dir, "tree")).FullName;
    var deep = Path.Combine(src, DeepDir.Replace('/', Path.DirectorySeparatorChar));
    Directory.CreateDirectory(deep);
    File.WriteAllText(Path.Combine(deep, "deepest.txt"), "deep");
    var iso = Path.Combine(this._dir, "xorriso.iso");
    var make = FsInteropToolbox.RunWsl($"ln -s {DeepDir}/deepest.txt {Q(Path.Combine(src, "link"))} && xorriso -as mkisofs -R -o {Q(iso)} {Q(src)} 2>&1");
    Assert.That(make.ExitCode, Is.Zero, make.StdOut + make.StdErr);
    var reader = new IsoReader(new MemoryStream(File.ReadAllBytes(iso)));
    Assert.Multiple(() => {
      Assert.That(reader.Entries.Single(e => e.Name == "link").LinkTarget, Is.EqualTo(DeepDir + "/deepest.txt"));
      Assert.That(Encoding.ASCII.GetString(reader.Extract(reader.Entries.Single(e => e.Name == DeepDir + "/deepest.txt"))), Is.EqualTo("deep"));
    });
  }

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));

  private static string Q(string path) => FsInteropToolbox.WinToWsl(path);

  private static string U(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static bool? _capable;

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable) Assert.Ignore("WSL not available.");
    if (!FsInteropToolbox.WslHasTool(tool)) Assert.Ignore($"'{tool}' not available in WSL.");
  }

  private static void RequireAppliance() {
    RequireTool("guestfish");
    _capable ??= FsInteropToolbox.RunWsl(ApplianceEnv + " d=$(mktemp -d); truncate -s 1M $d/p.img; " +
      "printf 'run\\nmkfs ext2 /dev/sda\\nmount /dev/sda /\\nls /\\numount /\\n' | timeout 600 guestfish -a $d/p.img 2>&1; rm -rf $d")
      .StdOut.Contains("lost+found", StringComparison.Ordinal);
    if (_capable != true) Assert.Ignore("the libguestfs appliance does not start here (no kernel image or no KVM).");
  }
}
