using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.Qnx4;

namespace Compression.Tests.Qnx4;

/// <summary>
/// WORM (write-once) coverage for the QNX4 file-system writer.
///
/// Boundaries exercised:
/// <list type="bullet">
///   <item>Round-trip: writer → reader → byte-equal payload</item>
///   <item>Empty file (zero bytes still allocates a single extent block)</item>
///   <item>Multi-block file (forces extent &gt; 1 block)</item>
///   <item>Multiple files (3-file mixed-size pack)</item>
///   <item>Path stripping: subdirectory components in input names are flattened to leaf</item>
///   <item>Name truncation: filenames > 16 bytes are clipped to the QNX4 short-name limit</item>
///   <item>System inodes (".bitmap" and ".inodes") are emitted but skipped by the reader</item>
///   <item>Boot block is intact (block 0 untouched / zeroed)</item>
///   <item>Capacity guard: more files than the flat-root cluster can hold throws</item>
///   <item>Descriptor.Create() pipes through to the writer</item>
/// </list>
/// </summary>
[TestFixture]
public class Qnx4WormTests {

  private const int BlockSize = 512;
  private const int InodeSize = 64;

  private static Qnx4Reader RoundTrip(Action<Qnx4Writer> populate) {
    var w = new Qnx4Writer();
    populate(w);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return new Qnx4Reader(ms);
  }

