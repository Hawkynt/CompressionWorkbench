#pragma warning disable CA1416 // Platform compatibility — the OCFS2 tools and libguestfs are Linux/WSL-only and guarded at runtime.

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using FileSystem.Ocfs2;

namespace Compression.Tests.Ocfs2;

/// <summary>
/// The third-party proof for the OCFS2 writer and in-place editor: the Linux
/// kernel's own <c>ocfs2</c> driver mounts the volume and reads it back, and
/// <c>fsck.ocfs2 -fn</c> checks it before and after a kernel read-write mount.
/// </summary>
/// <remarks>
/// <para>fsck.ocfs2 alone is not enough — it passed volumes the kernel refused
/// with "Journal file size (65536) is too small". The kernel is reached through a
/// libguestfs appliance (<c>guestfish</c>, direct backend), which boots the
/// distribution's generic kernel with the ocfs2 module; the WSL kernel itself has
/// none. Reads go through <c>tar-out</c>, so names, contents, modes, owners and
/// times are what the kernel reports.</para>
///
/// <para>Every test <see cref="Assert.Ignore(string)"/>s when ocfs2-tools,
/// guestfish or a working appliance is missing — never failing on environment.
/// The sudo password (to make <c>/dev/kvm</c> and <c>/boot/vmlinuz-*</c>
/// readable for the appliance) defaults to the test environment's and can be set
/// with <c>CWB_SUDO_PASSWORD</c>.</para>
/// </remarks>
[TestFixture]
[Category("OsIntegration")]
[Category("ExternalFsInterop")]
[Category("KernelMount")]
[Category("Wsl")]
[Category("Slow")]
public sealed class Ocfs2KernelMountTests {

  private string _tmpDir = null!;

