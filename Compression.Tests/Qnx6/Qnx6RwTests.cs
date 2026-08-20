using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Compression.Registry;
using FileSystem.Qnx6;

namespace Compression.Tests.Qnx6;

/// <summary>
/// R/W (Add/Remove) gate for QNX6. Promotion from WORM is gated on three
/// invariants:
///   1. Add → List → Extract round-trip exposes the new file with byte-equal
///      content and round-trips alongside the pre-existing entries.
///   2. Remove eliminates the entry from List, zeroes the data extent (the
///      wipe contract), and the result still self-round-trips.
///   3. After every mutation the secondary superblock — wherever the primary's own
///      block count puts it, not simply at the tail — is byte-equal to the primary
///      at 0x2000: the dual-superblock Power-Safe contract must hold synchronously
///      across every Add/Remove call.
///
/// External validation: the Linux kernel ships a read-only qnx6 driver
/// (mainline since 2.6.39). When WSL + sudo + loop devices + qnx6 module are
/// available the post-mutation image is mounted and listed via that driver as a
/// best-effort bonus. Anything other than a clean exit-0 mount → clean skip;
/// the in-tree round-trip is the primary gate.
/// </summary>
[TestFixture]
public class Qnx6RwTests {

  // ── Add ───────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesModifiable() {
    var d = new Qnx6FormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
      "Qnx6FormatDescriptor must advertise IArchiveModifiable so the registry routes Add/Remove to it.");
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
      "Capabilities flags must include CanModify alongside the interface advertisement.");
  }

  [Test, Category("HappyPath")]
  public void Add_NewFile_RoundTripsAlongsidePreExisting() {
    var d = new Qnx6FormatDescriptor();
    var first = "first content\n"u8.ToArray();
    var second = "second content longer than the first\n"u8.ToArray();

    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("first.txt", first) }, new FormatCreateOptions());

    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("second.txt", second) });

    image.Position = 0;
    var entries = d.List(image, null).OrderBy(e => e.Name).ToList();
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "first.txt", "second.txt" }));

    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, "first.txt", null), Is.EqualTo(first).AsCollection,
      "pre-existing file must survive a subsequent Add with content unchanged.");
    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, "second.txt", null), Is.EqualTo(second).AsCollection,
      "newly added file must extract byte-equal to its source.");
  }

  [Test, Category("HappyPath")]
  public void Add_PreservesDualSuperblockMirror() {
    var d = new Qnx6FormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("a.txt", "a\n"u8.ToArray()) }, new FormatCreateOptions());

    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("b.txt", "b\n"u8.ToArray()) });

    AssertSuperblockMirror(image, "post-Add dual-superblock parity");
  }

  [Test, Category("HappyPath")]
  public void Add_LargeFile_RoundTripsAndExtendsImage() {
    var d = new Qnx6FormatDescriptor();
    var seed = "seed\n"u8.ToArray();
    var big = new byte[5000];
    for (var i = 0; i < big.Length; i++) big[i] = (byte)((i * 37 + 11) & 0xFF);

    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("seed.txt", seed) }, new FormatCreateOptions());
    var sizeBefore = image.Length;

    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("big.bin", big) });

    Assert.That(image.Length, Is.GreaterThan(sizeBefore),
      "image must grow to accommodate the new file's contiguous data extent.");
    image.Position = 0;
    var extracted = d.ExtractEntryToMemory(image, "big.bin", null);
    Assert.That(extracted, Is.EqualTo(big).AsCollection);
    AssertSuperblockMirror(image, "post-Add (large file) dual-superblock parity");
  }

  [Test, Category("Boundary")]
  public void Add_ReplacesByName() {
    var d = new Qnx6FormatDescriptor();
    var original = "original\n"u8.ToArray();
    var replacement = "this is the replacement payload\n"u8.ToArray();

    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("dup.txt", original) }, new FormatCreateOptions());

    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("dup.txt", replacement) });

    image.Position = 0;
    var entries = d.List(image, null);
    Assert.That(entries, Has.Count.EqualTo(1),
      "Add of an existing name must replace, not duplicate (matches every other R/W FS in the repo).");

    image.Position = 0;
    var actual = d.ExtractEntryToMemory(image, "dup.txt", null);
    Assert.That(actual, Is.EqualTo(replacement).AsCollection);
    AssertSuperblockMirror(image, "post-Add (replace) dual-superblock parity");
  }

  [Test, Category("Boundary")]
  public void Add_PastSingleBlockRootCapacity_Throws() {
    var d = new Qnx6FormatDescriptor();
    // Single-block root dir = 32 dirents. Create() fills with 32 entries
    // (writer caps at the same limit), then Add of a 33rd must throw.
    var initial = new List<ArchiveInputInfo>();
    // Fill the root exactly. This used to ask for thirty-two and rely on the
    // writer keeping thirty and dropping two without a word — which is the
    // silent loss the writer now refuses, so the setup says what it means.
    for (var i = 0; i < FileSystem.Qnx6.Qnx6Writer.MaxFiles; i++)
      initial.Add(MakeInput($"f{i:D2}.txt", [(byte)i]));

    using var image = new MemoryStream();
    d.Create(image, initial, new FormatCreateOptions());

    image.Position = 0;
    Assert.Throws<NotSupportedException>(() =>
        d.Add(image, new List<ArchiveInputInfo> { MakeInput("overflow.txt", "x"u8.ToArray()) }),
      "single-block root dir caps at 32 dirents; the 33rd Add must throw.");
  }

  // ── Remove ────────────────────────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_DropsEntryFromList() {
    var d = new Qnx6FormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> {
      MakeInput("keep.txt", "keep\n"u8.ToArray()),
      MakeInput("drop.txt", "drop\n"u8.ToArray()),
    }, new FormatCreateOptions());

    image.Position = 0;
    d.Remove(image, ["drop.txt"]);

    image.Position = 0;
    var entries = d.List(image, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("keep.txt"));

    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, "keep.txt", null),
      Is.EqualTo("keep\n"u8.ToArray()).AsCollection,
      "surviving entry must extract byte-equal post-Remove.");
    AssertSuperblockMirror(image, "post-Remove dual-superblock parity");
  }

  [Test, Category("HappyPath")]
  public void Remove_WipesDataExtent() {
    // The Remove contract says removed bytes must be unrecoverable from the
    // resulting image. We assert by writing a distinctive marker pattern,
    // removing the file, then scanning the image to ensure the marker is gone.
    var d = new Qnx6FormatDescriptor();
    var marker = Encoding.ASCII.GetBytes("WIPE-ME-XYZZY-MARKER-0123456789");
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("victim.bin", marker) }, new FormatCreateOptions());

    image.Position = 0;
    d.Remove(image, ["victim.bin"]);

    var img = image.ToArray();
    var found = IndexOf(img, marker);
    Assert.That(found, Is.LessThan(0),
      "Remove must wipe the file's data extent — the marker pattern must no longer appear anywhere in the image.");
  }

  [Test, Category("Boundary")]
  public void Remove_UnknownEntry_NoOp() {
    var d = new Qnx6FormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("only.txt", "x\n"u8.ToArray()) }, new FormatCreateOptions());
    var before = image.ToArray();

    image.Position = 0;
    d.Remove(image, ["does-not-exist.txt"]);

    var after = image.ToArray();
    Assert.That(after, Is.EqualTo(before).AsCollection,
      "Remove of an unknown name must be a no-op — no bytes touched.");
  }

  [Test, Category("Boundary")]
  public void Remove_ThenAdd_RecyclesInodeSlot() {
    var d = new Qnx6FormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("first.txt", "first\n"u8.ToArray()) }, new FormatCreateOptions());

    image.Position = 0;
    d.Remove(image, ["first.txt"]);
    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("second.txt", "second\n"u8.ToArray()) });

    image.Position = 0;
    var entries = d.List(image, null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("second.txt"));

    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, "second.txt", null),
      Is.EqualTo("second\n"u8.ToArray()).AsCollection);
    AssertSuperblockMirror(image, "post-Remove-then-Add dual-superblock parity");
  }

  // ── External: WSL kernel qnx6 driver mount-and-list ───────────────────────

  [Test, Category("ExternalInterop")]
  public void Wsl_KernelDriver_Mounts_PostMutationImage() {
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
      Assert.Ignore("WSL probe only runs on Windows hosts.");
    if (!IsWslAvailable())
      Assert.Ignore("wsl.exe not available — skipping QNX6 kernel-mount probe.");

    var d = new Qnx6FormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, new List<ArchiveInputInfo> { MakeInput("seed.txt", "seed\n"u8.ToArray()) }, new FormatCreateOptions());
    image.Position = 0;
    d.Add(image, new List<ArchiveInputInfo> { MakeInput("added-by-modifier.txt", "added\n"u8.ToArray()) });

    var tmp = Path.Combine(Path.GetTempPath(), $"qnx6-rw-{Guid.NewGuid():N}.img");
    File.WriteAllBytes(tmp, image.ToArray());
    try {
      var wslPath = WindowsToWsl(tmp);
      var mountScript = $@"set -e
                            mp=$(mktemp -d)
                            sudo -n modprobe qnx6 2>/dev/null || true
                            sudo -n mount -t qnx6 -o loop,ro {wslPath} $mp 2>/dev/null || exit 77
                            ls -1 $mp
                            sudo -n umount $mp 2>/dev/null || true";
      var (exit, stdout) = RunWsl("bash", "-c", mountScript);
      if (exit == 77 || exit != 0) {
        Assert.Ignore($"WSL qnx6 mount unavailable (exit={exit}): kernel driver, loop devices, or sudo unavailable.");
        return;
      }
      Assert.That(stdout, Does.Contain("added-by-modifier.txt"),
        "Linux kernel qnx6 driver should see the post-Add entry alongside the seed.");
    } finally {
      try { File.Delete(tmp); } catch { /* best-effort */ }
    }
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  private static ArchiveInputInfo MakeInput(string archiveName, byte[] data)
    => ArchiveInputInfo.InMemory(archiveName, data);

  private static void AssertSuperblockMirror(Stream image, string context) {
    image.Position = 0;
    using var ms = new MemoryStream();
    image.CopyTo(ms);
    var img = ms.ToArray();
    Assert.That(img.Length, Is.GreaterThan(0x2000 + 512),
      $"{context}: image must be larger than primary superblock window.");
    var primary = img.AsSpan(0x2000, 512).ToArray();

    // The mirror is not simply at the end of the image. A driver adds the boot and
    // superblock areas to the block count the primary records and reads it there,
    // so that is where it has to be — and the block it occupies is not one of the
    // filesystem's own, which is what the count leaves room for.
    var blockSize = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000 + 0x30));
    var numBlocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(0x2000 + 0x3C));
    var mirrorAt = (long)(numBlocks + (0x2000 + 0x1000) / blockSize) * blockSize;
    Assert.That(mirrorAt + 512, Is.LessThanOrEqualTo(img.Length),
      $"{context}: the mirror the superblock points at must be inside the image.");

    var secondary = img.AsSpan((int)mirrorAt, 512).ToArray();
    Assert.That(secondary, Is.EqualTo(primary).AsCollection,
      $"{context}: the secondary superblock must mirror the primary byte-for-byte (Power-Safe contract).");
  }

  private static int IndexOf(byte[] haystack, byte[] needle) {
    for (var i = 0; i <= haystack.Length - needle.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++) {
        if (haystack[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }

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
    var psi = new ProcessStartInfo("wsl.exe") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    psi.ArgumentList.Add(program);
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    if (!p.WaitForExit(15_000)) {
      try { p.Kill(); } catch { /* ignore */ }
      return (-1, stdout);
    }
    return (p.ExitCode, stdout);
  }

  private static string WindowsToWsl(string path) {
    if (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))
      return path;
    var drive = char.ToLowerInvariant(path[0]);
    var rest = path.Substring(2).Replace('\\', '/');
    return $"/mnt/{drive}{rest}";
  }
}
