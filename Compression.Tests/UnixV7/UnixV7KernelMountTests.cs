#pragma warning disable CS1591
using System.Security.Cryptography;
using System.Text;
using FileSystem.UnixV7;

namespace Compression.Tests.UnixV7;

/// <summary>
/// Third-party proof for the V7 reader and writer: the Linux kernel's <c>sysv</c>
/// driver, file-system type <c>v7</c>, reached through the libguestfs appliance
/// (the WSL kernel has no sysv module). The kernel reads what the writer made, and
/// writes files into it that the reader then has to find.
/// </summary>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
[Category("Slow")]
public sealed class UnixV7KernelMountTests {
  private string _tmp = null!;
  private static bool? _kernelCapable;

  [SetUp]
  public void SetUp() {
    _tmp = Path.Combine(Path.GetTempPath(), $"cwb_v7k_{Guid.NewGuid():N}");
    Directory.CreateDirectory(_tmp);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(_tmp, true); } catch { /* best effort */ }
  }

  private static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 13 + seed + (i >> 10));
    return data;
  }

  private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

  [Test]
  public void GivenAVolumeTheWriterMade_WhenTheKernelMountsIt_ThenEveryFileReadsBackByteExact() {
    RequireKernel();
    var files = new Dictionary<string, byte[]> {
      ["hello"] = "hello, v7\n"u8.ToArray(),
      ["empty"] = [],
      ["usr/lib/direct"] = Pattern(5120, 1),
      ["usr/lib/single"] = Pattern(5121, 2),
      ["usr/lib/double"] = Pattern(200_000, 3),
      ["abcdefghijklmn"] = Pattern(17, 4),
    };
    var w = new UnixV7Writer();
    foreach (var (name, data) in files) w.AddFile(name, data);
    w.AddDirectory("tmp");
    var img = Path.Combine(_tmp, "w.v7");
    File.WriteAllBytes(img, w.Build());

    var commands = new List<string> { "mount-vfs ro v7 /dev/sda /", "is-dir /tmp" };
    commands.AddRange(files.Keys.Select(k => $"checksum sha256 /{k}"));
    commands.Add("umount /");
    var (exit, output) = Guestfish(img, commands);
    Assert.That(exit, Is.Zero, output);
    var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    Assert.That(lines[0], Is.EqualTo("true"), output);
    Assert.That(lines.Skip(1).Take(files.Count), Is.EqualTo(files.Values.Select(Sha)), output);
  }

  [Test]
  public void GivenAVolumeTheWriterMade_WhenTheKernelWritesIntoIt_ThenTheReaderSeesTheKernelsFiles() {
    RequireKernel();
    var w = new UnixV7Writer();
    // The seed is the room the kernel works in: removing it returns its blocks to the
    // chained free list, from which the kernel then allocates.
    w.AddFile("seed", Pattern(200_000, 8));
    var img = Path.Combine(_tmp, "k.v7");
    File.WriteAllBytes(img, w.Build());
    var payload = Path.Combine(_tmp, "payload.bin");
    var bytes = Pattern(150_000, 9);
    File.WriteAllBytes(payload, bytes);

    var (exit, output) = Guestfish(img, [
      "mount-vfs rw v7 /dev/sda /",
      "rm /seed",
      "mkdir /kdir",
      "write /kdir/note \"written by the kernel\"",
      $"upload {Unquoted(payload)} /kdir/payload",
      "umount /",
    ]);
    Assert.That(exit, Is.Zero, output);

    using var r = new UnixV7Reader(File.OpenRead(img), leaveOpen: false);
    var byName = r.Entries.ToDictionary(e => e.Name);
    Assert.That(byName.ContainsKey("seed"), Is.False, "the kernel removed it");
    Assert.That(Encoding.ASCII.GetString(r.Extract(byName["kdir/note"])), Is.EqualTo("written by the kernel"));
    Assert.That(r.Extract(byName["kdir/payload"]), Is.EqualTo(bytes));
  }

  /// <summary>
  /// A real V7 root volume: <c>unix_v7_rl.dsk</c> from the SIMH software kit
  /// <c>uv7swre.zip</c> (RL02, 1979 distribution, Caldera Ancient UNIX licence), with
  /// its manifest from the kernel's own copy-out. Not checked in: set
  /// <c>CWB_UNIXV7_IMAGE</c> and <c>CWB_UNIXV7_MANIFEST</c> to run it.
  /// </summary>
  [Test]
  public void GivenTheSimhV7Distribution_WhenRead_ThenEveryFileMatchesTheKernelCopy() {
    var imagePath = Environment.GetEnvironmentVariable("CWB_UNIXV7_IMAGE");
    var manifestPath = Environment.GetEnvironmentVariable("CWB_UNIXV7_MANIFEST");
    if (imagePath is null || manifestPath is null || !File.Exists(imagePath) || !File.Exists(manifestPath))
      Assert.Ignore("set CWB_UNIXV7_IMAGE and CWB_UNIXV7_MANIFEST to the SIMH unix_v7_rl.dsk and its kernel manifest");

    using var r = new UnixV7Reader(File.OpenRead(imagePath), leaveOpen: false);
    var byName = r.Entries.ToDictionary(e => e.Name, StringComparer.Ordinal);
    var mismatches = new List<string>();
    var files = 0;
    foreach (var line in File.ReadLines(manifestPath)) {
      if (line.StartsWith("D ", StringComparison.Ordinal)) {
        if (!byName.TryGetValue(line[2..], out var d) || !d.IsDirectory) mismatches.Add("dir " + line[2..]);
        continue;
      }
      var name = line[68..];
      if (!byName.TryGetValue(name, out var e)) { mismatches.Add("missing " + name); continue; }
      if (Sha(r.Extract(e)) != line[..64]) mismatches.Add("content " + name);
      ++files;
    }
    Assert.That(mismatches, Is.Empty);
    Assert.That(files, Is.GreaterThan(1000));
  }

  private (int ExitCode, string Output) Guestfish(string img, IEnumerable<string> commands) {
    var script = Path.Combine(_tmp, $"gf_{Guid.NewGuid():N}.txt");
    File.WriteAllText(script, string.Join('\n', ["run", .. commands]) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {Unquoted(img)} -f {Unquoted(script)} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private static string Unquoted(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static void RequireKernel() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("guestfish"))
      Assert.Ignore("guestfish not available (install libguestfs-tools).");
    _kernelCapable ??= Probe();
    if (_kernelCapable != true)
      Assert.Ignore("the libguestfs appliance cannot mount a v7 volume here (no sysv module, no kernel image, or no KVM).");
  }

  // Probes the appliance's kernel for the sysv module only — never with a volume
  // this package wrote, so a broken writer fails the tests instead of skipping them.
  private static bool Probe() {
    var r = FsInteropToolbox.RunWsl(
      $"{ApplianceEnv} printf 'debug sh \"modprobe sysv && grep -w v7 /proc/filesystems\"\\n' | (cd /tmp && timeout 600 guestfish -N cwb_v7probe.img=disk:1M; rm -f cwb_v7probe.img) 2>&1");
    return r.StdOut.Contains("v7", StringComparison.Ordinal);
  }
}
