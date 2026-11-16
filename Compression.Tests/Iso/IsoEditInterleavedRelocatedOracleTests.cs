#pragma warning disable CS1591
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Iso;

namespace Compression.Tests.Iso;

/// <summary>
/// Third-party proof for in-place edits of ISO 9660 images with interleaved files and with Rock
/// Ridge relocated folders: the Linux kernel's isofs mounts the edited images three ways (Rock
/// Ridge, Joliet, plain ISO 9660) and reads every file where that view puts it; xorriso loads
/// them without a complaint and osirrox extracts them; isoinfo lists the recorded tree and
/// extracts from it; and Windows' CDFS driver reads the interleaved file and the files added
/// next to it.
/// </summary>
/// <remarks>Skips (never fails) when WSL, a tool, a working libguestfs appliance or Mount-DiskImage is missing.</remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
public sealed class IsoEditInterleavedRelocatedOracleTests {
  private static readonly IsoFormatDescriptor Iso = new();
  private const string DataSha = "26a249da34d96c852164fe299ccf991dd52362c5ca2af48045e53c1f679474c6";
  private const string Deep = "d01/d02/d03/d04/d05/d06/d07/d08/d09/d10/d11/d12";
  private static readonly string NewChain = IsoRelocatedEditTests.NewChain;
  private static readonly byte[] Added = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("added next to the interleaved file\n", 300)));

  private string _dir = null!;

  [SetUp]
  public void SetUp() => this._dir = Directory.CreateTempSubdirectory("cwb_isoedit_").FullName;

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static MemoryStream Interleaved(string name) {
    using var resource = typeof(IsoEditInterleavedRelocatedOracleTests).Assembly.GetManifestResourceStream($"IsoVectors.{name}.iso.gz")!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return image;
  }

  /// <summary>
  /// An interleaved reference image with new.bin and sub/inner.txt added in place (lower case,
  /// so the Joliet and the plain view name them alike).
  /// </summary>
  private string EditedInterleaved(string name) {
    using var image = Interleaved(name);
    Iso.Add(image, [ArchiveInputInfo.InMemory("new.bin", Added), ArchiveInputInfo.InMemory("sub/inner.txt", "inner\n"u8.ToArray())]);
    var path = Path.Combine(this._dir, name + ".iso");
    File.WriteAllBytes(path, image.ToArray());
    return path;
  }

  /// <summary>
  /// Our Rock Ridge + Joliet image with a writer-relocated tree, edited: a file added into the
  /// relocated folder and one removed from it, a second folder relocated next to it, and a new
  /// 11-level chain whose eighth folder is relocated by the editor.
  /// </summary>
  private string EditedRelocated() {
    using var image = IsoRelocatedEditTests.Ours();
    Iso.Add(image, [
      ArchiveInputInfo.InMemory(Deep + "/second.txt", "second\n"u8.ToArray()),
      ArchiveInputInfo.InMemory("d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt", "another relocation\n"u8.ToArray()),
      ArchiveInputInfo.InMemory(NewChain + "/new.txt", "eleven levels down\n"u8.ToArray()),
    ]);
    Iso.Remove(image, [Deep + "/deepest.txt"]);
    var path = Path.Combine(this._dir, "relocated.iso");
    File.WriteAllBytes(path, image.ToArray());
    return path;
  }

  /// <summary>genisoimage's relocated reference image, edited inside and below its relocated folder.</summary>
  private string EditedGenisoimage() {
    using var image = IsoRelocatedEditTests.Relocated();
    Iso.Add(image, [
      ArchiveInputInfo.InMemory("a/b/c/d/e/f/g/h/beside-i.txt", "in the relocated folder\n"u8.ToArray()),
      ArchiveInputInfo.InMemory(IsoRelocatedEditTests.MovedChain + "/k/l/m/further.txt", "below the relocated folder\n"u8.ToArray()),
    ]);
    Iso.Remove(image, ["sub/small.txt"]);
    var path = Path.Combine(this._dir, "rr-relocated.iso");
    File.WriteAllBytes(path, image.ToArray());
    return path;
  }

  private static string Guestfish(string image, string dir, params string[] commands) {
    var script = Path.Combine(dir, "gf.txt");
    File.WriteAllText(script, string.Join('\n', new[] { "run" }.Concat(commands)) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {U(image)} -f {U(script)} 2>&1");
    var output = r.StdOut + r.StdErr;
    Assert.That(r.ExitCode, Is.Zero, output);
    return output;
  }

  private static string[] Mount(string options, params string[] commands)
    => [$"mount-vfs {options} iso9660 /dev/sda /", .. commands, "umount /"];

  private static List<string> Lines(string output) => output.Split('\n').Select(l => l.Trim()).ToList();

  // ── Kernel isofs, three views ──────────────────────────────────────────

  [Test]
  public void GivenAnEditedRelocatedTree_WhenTheKernelMountsItThreeWays_ThenEachViewReadsTheFilesWhereItPutsThem() {
    RequireAppliance();
    var image = this.EditedRelocated();
    var output = Guestfish(image, this._dir, [
      // Rock Ridge: the real hierarchy through CL/PL.
      .. Mount("ro", $"cat /{Deep}/second.txt", $"cat /{NewChain}/new.txt", "cat /d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt",
        $"ls /{Deep}", "echo ---moved", "ls /rr_moved", "echo ---joliet"),
      // Joliet: the real hierarchy as recorded.
      .. Mount("ro,norock", $"cat /{Deep}/second.txt", $"cat /{NewChain}/new.txt", "cat /d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt",
        "echo ---plain"),
      // Plain ISO 9660: relocated folders in rr_moved, CL placeholders as empty files.
      .. Mount("ro,norock,nojoliet", "cat /rr_moved/d08/d09/d10/d11/d12/second.txt", "cat /rr_moved/x08/x09/x10/x11/new.txt",
        "cat /rr_moved/other/x/y/z.txt", "filesize /x01/x02/x03/x04/x05/x06/x07/x08", "ls /rr_moved"),
    ]);
    var lines = Lines(output);
    var rockRidge = lines.TakeWhile(l => l != "---joliet").ToList();
    var joliet = lines.SkipWhile(l => l != "---joliet").TakeWhile(l => l != "---plain").ToList();
    var plain = lines.SkipWhile(l => l != "---plain").ToList();
    Assert.Multiple(() => {
      foreach (var (view, seen) in new[] { ("Rock Ridge", rockRidge), ("Joliet", joliet), ("plain", plain) }) {
        Assert.That(seen, Does.Contain("second"), view);
        Assert.That(seen, Does.Contain("eleven levels down"), view);
        Assert.That(seen, Does.Contain("another relocation"), view);
      }
      Assert.That(rockRidge, Does.Not.Contain("deepest.txt"), "the removed file is gone");
      // isofs lists rr_moved itself but hides the RE records in it: each relocated folder
      // shows only where its CL placeholder is.
      Assert.That(rockRidge.SkipWhile(l => l != "---moved").Skip(1).Where(l => l.Length > 0), Is.Empty, "rr_moved shows nothing through Rock Ridge");
      Assert.That(plain, Does.Contain("0"), "the CL placeholder is an empty file without Rock Ridge");
      Assert.That(plain, Is.SupersetOf(new[] { "d08", "other", "x08" }), "all three relocated folders in rr_moved");
    });
  }

  [Test]
  public void GivenGenisoimagesRelocatedImageEdited_WhenTheKernelMountsItThreeWays_ThenEachViewReadsTheNewFiles() {
    RequireAppliance();
    var image = this.EditedGenisoimage();
    const string Chain = IsoRelocatedEditTests.MovedChain;
    var output = Guestfish(image, this._dir, [
      .. Mount("ro", "cat /a/b/c/d/e/f/g/h/beside-i.txt", $"cat /{Chain}/k/l/m/further.txt", $"cat /{Chain}/deep.txt",
        "ls /sub", "echo ---moved", "ls /rr_moved", "echo ---joliet"),
      .. Mount("ro,norock", "cat /a/b/c/d/e/f/g/h/beside-i.txt", $"cat /{Chain}/k/l/m/further.txt", $"cat /{Chain}/deep.txt",
        "echo ---plain"),
      .. Mount("ro,norock,nojoliet", "cat /rr_moved/h/beside_i.txt", "cat /rr_moved/h/i/j/k/l/m/further.txt", "cat /rr_moved/h/i/j/deep.txt",
        "filesize /a/b/c/d/e/f/g/h"),
    ]);
    var lines = Lines(output);
    var rockRidge = lines.TakeWhile(l => l != "---joliet").ToList();
    var joliet = lines.SkipWhile(l => l != "---joliet").TakeWhile(l => l != "---plain").ToList();
    var plain = lines.SkipWhile(l => l != "---plain").ToList();
    Assert.Multiple(() => {
      foreach (var (view, seen) in new[] { ("Rock Ridge", rockRidge), ("Joliet", joliet), ("plain", plain) }) {
        Assert.That(seen, Does.Contain("in the relocated folder"), view);
        Assert.That(seen, Does.Contain("below the relocated folder"), view);
        Assert.That(seen, Does.Contain("nine levels down"), $"{view}: genisoimage's own deep file is untouched");
      }
      Assert.That(rockRidge, Does.Not.Contain("small.txt"), "the removed file is gone");
      // isofs lists rr_moved itself but hides the RE records in it: each relocated folder
      // shows only where its CL placeholder is.
      Assert.That(rockRidge.SkipWhile(l => l != "---moved").Skip(1).Where(l => l.Length > 0), Is.Empty, "rr_moved shows nothing through Rock Ridge");
      Assert.That(plain, Does.Contain("0"), "the CL placeholder is an empty file without Rock Ridge");
    });
  }

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnEditedInterleavedImage_WhenTheKernelMountsItThreeWays_ThenTheTreeAndTheNewFilesRead(string name) {
    RequireAppliance();
    var image = this.EditedInterleaved(name);
    // isofs lists an interleaved file but does not read its units (it reports it empty);
    // CDFS reads it in the Windows test below.
    var output = Guestfish(image, this._dir, [
      .. Mount("ro", "checksum sha256 /new.bin", "cat /sub/inner.txt", "ls /"),
      .. Mount("ro,norock", "checksum sha256 /new.bin", "cat /sub/inner.txt", "ls /"),
      .. Mount("ro,norock,nojoliet", "checksum sha256 /new.bin", "cat /sub/inner.txt", "ls /"),
    ]);
    var lines = Lines(output);
    Assert.Multiple(() => {
      Assert.That(lines.Count(l => l == Sha256(Added)), Is.EqualTo(3), "the added file in every view");
      Assert.That(lines.Count(l => l == "inner"), Is.EqualTo(3), "the added folder in every view");
      Assert.That(lines.Count(l => l.Equals("data.bin", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(3), "the interleaved file is still listed");
    });
  }

  // ── xorriso / osirrox ──────────────────────────────────────────────────

  [Test]
  public void GivenAnEditedRelocatedTree_WhenXorrisoLoadsItAndOsirroxExtractsIt_ThenTheRealHierarchyComesOut() {
    RequireTool("xorriso");
    foreach (var (image, expected, absent) in new[] {
      (this.EditedRelocated(),
        new Dictionary<string, string> { [Deep + "/second.txt"] = "second\n", [NewChain + "/new.txt"] = "eleven levels down\n",
          ["d01/d02/d03/d04/d05/d06/d07/other/x/y/z.txt"] = "another relocation\n", ["top.txt"] = "top\n" },
        Deep + "/deepest.txt"),
      (this.EditedGenisoimage(),
        new Dictionary<string, string> { ["a/b/c/d/e/f/g/h/beside-i.txt"] = "in the relocated folder\n",
          [IsoRelocatedEditTests.MovedChain + "/k/l/m/further.txt"] = "below the relocated folder\n",
          [IsoRelocatedEditTests.MovedChain + "/deep.txt"] = "nine levels down\n" },
        "sub/small.txt"),
    }) {
      var find = FsInteropToolbox.RunWsl($"xorriso -abort_on FAILURE -return_with SORRY 32 -indev {Q(image)} -find / -exec lsdl 2>&1");
      var listing = find.StdOut + find.StdErr;
      Assert.That(find.ExitCode, Is.Zero, listing);
      Assert.That(listing, Does.Not.Contain("SORRY").And.Not.Contain("FAILURE").And.Not.Contain("WARNING"), listing);
      Assert.That(listing, Does.Not.Contain("/rr_moved/"), "relocated folders are shown at their real place");

      var outDir = Path.Combine(this._dir, "x-" + Path.GetFileNameWithoutExtension(image));
      var extract = FsInteropToolbox.RunWsl($"xorriso -osirrox on -indev {Q(image)} -extract / {Q(outDir)} 2>&1");
      Assert.That(extract.ExitCode, Is.Zero, extract.StdOut + extract.StdErr);
      Assert.Multiple(() => {
        foreach (var (path, content) in expected)
          Assert.That(File.ReadAllText(Path.Combine(outDir, path.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(content), path);
        Assert.That(File.Exists(Path.Combine(outDir, absent.Replace('/', Path.DirectorySeparatorChar))), Is.False, absent);
        var moved = Path.Combine(outDir, "rr_moved");
        Assert.That(Directory.Exists(moved) && Directory.EnumerateFileSystemEntries(moved).Any(), Is.False, "rr_moved shows nothing");
      });
    }
  }

  // ── isoinfo ────────────────────────────────────────────────────────────

  [Test]
  public void GivenAnEditedRelocatedTree_WhenIsoinfoListsAndExtractsIt_ThenRecordedAndJolietTreesAgree() {
    RequireTool("isoinfo");
    var image = this.EditedRelocated();

    var rockRidge = FsInteropToolbox.RunWsl($"isoinfo -R -l -i {Q(image)} 2>&1").StdOut;
    var joliet = FsInteropToolbox.RunWsl($"isoinfo -J -l -i {Q(image)} 2>&1").StdOut;
    var plain = FsInteropToolbox.RunWsl($"isoinfo -x '/RR_MOVED/X08/X09/X10/X11/NEW.TXT;1' -i {Q(image)} 2>&1").StdOut;
    var jfile = FsInteropToolbox.RunWsl($"isoinfo -J -x '/{NewChain}/new.txt' -i {Q(image)} 2>&1").StdOut;
    Assert.Multiple(() => {
      // isoinfo -R names the records as recorded: relocated folders in rr_moved, CL placeholders
      // as zero-length entries at the real place.
      Assert.That(rockRidge, Does.Contain("Directory listing of /rr_moved/x08/x09/x10/x11/"), rockRidge);
      Assert.That(rockRidge, Does.Contain("Directory listing of /rr_moved/other/x/y/"), rockRidge);
      Assert.That(rockRidge, Does.Contain("Directory listing of /rr_moved/d08/d09/d10/d11/d12/"), rockRidge);
      Assert.That(rockRidge, Does.Match(@"Directory listing of /x01/x02/x03/x04/x05/x06/x07/\n(?:.*\n){2}\S+\s+\d+\s+\d+\s+\d+\s+0 .*\bx08\b"), "the new CL placeholder");
      Assert.That(rockRidge, Does.Not.Contain("deepest.txt"));
      Assert.That(joliet, Does.Contain($"Directory listing of /{NewChain}/"), joliet);
      Assert.That(joliet, Does.Contain($"Directory listing of /{Deep}/"), joliet);
      Assert.That(joliet, Does.Not.Contain("rr_moved"), "Joliet keeps the real hierarchy");
      Assert.That(plain, Is.EqualTo("eleven levels down\n"));
      Assert.That(jfile, Is.EqualTo("eleven levels down\n"));
    });
  }

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnEditedInterleavedImage_WhenIsoinfoListsIt_ThenItShowsTheInterleavedFileAndExtractsTheNewOnes(string name) {
    RequireTool("isoinfo");
    var image = this.EditedInterleaved(name);
    var listing = FsInteropToolbox.RunWsl($"isoinfo -l -i {Q(image)} 2>&1").StdOut;
    var added = FsInteropToolbox.RunWsl($"isoinfo -x '/NEW.BIN;1' -i {Q(image)} 2>&1 | sha256sum").StdOut;
    var inner = FsInteropToolbox.RunWsl($"isoinfo -x '/SUB/INNER.TXT;1' -i {Q(image)} 2>&1").StdOut;
    Assert.Multiple(() => {
      Assert.That(listing, Does.Contain("DATA.BIN;1").And.Contain("NEW.BIN;1").And.Contain("Directory listing of /SUB/"), listing);
      Assert.That(added, Does.StartWith(Sha256(Added)));
      Assert.That(inner, Is.EqualTo("inner\n"));
    });
  }

  // ── Windows CDFS ───────────────────────────────────────────────────────

  [TestCase("interleave-1-1")]
  [TestCase("interleave-2-3")]
  public void GivenAnEditedInterleavedImage_WhenWindowsMountsIt_ThenCdfsReadsTheInterleavedFileAndTheNewOnes(string name) {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows CDFS is only on Windows.");
    var image = this.EditedInterleaved(name);
    var script = Path.Combine(this._dir, "read.ps1");
    File.WriteAllText(script, string.Join('\n',
      "param([string]$Iso)",
      "$ErrorActionPreference = 'Stop'",
      "Import-Module (Join-Path $PSHOME 'Modules/Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1') -Force",
      "$img = Mount-DiskImage -ImagePath $Iso -PassThru",
      "try {",
      "  Start-Sleep -Milliseconds 500",
      "  $v = $img | Get-Volume",
      "  \"fs $($v.FileSystem)\"",
      "  foreach ($f in 'DATA.BIN', 'NEW.BIN', 'SUB\\INNER.TXT') {",
      "    $p = \"$($v.DriveLetter):\\$f\"",
      "    \"$f $((Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLower()) $((Get-Item -LiteralPath $p).Length)\"",
      "  }",
      "} finally { Dismount-DiskImage -ImagePath $Iso | Out-Null }") + "\n");
    var psi = new ProcessStartInfo("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Iso", image]) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
    };
    string output;
    try {
      using var p = Process.Start(psi)!;
      var stderr = p.StandardError.ReadToEndAsync();
      output = p.StandardOutput.ReadToEnd();
      if (!p.WaitForExit(120_000)) { p.Kill(true); Assert.Ignore("Mount-DiskImage did not finish."); }
      output += stderr.Result;
    } catch (System.ComponentModel.Win32Exception) {
      Assert.Ignore("PowerShell is not available.");
      return;
    }
    if (!output.Contains("fs CDFS", StringComparison.Ordinal) && !output.Contains("fs UDF", StringComparison.Ordinal))
      Assert.Ignore($"Mount-DiskImage is not usable here: {output.Trim()}");
    var lines = Lines(output);
    Assert.Multiple(() => {
      Assert.That(lines, Does.Contain("fs CDFS"), output);
      Assert.That(lines, Does.Contain($"DATA.BIN {DataSha} 10940"), "CDFS reads the interleaved units across the kept gaps");
      Assert.That(lines, Does.Contain($"NEW.BIN {Sha256(Added)} {Added.Length}"));
      Assert.That(lines, Does.Contain($"SUB\\INNER.TXT {Sha256("inner\n"u8.ToArray())} 6"));
    });
  }

  // ── Helpers ─────────────────────────────────────────────────────────────

  private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

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