  [SetUp]
  public void SetUp() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_ocfs2k_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void TearDown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  private static readonly DateTimeOffset CreatedAt = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);

  // ── fresh volumes ─────────────────────────────────────────────────────

  /// <summary>
  /// Given the smallest volume the writer makes — no files, a journal of exactly
  /// the kernel's 4 MiB minimum — when the kernel mounts it read-write and writes
  /// a file and a directory, then the mount succeeds, fsck.ocfs2 is clean before
  /// and after, and this package's reader sees what the kernel wrote.
  /// </summary>
  [Test]
  public void EmptyVolume_KernelMountsReadWrite_FsckCleanBeforeAndAfter() {
    RequireKernel();
    var img = this.Save("empty.ocfs2", new Ocfs2Writer().Build());

    var view = this.Mount(img, readWrite: ["write /kernel.txt \"written-by-kernel\"", "mkdir /kdir"]);

    view.AssertClean();
    Assert.That(view.Entries.Keys, Is.EquivalentTo(new[] { "lost+found" }), "a fresh volume holds only lost+found");
    var after = Ocfs2Reader.ReadFiles(File.ReadAllBytes(img)).ToDictionary(f => f.Name, f => Encoding.ASCII.GetString(f.Data));
    Assert.That(after["kernel.txt"], Is.EqualTo("written-by-kernel"), "our reader reads the file the kernel wrote");
  }

  /// <summary>
  /// Given a tree with nested directories and files on both sides of the inline
  /// boundary (0, 3896 and 3897 bytes) plus multi-cluster files, when the kernel
  /// mounts it, then every name and byte reads back identical, files are 0644 and
  /// directories 0755, owned by root, stamped with the creation time — and
  /// fsck.ocfs2 stays clean after a read-write mount.
  /// </summary>
  [Test]
  public void Tree_ReadsBackByteIdentical_WithModesOwnersAndTimes() {
    RequireKernel();
    var expected = new Dictionary<string, byte[]> {
      ["readme.txt"] = "ocfs2 kernel mount\n"u8.ToArray(),
      ["empty.txt"] = [],
      ["inline-max.bin"] = Ocfs2LayoutTests.Pattern(Ocfs2Writer.MaxInline),
      ["inline-max-plus-one.bin"] = Ocfs2LayoutTests.Pattern(Ocfs2Writer.MaxInline + 1, 2),
      ["docs/guide.txt"] = "guide\n"u8.ToArray(),
      ["docs/api/reference.bin"] = Ocfs2LayoutTests.Pattern(100_000, 3),
    };
    var w = new Ocfs2Writer();
    w.SetIdentity(Guid.NewGuid().ToByteArray(), 0x12345678, CreatedAt);
    foreach (var (name, data) in expected) w.AddFile(name, data);
    var img = this.Save("tree.ocfs2", w.Build());

    var view = this.Mount(img, readWrite: ["write /k.txt \"k\""]);

    view.AssertClean();
    AssertFiles(view, expected);
    Assert.Multiple(() => {
      foreach (var (path, e) in view.Entries) {
        if (path == "lost+found") continue;
        Assert.That(e.Mode, Is.EqualTo(e.IsDir ? 0x1ED : 0x1A4), $"{path}: mode");
        Assert.That((e.Uid, e.Gid), Is.EqualTo((0, 0)), $"{path}: owner");
        Assert.That(e.MTime, Is.EqualTo(CreatedAt), $"{path}: mtime");
      }
    });
  }

  /// <summary>
  /// Given more files than one inode group holds and a file larger than one
  /// cluster group (so its data is two extent records around a group
  /// descriptor), when the kernel mounts the volume, then it lists every file and
  /// the large file's SHA-256 matches.
  /// </summary>
  [Test]
  public void ManyFilesAndAFileLargerThanAClusterGroup_KernelReadsThemAll() {
    RequireKernel();
    const long bigSize = (Ocfs2Writer.ClustersPerGroup + 100L) * 4096;
    var w = new Ocfs2Writer();
    for (var i = 0; i < 1100; ++i) w.AddFile($"many/f{i:D4}.txt", Encoding.ASCII.GetBytes($"file {i}\n"));
    var big = BigPattern(bigSize);
    w.AddFile("big.bin", big);
    var img = Path.Combine(this._tmpDir, "boundary.ocfs2");
    using (var fs = File.Create(img)) w.WriteTo(fs);

    var fsck = Fsck(img);
    Assert.That(fsck.ExitCode, Is.Zero, $"fsck.ocfs2 -fn:\n{fsck.Output}");
    var gf = this.Guestfish(img, ["mount-vfs \"\" ocfs2 /dev/sda /", "checksum sha256 /big.bin", "ls /many", "umount /"]);
    Assert.That(gf.ExitCode, Is.Zero, gf.Output);
    var lines = gf.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Assert.That(lines[0], Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(big))), "big.bin through the kernel");
    Assert.That(lines.Count(l => l.StartsWith('f')), Is.EqualTo(1100), "every file in /many through the kernel");
  }

  // ── in-place edits ────────────────────────────────────────────────────

  /// <summary>
  /// Given a roomy volume from this package's writer, when files are added
  /// (inline and extent-backed), replaced, removed and the volume defragmented in
  /// place, then the kernel reads exactly the edited tree and fsck.ocfs2 is clean
  /// before and after a kernel read-write mount.
  /// </summary>
  [Test]
  public void InPlaceEdits_OnOurVolume_KernelReadsTheEditedTree() {
    RequireKernel();
    var w = new Ocfs2Writer();
    w.SetMinimumSize(32L * 1024 * 1024);
    w.AddFile("first.bin", Ocfs2LayoutTests.Pattern(50_000));
    w.AddFile("readme.txt", "original"u8.ToArray());
    w.AddFile("docs/guide.txt", "guide"u8.ToArray());
    var img = this.Save("edited.ocfs2", w.Build());

    var expected = new Dictionary<string, byte[]> {
      ["readme.txt"] = "replaced in place"u8.ToArray(),
      ["docs/guide.txt"] = "guide"u8.ToArray(),
      ["small.txt"] = "added inline"u8.ToArray(),
      ["large.bin"] = Ocfs2LayoutTests.Pattern(70_000, 5),
    };
    using (var s = File.Open(img, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new Ocfs2FormatDescriptor();
      d.Add(s, [
        ArchiveInputInfo.InMemory("small.txt", expected["small.txt"]),
        ArchiveInputInfo.InMemory("large.bin", expected["large.bin"]),
        ArchiveInputInfo.InMemory("readme.txt", expected["readme.txt"]),
      ]);
      d.Remove(s, ["first.bin"]);
      d.Defragment(s);
    }

    var view = this.Mount(img, readWrite: ["write /k.txt \"k\""]);

    view.AssertClean();
    AssertFiles(view, expected);
  }

  /// <summary>
  /// Given a volume made by <c>mkfs.ocfs2 -M local</c> and filled through the
  /// kernel, when files are added, replaced and removed in place and the volume
  /// is defragmented, then fsck.ocfs2 is clean, the kernel reads the edited tree,
  /// and everything the kernel wrote that was not edited is unchanged.
  /// </summary>
  [Test]
  public void InPlaceEdits_OnAnMkfsVolumeFilledByTheKernel_KernelReadsTheEditedTree() {
    RequireKernel();
    var img = Path.Combine(this._tmpDir, "mkfs.ocfs2");
    var wsl = Unquoted(img);
    var mk = FsInteropToolbox.RunWsl(
      $"truncate -s 64M {wsl} && mkfs.ocfs2 -q -M local -b 4096 -C 4096 -N 1 -L cwbmkfs --force {wsl} 2>&1");
    Assert.That(mk.ExitCode, Is.Zero, $"mkfs.ocfs2: {mk.StdOut}{mk.StdErr}");
    var fill = this.Guestfish(img, [
      "mount-vfs \"\" ocfs2 /dev/sda /",
      "write /kernel.txt \"kernel wrote this\"",
      "fallocate64 /kernel.bin 200000",
      "mkdir /kdir",
      "write /kdir/nested.txt \"nested by kernel\"",
      "umount /",
    ]);
    Assert.That(fill.ExitCode, Is.Zero, fill.Output);

    var expected = new Dictionary<string, byte[]> {
      ["kernel.txt"] = "replaced by cwb"u8.ToArray(),
      ["kdir/nested.txt"] = "nested by kernel"u8.ToArray(),
      ["cwb-small.txt"] = "from cwb"u8.ToArray(),
      ["cwb-large.bin"] = Ocfs2LayoutTests.Pattern(123_456, 9),
    };
    using (var s = File.Open(img, FileMode.Open, FileAccess.ReadWrite)) {
      var d = new Ocfs2FormatDescriptor();
      d.Add(s, [
        ArchiveInputInfo.InMemory("cwb-small.txt", expected["cwb-small.txt"]),
        ArchiveInputInfo.InMemory("cwb-large.bin", expected["cwb-large.bin"]),
        ArchiveInputInfo.InMemory("kernel.txt", expected["kernel.txt"]),
      ]);
      d.Remove(s, ["kernel.bin"]);
      d.Defragment(s);
    }

    var view = this.Mount(img, readWrite: ["write /k.txt \"k\""]);

    view.AssertClean();
    AssertFiles(view, expected);
  }

  // ── harness ───────────────────────────────────────────────────────────

  private sealed record Entry(bool IsDir, int Mode, int Uid, int Gid, DateTimeOffset MTime, byte[] Data);

  private sealed record KernelView(
      (int ExitCode, string Output) FsckBefore,
      (int ExitCode, string Output) Guestfish,
      (int ExitCode, string Output)? FsckAfter,
      Dictionary<string, Entry> Entries) {
    public void AssertClean() {
      Assert.That(this.FsckBefore.ExitCode, Is.Zero, $"fsck.ocfs2 -fn before mounting:\n{this.FsckBefore.Output}");
      Assert.That(this.FsckBefore.Output, Does.Contain("All passes succeeded"));
      Assert.That(this.Guestfish.ExitCode, Is.Zero, $"kernel mount through guestfish:\n{this.Guestfish.Output}");
      if (this.FsckAfter is { } after) {
        Assert.That(after.ExitCode, Is.Zero, $"fsck.ocfs2 -fn after the kernel read-write mount:\n{after.Output}");
        Assert.That(after.Output, Does.Contain("All passes succeeded"));
      }
    }
  }

  private static void AssertFiles(KernelView view, Dictionary<string, byte[]> expected) {
    var files = view.Entries.Where(e => !e.Value.IsDir).ToDictionary(e => e.Key, e => e.Value.Data);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys), "the files the kernel lists");
    foreach (var (name, data) in expected)
      Assert.That(files[name], Is.EqualTo(data), $"{name} through the kernel");
  }

  private string Save(string name, byte[] image) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, image);
    return path;
  }

  /// <summary>
  /// fsck.ocfs2 -fn, then a kernel mount that tars the tree out (and optionally
  /// runs read-write commands), then — after a read-write mount — fsck again.
  /// The tar is read before the read-write commands, so it shows the volume as
  /// it was handed over.
  /// </summary>
  private KernelView Mount(string img, string[]? readWrite = null) {
    var before = Fsck(img);
    var tar = Path.Combine(this._tmpDir, Path.GetFileName(img) + ".tar");
    var commands = new List<string> { "mount-vfs \"\" ocfs2 /dev/sda /", $"tar-out / {Unquoted(tar)} numericowner:true" };
    if (readWrite != null) commands.AddRange(readWrite);
    commands.Add("umount /");
    var gf = this.Guestfish(img, [.. commands]);
    var after = readWrite != null ? Fsck(img) : ((int, string)?)null;
    return new KernelView(before, gf, after, File.Exists(tar) ? ReadTar(tar) : []);
  }

  private static Dictionary<string, Entry> ReadTar(string path) {
    var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
    using var stream = File.OpenRead(path);
    using var reader = new TarReader(stream);
    while (reader.GetNextEntry() is { } e) {
      var name = (e.Name.StartsWith("./", StringComparison.Ordinal) ? e.Name[2..] : e.Name).Trim('/');
      if (name.Length == 0) continue;
      if (name == "lost+found" || name.StartsWith("lost+found/", StringComparison.Ordinal)) {
        entries[name] = new Entry(true, (int)e.Mode, e.Uid, e.Gid, e.ModificationTime, []);
        continue;
      }
      var data = Array.Empty<byte>();
      if (e.DataStream != null) {
        using var ms = new MemoryStream();
        e.DataStream.CopyTo(ms);
        data = ms.ToArray();
      }
      entries[name] = new Entry(e.EntryType == TarEntryType.Directory, (int)e.Mode, e.Uid, e.Gid, e.ModificationTime, data);
    }
    return entries;
  }

  private static (int ExitCode, string Output) Fsck(string img) {
    var r = FsInteropToolbox.RunWsl($"fsck.ocfs2 -fn {Unquoted(img)} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  private (int ExitCode, string Output) Guestfish(string img, string[] commands) {
    var script = Path.Combine(this._tmpDir, $"gf_{Guid.NewGuid():N}.txt");
    File.WriteAllText(script, string.Join('\n', ["run", .. commands]) + "\n");
    var r = FsInteropToolbox.RunWsl($"{ApplianceEnv} timeout 600 guestfish -a {Unquoted(img)} -f {Unquoted(script)} 2>&1");
    return (r.ExitCode, r.StdOut + r.StdErr);
  }

  /// <summary>A path as the Linux side sees it, without the quotes <c>WinToWsl</c> adds (test paths hold no spaces).</summary>
  private static string Unquoted(string path) => FsInteropToolbox.WinToWsl(path).Trim('\'');

  private static string SudoPassword => Environment.GetEnvironmentVariable("CWB_SUDO_PASSWORD") ?? "1234";

  /// <summary>
  /// The appliance reads the host's vmlinuz (root-only by default) and wants
  /// /dev/kvm; both chmods are no-ops once applied.
  /// </summary>
  private static string ApplianceEnv =>
    "export LIBGUESTFS_BACKEND=direct; " +
    $"echo {SudoPassword} | sudo -S chmod 0666 /dev/kvm 2>/dev/null; " +
    $"echo {SudoPassword} | sudo -S chmod 0644 /boot/vmlinuz-* 2>/dev/null;";

  private static bool? _kernelCapable;

  /// <summary>
  /// Skips unless ocfs2-tools and a guestfish appliance that can mount a real
  /// mkfs.ocfs2 volume are reachable. Probed once per run.
  /// </summary>
  private static void RequireKernel() {
    foreach (var tool in new[] { "mkfs.ocfs2", "fsck.ocfs2", "guestfish" })
      if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool(tool))
        Assert.Ignore($"'{tool}' not available (install ocfs2-tools and libguestfs-tools).");

    _kernelCapable ??= ProbeKernel();
    if (_kernelCapable != true)
      Assert.Ignore("the libguestfs appliance cannot mount an mkfs.ocfs2 volume here (no ocfs2 module, no kernel image, or no KVM).");
  }

  private static bool ProbeKernel() {
    var r = FsInteropToolbox.RunWsl(
      ApplianceEnv + " d=$(mktemp -d); truncate -s 32M $d/p.img; " +
      "mkfs.ocfs2 -q -M local -b 4096 -C 4096 -N 1 --force $d/p.img >/dev/null 2>&1; " +
      "printf 'run\\nmount-vfs \"\" ocfs2 /dev/sda /\\nls /\\numount /\\n' | timeout 600 guestfish -a $d/p.img 2>&1; rm -rf $d");
    return r.StdOut.Contains("lost+found", StringComparison.Ordinal);
  }

  private static byte[] BigPattern(long size) {
    var data = new byte[size];
    for (long p = 0; p < size; ++p) data[p] = (byte)(p * 31 + 1 + (p >> 12));
    return data;
  }
}
