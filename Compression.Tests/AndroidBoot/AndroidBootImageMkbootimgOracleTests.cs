#pragma warning disable CS1591
using System.Diagnostics;
using System.Text;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Compression.Tests.AndroidBoot;

/// <summary>
/// The Android boot image descriptor judged by AOSP's own tools: <c>mkbootimg</c> builds an image,
/// the descriptor reads it, and writing the components it read back out must reproduce
/// <c>mkbootimg</c>'s bytes exactly; <c>unpack_bootimg</c> must extract from our images exactly the
/// components we put in.
/// </summary>
/// <remarks>
/// The tools are found as <c>mkbootimg</c> / <c>unpack_bootimg</c> on the PATH (Debian/Ubuntu package
/// <c>mkbootimg</c>), or through <c>CWB_MKBOOTIMG</c> / <c>CWB_UNPACK_BOOTIMG</c> naming either an
/// executable or the <c>.py</c> script from <c>https://android.googlesource.com/platform/system/tools/mkbootimg</c>.
/// Without them the tests are ignored.
/// </remarks>
[TestFixture]
[Category("ArchiveExternal")]
public sealed class AndroidBootImageMkbootimgOracleTests {

  private string _dir = null!;

  [SetUp]
  public void SetUp() {
    this._dir = Path.Combine(Path.GetTempPath(), "cwb_mkbootimg_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  private static readonly Lazy<string[]?> Mkbootimg = new(() => FindTool("CWB_MKBOOTIMG", "mkbootimg"));
  private static readonly Lazy<string[]?> UnpackBootimg = new(() => FindTool("CWB_UNPACK_BOOTIMG", "unpack_bootimg"));

  /// <summary>Runs a script with its own directory importable, which an embedded Python's <c>._pth</c> would otherwise prevent.</summary>
  internal const string RunScript = "import os, runpy, sys; sys.argv = sys.argv[1:]; sys.path.insert(0, os.path.dirname(os.path.abspath(sys.argv[0]))); runpy.run_path(sys.argv[0], run_name='__main__')";

  private static string[]? FindTool(string variable, string name) {
    var configured = Environment.GetEnvironmentVariable(variable);
    var candidates = new List<string[]>();
    if (!string.IsNullOrWhiteSpace(configured)) {
      if (configured.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) {
        candidates.Add(["python3", "-c", RunScript, configured]);
        candidates.Add(["python", "-c", RunScript, configured]);
      } else candidates.Add([configured]);
    }
    candidates.Add([name]);
    foreach (var c in candidates)
      try {
        if (Run(c, ["--help"]).ExitCode == 0) return c;
      } catch (Win32Exception) {
        // not there
      }
    return null;
  }

  private static (int ExitCode, string StdOut, string StdErr) Run(string[] tool, IEnumerable<string> arguments) {
    var start = new ProcessStartInfo(tool[0]) {
      RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
    };
    foreach (var a in tool.Skip(1).Concat(arguments)) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout.Result, stderr);
  }

  private static string[] Require(Lazy<string[]?> tool, string name) {
    if (tool.Value is null) Assert.Ignore($"{name} not found (apt install mkbootimg, or set CWB_MKBOOTIMG / CWB_UNPACK_BOOTIMG).");
    return tool.Value!;
  }

  private string Save(string name, byte[] data) {
    var path = Path.Combine(this._dir, name);
    File.WriteAllBytes(path, data);
    return path;
  }

  private byte[] RunMkbootimg(IEnumerable<string> arguments, string outputFlag = "--output") {
    var output = Path.Combine(this._dir, "reference.img");
    var (code, _, stderr) = Run(Require(Mkbootimg, "mkbootimg"), arguments.Concat([outputFlag, output]));
    Assert.That(code, Is.Zero, $"mkbootimg failed:\n{stderr}");
    return File.ReadAllBytes(output);
  }

  private static byte[] B(int length, int seed) => AndroidBootImageTests.Bytes(length, seed);

  private static IEnumerable<TestCaseData> BootCases() {
    yield return new TestCaseData(0, 2048, false).SetName("GivenMkbootimgV0_WhenReadAndRewritten_ThenTheBytesAreIdentical");
    yield return new TestCaseData(0, 4096, true).SetName("GivenMkbootimgV0WithSecondAndLongCmdline_WhenReadAndRewritten_ThenTheBytesAreIdentical");
    yield return new TestCaseData(1, 2048, true).SetName("GivenMkbootimgV1WithRecoveryDtbo_WhenReadAndRewritten_ThenTheBytesAreIdentical");
    yield return new TestCaseData(2, 4096, true).SetName("GivenMkbootimgV2WithDtb_WhenReadAndRewritten_ThenTheBytesAreIdentical");
    yield return new TestCaseData(3, 4096, false).SetName("GivenMkbootimgV3_WhenReadAndRewritten_ThenTheBytesAreIdentical");
    yield return new TestCaseData(4, 4096, false).SetName("GivenMkbootimgV4_WhenReadAndRewritten_ThenTheBytesAreIdentical");
  }

  [TestCaseSource(nameof(BootCases))]
  public void MkbootimgBootImage_ReadsAndRewritesIdentically(int version, int page, bool extras) {
    var kernel = B(12345, 1);
    var ramdisk = B(6789, 2);
    var args = new List<string> {
      "--header_version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "--kernel", this.Save("k", kernel), "--ramdisk", this.Save("r", ramdisk),
      "--os_version", "13.0.2", "--os_patch_level", "2024-05",
      "--cmdline", extras ? "console=ttyMSM0,115200n8 " + new string('x', 600) : "console=ttyS0 quiet",
    };
    var expected = new Dictionary<string, byte[]> { ["kernel"] = kernel, ["ramdisk"] = ramdisk };
    if (version < 3) {
      args.AddRange(["--pagesize", page.ToString(System.Globalization.CultureInfo.InvariantCulture), "--base", "0x80000000", "--board", "cwb-board"]);
      if (extras && version < 3) {
        var second = B(999, 3);
        args.AddRange(["--second", this.Save("s", second)]);
        expected["second"] = second;
      }
      if (extras && version >= 1) {
        var dtbo = B(2222, 4);
        args.AddRange(["--recovery_dtbo", this.Save("o", dtbo)]);
        expected["recovery_dtbo"] = dtbo;
      }
      if (version == 2) {
        var dtb = B(3333, 5);
        args.AddRange(["--dtb", this.Save("d", dtb)]);
        expected["dtb"] = dtb;
      }
    }
    var reference = this.RunMkbootimg(args);

    var ours = AndroidBootImageTests.Extract(reference);
    foreach (var (name, data) in expected)
      Assert.That(ours[name], Is.EqualTo(data), $"component '{name}' read differently from what mkbootimg stored");
    Assert.That(ours.Keys.Except(["metadata.ini"]), Is.EquivalentTo(expected.Keys));
    Assert.That(Encoding.UTF8.GetString(ours["metadata.ini"]), Does.Contain("os_version = 13.0.2").And.Contain("os_patch_level = 2024-05"));
    if (version < 3) Assert.That(Encoding.UTF8.GetString(ours["metadata.ini"]), Does.Contain("image_id_valid = true"));

    Assert.That(AndroidBootImageTests.RebuildFromExtraction(reference), Is.EqualTo(reference),
      "the components and header read from mkbootimg's image did not write back to the same bytes");
  }

  [TestCase(3), TestCase(4)]
  public void GivenMkbootimgVendorBoot_WhenReadAndRewritten_ThenTheBytesAreIdentical(int version) {
    var dtb = B(4321, 6);
    var args = new List<string> {
      "--header_version", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
      "--pagesize", "4096", "--base", "0x40000000", "--board", "vendorbrd",
      "--vendor_cmdline", "androidboot.console=ttyS0 loglevel=4", "--dtb", this.Save("d", dtb),
    };
    var expected = new Dictionary<string, byte[]> { ["dtb"] = dtb };
    var platform = B(7000, 7);
    args.AddRange(["--vendor_ramdisk", this.Save("vr", platform)]);
    if (version == 3) expected["vendor_ramdisk"] = platform;
    else {
      var dlkm = B(1234, 8);
      var bootconfig = Encoding.ASCII.GetBytes("androidboot.hardware=cwb\n");
      args.AddRange(["--vendor_bootconfig", this.Save("bc", bootconfig),
        "--ramdisk_type", "dlkm", "--ramdisk_name", "dlkm", "--board_id0", "0xC0FFEE", "--vendor_ramdisk_fragment", this.Save("dl", dlkm)]);
      expected["vendor_ramdisk00"] = platform;
      expected["vendor_ramdisk01"] = dlkm;
      expected["bootconfig"] = bootconfig;
    }
    var reference = this.RunMkbootimg(args, "--vendor_boot");

    var ours = AndroidBootImageTests.Extract(reference);
    foreach (var (name, data) in expected)
      Assert.That(ours[name], Is.EqualTo(data), $"component '{name}' read differently from what mkbootimg stored");
    if (version == 4)
      Assert.That(Encoding.UTF8.GetString(ours["metadata.ini"]), Does.Contain("[vendor_ramdisk01]\ntype = 3\nname = dlkm\nboard_id = 0x00C0FFEE,"));

    Assert.That(AndroidBootImageTests.RebuildFromExtraction(reference), Is.EqualTo(reference));
  }

  [Test]
  public void GivenAnImageBuiltByHand_WhenMkbootimgBuildsTheSame_ThenTheBytesAreIdentical() {
    var kernel = B(20000, 9);
    var ramdisk = B(3000, 10);
    var dtb = B(500, 11);
    var reference = this.RunMkbootimg([
      "--header_version", "2", "--kernel", this.Save("k", kernel), "--ramdisk", this.Save("r", ramdisk), "--dtb", this.Save("d", dtb),
      "--pagesize", "2048", "--cmdline", "root=/dev/ram0", "--board", "hand", "--os_version", "10", "--os_patch_level", "2020-01",
    ]);
    var ours = AndroidBootImageTests.Create(("kernel", kernel), ("ramdisk", ramdisk), ("dtb", dtb), ("metadata.ini", AndroidBootImageTests.Ini(
      "[android_boot]\nheader_version = 2\ncmdline = root=/dev/ram0\nboard = hand\nos_version = 10.0.0\nos_patch_level = 2020-01\n")));
    Assert.That(ours, Is.EqualTo(reference));
  }

  [Test]
  public void GivenAnEditedImage_WhenUnpackBootimgReadsIt_ThenItExtractsTheNewComponents() {
    var unpack = Require(UnpackBootimg, "unpack_bootimg");
    var image = AndroidBootImageTests.Create(("kernel", B(5000, 12)), ("ramdisk", B(800, 13)), ("recovery_dtbo", B(900, 14)),
      ("metadata.ini", AndroidBootImageTests.Ini("[android_boot]\nheader_version = 1\ncmdline = a=b\n")));
    var newRamdisk = B(4100, 15);
    using var ms = new MemoryStream();
    ms.Write(image);
    new FileFormat.AndroidBootImage.AndroidBootImageFormatDescriptor().Add(ms, [Compression.Registry.ArchiveInputInfo.InMemory("ramdisk", newRamdisk)]);

    var path = this.Save("edited.img", ms.ToArray());
    var outDir = Path.Combine(this._dir, "out");
    var (code, stdout, stderr) = Run(unpack, ["--boot_img", path, "--out", outDir]);
    Assert.That(code, Is.Zero, $"unpack_bootimg rejected our image:\n{stderr}");
    Assert.Multiple(() => {
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "kernel")), Is.EqualTo(B(5000, 12)));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "ramdisk")), Is.EqualTo(newRamdisk));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "recovery_dtbo")), Is.EqualTo(B(900, 14)));
      Assert.That(stdout, Does.Contain("command line args: a=b"));
    });
  }
}
