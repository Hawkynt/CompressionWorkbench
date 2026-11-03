using System.Text;
using Compression.Registry;
using FileSystem.Svr4Bfs;

namespace Compression.Tests.Svr4Bfs;

/// <summary>
/// Third-party proof for the SVR4 BFS writer and in-place editor: the Linux <c>bfs</c>
/// driver, reached through a libguestfs appliance (the WSL kernel has no bfs module),
/// mounts what we write and every file it reads back must match byte for byte; it then
/// writes into our volume and our reader must read what it wrote. The reverse direction
/// — <c>mkfs.bfs</c> formatting, our editor adding — is covered too.
/// </summary>
/// <remarks>
/// Every test <see cref="Assert.Ignore(string)"/>s when util-linux's <c>mkfs.bfs</c>,
/// <c>guestfish</c> or an appliance kernel with the bfs module is missing. The sudo
/// password (to make <c>/dev/kvm</c> and <c>/boot/vmlinuz-*</c> readable for the
/// appliance) defaults to the test environment's and can be set with
/// <c>CWB_SUDO_PASSWORD</c>.
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
[Category("Slow")]
public sealed class Svr4BfsKernelMountTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_svr4bfs_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static byte[] Pattern(int size, int seed = 1) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 31 + seed + (i >> 9));
    return data;
  }

  [Test]
  public void GivenOurVolume_WhenKernelMountsIt_ThenEveryFileReadsBackByteExact_AndKernelWritesWeRead() {
    RequireKernel();
    var expected = new Dictionary<string, byte[]> {
      ["unix"] = Pattern(70_000),
      ["empty"] = [],
      ["one-block"] = Pattern(512, 2),
      ["abcdefghijklmn"] = "fourteen-byte name\n"u8.ToArray(),
      ["boot"] = Pattern(513, 3),
    };
    var d = new Svr4BfsFormatDescriptor();
    var img = Path.Combine(this._tmpDir, "ours.bfs");
    using (var fs = File.Create(img))
      d.Create(fs, expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList(), new FormatCreateOptions());

    var got = this.KernelRead(img, expected.Keys, writes: ["write /kernel.txt \"written-by-kernel\""]);

    AssertSame(got, expected);
    using var s = File.OpenRead(img);
    var r = new Svr4BfsReader(s);
    var k = r.Entries.Single(e => e.Name == "kernel.txt");
    Assert.That(Encoding.ASCII.GetString(r.Extract(k)), Is.EqualTo("written-by-kernel"), "our reader reads the file the kernel wrote");
    foreach (var (name, data) in expected)
      Assert.That(r.Extract(r.Entries.Single(e => e.Name == name)), Is.EqualTo(data), $"{name} after the kernel's read-write mount");
  }

  [Test]
  public void GivenOurInPlaceEdits_WhenKernelMountsTheVolume_ThenItSeesExactlyTheEditedSet() {
    RequireKernel();
    var d = new Svr4BfsFormatDescriptor();
    var img = Path.Combine(this._tmpDir, "edited.bfs");
    using (var fs = File.Create(img))
      d.Create(fs, [
        ArchiveInputInfo.InMemory("a", Pattern(3000, 4)),
        ArchiveInputInfo.InMemory("b", Pattern(700, 5)),
        ArchiveInputInfo.InMemory("c", "c"u8.ToArray()),
      ], new FormatCreateOptions());
    using (var fs = File.Open(img, FileMode.Open, FileAccess.ReadWrite)) {
      d.Remove(fs, ["a"]);
      d.Add(fs, [ArchiveInputInfo.InMemory("small", Pattern(600, 6)), ArchiveInputInfo.InMemory("b", Pattern(4000, 7))]);
    }
    var expected = new Dictionary<string, byte[]> {
      ["small"] = Pattern(600, 6), // first fit: into the hole "a" left
      ["b"] = Pattern(4000, 7),
      ["c"] = "c"u8.ToArray(),
    };

    AssertSame(this.KernelRead(img, expected.Keys), expected);
  }

  [Test]
  public void GivenMkfsBfsVolume_WhenWeAddFiles_ThenKernelReadsThem() {
    RequireKernel();
    var img = Path.Combine(this._tmpDir, "mkfs.bfs");
    var mk = FsInteropToolbox.RunWsl($"truncate -s 256K {Unquoted(img)} && mkfs.bfs -V mkvol {Unquoted(img)} 2>&1");
    Assert.That(mk.ExitCode, Is.Zero, mk.StdOut + mk.StdErr);
    var expected = new Dictionary<string, byte[]> { ["first"] = Pattern(10_000, 8), ["second"] = [] };
    using (var fs = File.Open(img, FileMode.Open, FileAccess.ReadWrite))
      new Svr4BfsFormatDescriptor().Add(fs, expected.Select(kv => ArchiveInputInfo.InMemory(kv.Key, kv.Value)).ToList());

    AssertSame(this.KernelRead(img, expected.Keys), expected);
  }

  private static void AssertSame(Dictionary<string, byte[]> got, Dictionary<string, byte[]> expected) {
    Assert.That(got.Keys, Is.EquivalentTo(expected.Keys), "the names the kernel lists");
    foreach (var (name, data) in expected)
      Assert.That(got[name], Is.EqualTo(data), $"{name} through the kernel");
  }

  /// <summary>
  /// Mounts <paramref name="img"/> with the kernel bfs driver, lists the root, downloads
  /// <paramref name="names"/>, then runs <paramref name="writes"/> on the read-write mount.
  /// </summary>
  private Dictionary<string, byte[]> KernelRead(string img, IEnumerable<string> names, string[]? writes = null) {
    var outDir = Path.Combine(this._tmpDir, $"out_{Guid.NewGuid():N}");
    Directory.CreateDirectory(outDir);
    var commands = new List<string> { "mount-vfs \"\" bfs /dev/sda /", "ls /" };
    foreach (var n in names) commands.Add($"download /{n} {Unquoted(Path.Combine(outDir, n))}");
    commands.AddRange(writes ?? []);
    commands.Add("umount /");

    var script = Path.Combine(this._tmpDir, $"gf_{Guid.NewGuid():N}.txt");
    File.WriteAllText(script, string.Join('\n', ["run", .. commands]) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {Unquoted(img)} -f {Unquoted(script)} 2>&1");
    Assert.That(r.ExitCode, Is.Zero, $"kernel mount through guestfish:\n{r.StdOut}{r.StdErr}");

    var listed = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return listed.Where(n => File.Exists(Path.Combine(outDir, n)))
      .ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(outDir, n)));
  }

  private static string Unquoted(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static bool? _kernelCapable;

  private static void RequireKernel() {
    foreach (var tool in new[] { "mkfs.bfs", "guestfish" })
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' not available (install util-linux and libguestfs-tools).");
    _kernelCapable ??= ProbeKernel();
    if (_kernelCapable != true)
      Assert.Ignore("the libguestfs appliance cannot mount an mkfs.bfs volume here (no bfs module, no kernel image, or no KVM).");
  }

  private static bool ProbeKernel() {
    var r = FsInteropToolbox.RunWsl(
      ApplianceEnv + " d=$(mktemp -d); truncate -s 64K $d/p.img; mkfs.bfs $d/p.img >/dev/null 2>&1; " +
      "printf 'run\\nmount-vfs \"\" bfs /dev/sda /\\nwrite /probe ok\\nls /\\numount /\\n' | timeout 600 guestfish -a $d/p.img 2>&1; rm -rf $d");
    return r.StdOut.Contains("probe", StringComparison.Ordinal);
  }
}
