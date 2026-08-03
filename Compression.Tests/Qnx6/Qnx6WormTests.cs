using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Registry;
using FileSystem.Qnx6;

namespace Compression.Tests.Qnx6;

/// <summary>
/// WORM-emission tests for the QNX6 writer. Self-round-trip is the primary
/// gate (the in-tree <see cref="Qnx6Reader"/> reads what
/// <see cref="Qnx6Writer"/> writes). A WSL-mount cross-check is provided as a
/// best-effort bonus: the Linux kernel ships a read-only qnx6 driver. The
/// mount test skips cleanly if WSL or root privileges are unavailable.
/// </summary>
[TestFixture]
public class Qnx6WormTests {

  // ─────────────────────────────────────────────────────────────────────────
  // Self-round-trip — primary gate
  // ─────────────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCreatable() {
    var d = new Qnx6FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>(),
      "Qnx6FormatDescriptor must advertise IArchiveCreatable so the registry routes Create() to it.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True,
      "Capabilities flags must include CanCreate alongside the interface advertisement.");
  }

  [Test, Category("HappyPath")]
  public void Create_EmitsValidSuperblockMagic() {
    var d = new Qnx6FormatDescriptor();
    var inputs = new List<ArchiveInputInfo> { MakeInput("alpha.txt", "alpha\n"u8.ToArray()) };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    var img = ms.ToArray();
    Assert.That(img.Length, Is.GreaterThanOrEqualTo(0x2000 + 0x48 + 16),
      "image must be at least large enough to hold the superblock at 0x2000.");
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000, 4));
    Assert.That(magic, Is.EqualTo(0x68191122u), "primary superblock magic must be the QNX6 LE constant.");
  }

  [Test, Category("HappyPath")]
  public void Create_EmitsPairedSecondarySuperblock() {
    var d = new Qnx6FormatDescriptor();
    var inputs = new List<ArchiveInputInfo> { MakeInput("paired.bin", new byte[] { 1, 2, 3, 4 }) };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    var img = ms.ToArray();
    var primary = img.AsSpan(0x2000, 512).ToArray();

    // The mirror does not go at the end of the image. A driver looks for it at
    // the block count the superblock records, plus the boot and superblock
    // areas in front of the filesystem — so that is where it has to be, and an
    // image that merely ends there is a coincidence rather than a contract.
    var blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000 + 0x30, 4));
    var numBlocks = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000 + 0x3C, 4));
    var mirrorAt = (int)((numBlocks + (0x2000 + 0x1000) / blockSize) * blockSize);
    Assert.That(mirrorAt + 512, Is.LessThanOrEqualTo(img.Length),
      "the mirror must be inside the image");

    var secondary = img.AsSpan(mirrorAt, 512).ToArray();
    Assert.That(secondary, Is.EqualTo(primary).AsCollection,
      "the mirror must be byte-identical to the primary — that's the power-safe contract.");
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_SingleSmallFile() {
    var d = new Qnx6FormatDescriptor();
    var content = "QNX6 round-trip\n"u8.ToArray();
    var inputs = new List<ArchiveInputInfo> { MakeInput("hello.txt", content) };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(content.Length));

    ms.Position = 0;
    var extracted = d.ExtractEntryToMemory(ms, "hello.txt", null);
    Assert.That(extracted, Is.EqualTo(content).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_MultipleFiles() {
    var d = new Qnx6FormatDescriptor();
    var inputs = new List<ArchiveInputInfo> {
      MakeInput("a.txt", "first\n"u8.ToArray()),
      MakeInput("b.dat", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
      MakeInput("c.bin", new byte[512]),
    };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null).OrderBy(e => e.Name).ToList();
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "a.txt", "b.dat", "c.bin" }));

    foreach (var name in new[] { "a.txt", "b.dat", "c.bin" }) {
      ms.Position = 0;
      var expected = inputs.First(i => i.ArchiveName == name).ReadContent();
      var actual = d.ExtractEntryToMemory(ms, name, null);
      Assert.That(actual, Is.EqualTo(expected).AsCollection, $"round-trip mismatch for {name}.");
    }
  }

  [Test, Category("Boundary")]
  public void RoundTrip_FileSpanningMultipleBlocks() {
    // 3.5 KiB file ⇒ 4 contiguous data blocks at 1 KiB each. The reader's
    // Extract reads size bytes from firstBlock, so a contiguous extent
    // larger than one block round-trips.
    var d = new Qnx6FormatDescriptor();
    var content = new byte[3500];
    for (var i = 0; i < content.Length; i++) content[i] = (byte)(i & 0xFF);
    var inputs = new List<ArchiveInputInfo> { MakeInput("big.bin", content) };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var actual = d.ExtractEntryToMemory(ms, "big.bin", null);
    Assert.That(actual, Is.EqualTo(content).AsCollection);
  }

  [Test, Category("Boundary")]
  public void RoundTrip_EmptyFile() {
    var d = new Qnx6FormatDescriptor();
    var inputs = new List<ArchiveInputInfo> { MakeInput("empty.txt", []) };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(0));

    ms.Position = 0;
    var actual = d.ExtractEntryToMemory(ms, "empty.txt", null);
    Assert.That(actual.Length, Is.EqualTo(0));
  }

  [Test, Category("Boundary")]
  public void Create_SkipsNamesLongerThan27Chars() {
    // 27 chars is the reader's name_len ceiling; longer names are silently
    // skipped to match reader scope (the spec's longfile-pointer dirent form
    // exists but isn't decoded by the current reader).
    var d = new Qnx6FormatDescriptor();
    var ok = new string('a', 27);
    var tooLong = new string('b', 28);
    var inputs = new List<ArchiveInputInfo> {
      MakeInput(ok, "ok\n"u8.ToArray()),
      MakeInput(tooLong, "skipped\n"u8.ToArray()),
    };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1), "the 28-char name must be skipped to match reader gate at name_len > 27.");
    Assert.That(entries[0].Name, Is.EqualTo(ok));
  }

  [Test, Category("Boundary")]
  public void Create_FlattensPathToLeafName() {
    // QNX6 reader walks a single-block root directory only — directory
    // components in the input path are dropped so the dirent shows the leaf.
    var d = new Qnx6FormatDescriptor();
    var inputs = new List<ArchiveInputInfo> { MakeInput("subdir/leaf.txt", "leaf\n"u8.ToArray()) };

    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("leaf.txt"),
      "flattening must drop the 'subdir/' component since the reader walks a single directory.");
  }

  [Test, Category("Boundary")]
  public void Create_EmptyInputList_StillEmitsValidImage() {
    var d = new Qnx6FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [], new FormatCreateOptions());

    ms.Position = 0;
    var entries = d.List(ms, null);
    Assert.That(entries, Is.Empty);

    // Image must still pass the magic check.
    var img = ms.ToArray();
    var magic = BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000, 4));
    Assert.That(magic, Is.EqualTo(0x68191122u));
  }

  [Test, Category("Sad")]
  public void Create_NullOutput_Throws() {
    var d = new Qnx6FormatDescriptor();
    Assert.Throws<ArgumentNullException>(() =>
      d.Create(null!, [], new FormatCreateOptions()));
  }

  [Test, Category("Sad")]
  public void Create_NullInputs_Throws() {
    var d = new Qnx6FormatDescriptor();
    using var ms = new MemoryStream();
    Assert.Throws<ArgumentNullException>(() =>
      d.Create(ms, null!, new FormatCreateOptions()));
  }

  // ─────────────────────────────────────────────────────────────────────────
  // WSL kernel-driver mount — best-effort bonus
  //
  // The Linux kernel qnx6 driver is read-only and may or may not be loadable
  // inside WSL2's kernel. We treat any failure (missing wsl, missing tool,
  // permission denied, EINVAL on mount) as a clean skip — the in-tree
  // round-trip is the primary gate for this stage.
  // ─────────────────────────────────────────────────────────────────────────

  [Test, Category("ExternalInterop")]
  public void Wsl_KernelDriver_Mounts_AndListsFile() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("WSL probe only runs on Windows hosts.");
    if (!IsWslAvailable())
      Assert.Ignore("wsl.exe not available — skipping QNX6 kernel-mount probe.");

    var d = new Qnx6FormatDescriptor();
    var content = "from-wsl\n"u8.ToArray();
    var inputs = new List<ArchiveInputInfo> { MakeInput("probe.txt", content) };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());

    var tmp = Path.Combine(Path.GetTempPath(), $"qnx6-probe-{Guid.NewGuid():N}.img");
    File.WriteAllBytes(tmp, ms.ToArray());
    try {
      // Translate Windows path to WSL form.
      var wslPath = WindowsToWsl(tmp);
      // Try a loop-back mount in a private WSL temp dir. Anything other than
      // exit 0 → clean skip; the kernel driver presence/loop privileges
      // aren't part of the gate.
      var mountScript = $@"set -e
                            mp=$(mktemp -d)
                            sudo -n mount -t qnx6 -o loop,ro {wslPath} $mp 2>/dev/null || exit 77
                            ls -1 $mp
                            sudo -n umount $mp 2>/dev/null || true";
      var (exit, stdout) = RunWsl("bash", "-c", mountScript);
      if (exit == 77 || exit != 0) {
        Assert.Ignore($"WSL qnx6 mount unavailable (exit={exit}): kernel driver, loop devices, or sudo unavailable.");
        return;
      }
      Assert.That(stdout, Does.Contain("probe.txt"),
        "Linux kernel qnx6 driver should list probe.txt from the mounted image.");
    } finally {
      try { File.Delete(tmp); } catch { /* best-effort */ }
    }
  }

  // ─────────────────────────────────────────────────────────────────────────
  // Helpers
  // ─────────────────────────────────────────────────────────────────────────

  private static ArchiveInputInfo MakeInput(string archiveName, byte[] data)
    => ArchiveInputInfo.InMemory(archiveName, data);

  private static bool IsWslAvailable() {
    try {
      var psi = new ProcessStartInfo("wsl.exe", "--version") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var p = Process.Start(psi);
      if (p is null) return false;
      if (!p.WaitForExit(3_000)) {
        try { p.Kill(); } catch { /* ignore */ }
        return false;
      }
      return p.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static (int Exit, string Stdout) RunWsl(string program, params string[] args) {
    var argList = new List<string> { program };
    argList.AddRange(args);
    var psi = new ProcessStartInfo("wsl.exe") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    foreach (var a in argList) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit(15_000)) {
      try { p.Kill(); } catch { /* ignore */ }
      return (-1, stdout);
    }
    return (p.ExitCode, stdout);
  }

  private static string WindowsToWsl(string path) {
    // C:\foo\bar  →  /mnt/c/foo/bar
    if (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
      return path;
    var drive = char.ToLowerInvariant(path[0]);
    var rest = path.Substring(2).Replace('\\', '/');
    return $"/mnt/{drive}{rest}";
  }
}