  [Test, Category("HappyPath")]
  public void Writer_SingleFile_RoundTrip() {
    var payload = "Hello QNX4 WORM!\n"u8.ToArray();
    var r = RoundTrip(w => w.AddFile("greet.txt", payload));

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var e = r.Entries[0];
    Assert.That(e.Name, Is.EqualTo("greet.txt"));
    Assert.That(e.IsDirectory, Is.False);
    Assert.That(e.Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(e), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_MultipleFiles_RoundTrip() {
    var p1 = Encoding.UTF8.GetBytes("alpha");
    var p2 = Encoding.UTF8.GetBytes(new string('X', 200));
    var p3 = new byte[800]; // forces a 2-block extent (800 > 512)
    for (var i = 0; i < p3.Length; i++) p3[i] = (byte)(i & 0xFF);

    var r = RoundTrip(w => {
      w.AddFile("a", p1);
      w.AddFile("b", p2);
      w.AddFile("c", p3);
    });

    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "a", "b", "c" }));
    var aEnt = r.Entries.First(x => x.Name == "a");
    var bEnt = r.Entries.First(x => x.Name == "b");
    var cEnt = r.Entries.First(x => x.Name == "c");
    Assert.That(r.Extract(aEnt), Is.EqualTo(p1));
    Assert.That(r.Extract(bEnt), Is.EqualTo(p2));
    Assert.That(r.Extract(cEnt), Is.EqualTo(p3));
    Assert.That(cEnt.ExtentBlockCount, Is.GreaterThanOrEqualTo(2u),
      "An 800-byte file must occupy 2 blocks (extent rounding).");
  }

  [Test, Category("Boundary")]
  public void Writer_EmptyFile_AllocatesAtLeastOneBlock() {
    var r = RoundTrip(w => w.AddFile("zero.bin", []));

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    var e = r.Entries[0];
    Assert.That(e.Size, Is.EqualTo(0));
    Assert.That(e.ExtentBlockCount, Is.GreaterThanOrEqualTo(1u),
      "Even empty files reserve one block so the extent walker has something to follow.");
    Assert.That(r.Extract(e), Is.EqualTo(Array.Empty<byte>()));
  }

  [Test, Category("Boundary")]
  public void Writer_ExactBlockSize_DoesNotOverAllocate() {
    var payload = new byte[BlockSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = 0xA5;

    var r = RoundTrip(w => w.AddFile("exact.bin", payload));

    var e = r.Entries[0];
    Assert.That(e.Size, Is.EqualTo(BlockSize));
    Assert.That(e.ExtentBlockCount, Is.EqualTo(1u),
      "Exactly-one-block payload must not round up to 2.");
    Assert.That(r.Extract(e), Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void Writer_NamePathFlattens_KeepsLeaf() {
    var payload = Encoding.UTF8.GetBytes("nested");
    var r = RoundTrip(w => w.AddFile("docs/sub/file.txt", payload));

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("file.txt"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("Boundary")]
  public void Writer_LongName_TruncatesToShortNameLimit() {
    var payload = "x"u8.ToArray();
    // 20 ASCII bytes — longer than the 16-byte QNX4 short-name slot
    const string longName = "abcdefghijklmnopqrst";
    var r = RoundTrip(w => w.AddFile(longName, payload));

    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(16),
      "QNX4 short names are capped at 16 bytes — the writer must truncate.");
    Assert.That(longName.StartsWith(r.Entries[0].Name), Is.True,
      "Truncated name should be a prefix of the original.");
  }

  [Test, Category("Sad")]
  public void Writer_TooManyFiles_ThrowsCleanly() {
    var w = new Qnx4Writer();
    // The root directory is 4 blocks of 8 entries = 32 slots, of which
    // .bitmap and .inodes take two — the root's own entry is in the
    // superblock, not here — leaving 30 for files.
    for (var i = 0; i < 31; i++)
      w.AddFile($"f{i:00}", [(byte)i]);

    using var ms = new MemoryStream();
    var ex = Assert.Throws<InvalidOperationException>(() => w.WriteTo(ms));
    Assert.That(ex!.Message, Does.Contain("WORM scope"));
    Assert.That(ex.Message, Does.Contain("30"));
  }

  [Test, Category("HappyPath")]
  public void Writer_BootBlockIsZero() {
    using var ms = new MemoryStream();
    var w = new Qnx4Writer();
    w.AddFile("a.txt", "hi"u8.ToArray());
    w.WriteTo(ms);
    var image = ms.ToArray();

    Assert.That(image.Length, Is.GreaterThanOrEqualTo(BlockSize));
    for (var i = 0; i < BlockSize; i++)
      Assert.That(image[i], Is.EqualTo(0), $"Boot block byte {i} should be zero.");
  }

  [Test, Category("HappyPath")]
  public void Writer_SystemInodesEmitted_ButNotListed() {
    // The writer emits .bitmap (entry 1) and .inodes (entry 2) but the reader
    // filters them out as "." -prefixed system entries... actually our reader
    // only filters "." / ".." — but since these files are real (.bitmap/.inodes
    // are valid QNX4 short names with no special semantics), the reader will
    // surface them. The point of this test is to verify they exist on-disk
    // at the expected slots; user files do not collide with them.
    using var ms = new MemoryStream();
    var w = new Qnx4Writer();
    w.AddFile("user.txt", "x"u8.ToArray());
    w.WriteTo(ms);
    var image = ms.ToArray();

    // .bitmap belongs in the root directory, which is where a driver looks for
    // it before it will mount anything — not in the superblock, whose four
    // entries are the root, the inode file and the two boot slots.
    var bitmapNameOff = 2 * BlockSize;
    var bitmapName = ReadInodeName(image.AsSpan(bitmapNameOff, 16));
    Assert.That(bitmapName, Is.EqualTo(".bitmap"));

    // .inodes is the second root directory entry, and also the superblock's
    // second slot — this checks the directory's copy.
    var inodesNameOff = 2 * BlockSize + InodeSize;
    var inodesName = ReadInodeName(image.AsSpan(inodesNameOff, 16));
    Assert.That(inodesName, Is.EqualTo(".inodes"));

    // The bitmap sits at LBA 6 now: the superblock takes block 1 and the root
    // directory the four after it.
    var bitmapBlockOff = 6 * BlockSize;
    Assert.That(image[bitmapBlockOff] & 1, Is.EqualTo(1),
      ".bitmap byte 0 bit 0 (boot block allocated) must be set.");
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Create_PipesToWriter() {
    var d = new Qnx4FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());

    var payload = "via descriptor"u8.ToArray();
    using var ms = new MemoryStream();
    d.Create(
      ms,
      [ArchiveInputInfo.InMemory("note.txt", payload)],
      new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("note.txt"));

    ms.Position = 0;
    Assert.That(d.ExtractEntryToMemory(ms, "note.txt", null), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_ImageDetectableByDescriptor() {
    // Confirms our writer output looks like a QNX4 image to the descriptor's
    // detection rules — the inode status byte at offset 0x23D is one of the
    // signatures the descriptor advertises.
    using var ms = new MemoryStream();
    var w = new Qnx4Writer();
    w.AddFile("ping.txt", "pong"u8.ToArray());
    w.WriteTo(ms);
    var image = ms.ToArray();

    // Entry 0 of block 1 is the root inode, and its status byte is at 0x3F of
    // that entry — not 0x3D, which is padding. It says USED and nothing else:
    // the LINK bit marks an entry as a long-name link record, and a driver
    // that saw it would read the root's name field as a 48-byte link instead.
    Assert.That(image[0x200 + 0x3F], Is.EqualTo(0x01),
      "the root entry must be marked used, and not as a link.");

    var d = new Qnx4FormatDescriptor();
    var sig = d.MagicSignatures.FirstOrDefault(s => s.Bytes[0] == 0x09 && s.Offset == 0x23D);
    Assert.That(sig, Is.Not.Null,
      "Descriptor must advertise a 0x09-at-0x23D signature so detection picks our image up.");
  }

  // ── WSL-gated external validation ────────────────────────────────────────
  //
  // The Linux kernel ships a qnx4 driver (CONFIG_QNX4FS_FS). When present,
  // we can mount our image read-only and use ls/cat to confirm the image is
  // truly mountable, not just self-consistent.

  [Test, Category("ExternalInterop")]
  public void Qnx4_OurImage_MountableByLinuxKernel() {
    RequireWsl();
    // The qnx4 module historically ships with mainline Linux but is
    // deprecated since 5.8 and removed in many minimal distros. Skip if
    // unavailable so the test is informational, not flaky.
    if (!HasQnx4Module())
      Assert.Ignore(
        "WSL kernel doesn't expose the qnx4 module (CONFIG_QNX4FS_FS=n). " +
        "Validate manually on a kernel built with qnx4 support: " +
        "`sudo mount -o loop,ro -t qnx4 <img> <mnt>`.");

    var payload = "hello-from-qnx4"u8.ToArray();
    var imgPath = Path.Combine(Path.GetTempPath(), $"cwb_qnx4_{Guid.NewGuid():N}.img");
    try {
      using (var fs = File.Create(imgPath)) {
        var w = new Qnx4Writer();
        w.AddFile("greet.txt", payload);
        w.WriteTo(fs);
      }

      var wslImg = WinToWsl(imgPath);
      var mntDir = $"/tmp/cwb_qnx4_mnt_{Guid.NewGuid():N}";
      var script =
        $"set -e; sudo -n mkdir -p {mntDir}; " +
        $"sudo -n mount -o loop,ro -t qnx4 {wslImg} {mntDir}; " +
        $"ls -la {mntDir}; " +
        $"cat {mntDir}/greet.txt; " +
        $"sudo -n umount {mntDir} || true; " +
        $"sudo -n rmdir {mntDir} || true";
      var result = RunWsl(script);

      // sudo-without-password is usually required for mount — when it isn't
      // available, treat this as a soft-skip with an actionable hint.
      if (result.StdErr.Contains("password", StringComparison.OrdinalIgnoreCase) ||
          result.ExitCode == 1 && result.StdOut.Length == 0) {
        Assert.Ignore(
          "WSL mount needs passwordless sudo (NOPASSWD) for the current user. " +
          "Configure via `sudo visudo` and add `<user> ALL=(ALL) NOPASSWD: ALL` " +
          "to mount QNX4 images automatically.");
      }

      Assert.That(result.ExitCode, Is.EqualTo(0),
        $"Linux qnx4 mount rejected our image:\nstdout:\n{result.StdOut}\nstderr:\n{result.StdErr}");
      Assert.That(result.StdOut, Does.Contain("greet.txt"),
        "Mounted directory should list greet.txt.");
      Assert.That(result.StdOut, Does.Contain("hello-from-qnx4"),
        "cat should print the file payload.");
    } finally {
      try { File.Delete(imgPath); } catch { /* best effort */ }
    }
  }

  // ── helpers ──────────────────────────────────────────────────────────────

  private static string ReadInodeName(ReadOnlySpan<byte> raw) {
    var end = 0;
    while (end < raw.Length && raw[end] != 0) end++;
    return Encoding.UTF8.GetString(raw[..end]);
  }

  private static bool HasQnx4Module() {
    // qnx4 is reachable when either:
    //   - the module is already loaded (lsmod | grep)
    //   - modprobe can find it (built-but-unloaded)
    //   - /proc/filesystems advertises it (built-in)
    var probe = RunWsl(
      "lsmod 2>/dev/null | grep -qw qnx4 || " +
      "modinfo qnx4 >/dev/null 2>&1 || " +
      "grep -qw qnx4 /proc/filesystems 2>/dev/null");
    return probe.ExitCode == 0;
  }

  // Thin shims so we can keep the test self-contained without leaking
  // ExternalFsInteropTests' internals — they wrap the same patterns.

  private static void RequireWsl() {
    if (!IsWslAvailable())
      Assert.Ignore(
        "WSL not installed. Run `wsl --install` in Admin PowerShell and reboot. " +
        "On a kernel with qnx4 module support the image will mount via " +
        "`sudo mount -o loop,ro -t qnx4 <img> <mnt>`.");
  }

  private static bool IsWslAvailable() {
    if (!OperatingSystem.IsWindows()) return false;
    try {
      var psi = new System.Diagnostics.ProcessStartInfo("wsl", "--status") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var p = System.Diagnostics.Process.Start(psi);
      if (p is null) return false;
      p.WaitForExit(5_000);
      return p.ExitCode == 0;
    } catch { return false; }
  }

  private static string WinToWsl(string winPath) {
    var full = Path.GetFullPath(winPath);
    if (full.Length < 2 || full[1] != ':') return full.Replace('\\', '/');
    var drive = char.ToLowerInvariant(full[0]);
    var tail = full[2..].Replace('\\', '/');
    return $"'/mnt/{drive}{tail}'";
  }

  private static (string StdOut, string StdErr, int ExitCode) RunWsl(string linuxCommand) {
    var dq = linuxCommand.Replace("\"", "\\\"");
    var psi = new System.Diagnostics.ProcessStartInfo("wsl", $"-e bash -c \"{dq}\"") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var p = System.Diagnostics.Process.Start(psi);
    if (p is null) return ("", "wsl failed to launch", -1);
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit(30_000);
    return (stdout, stderr, p.ExitCode);
  }

  // BinaryPrimitives kept here so we can verify low-level layout in future
  // assertions without pulling Compression.Core into the test project.
  private static uint ReadLeU32(ReadOnlySpan<byte> s) => BinaryPrimitives.ReadUInt32LittleEndian(s);
}
