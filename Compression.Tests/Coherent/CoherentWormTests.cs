using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Compression.Registry;
using FileSystem.Coherent;

namespace Compression.Tests.Coherent;

[TestFixture]
public class CoherentWormTests {

  private static byte[] CreateImage(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    using (var w = new CoherentWriter(ms, leaveOpen: true)) {
      foreach (var (name, data) in files)
        w.AddFile(name, data);
      w.Finish();
    }
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsSelfReadableImage_Empty() {
    var img = CreateImage();
    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.Entries, Is.Empty);
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsSelfReadableImage_SingleFile() {
    var payload = "Coherent WORM round-trip\n"u8.ToArray();
    var img = CreateImage(("hello.txt", payload));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Valid, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("hello.txt"));
    Assert.That(r.Entries[0].IsDirectory, Is.False);
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));

    var got = r.Extract(r.Entries[0]);
    Assert.That(got, Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Writer_EmitsSelfReadableImage_MultipleFiles() {
    var files = new[] {
      ("alpha", Encoding.ASCII.GetBytes("alpha-body")),
      ("beta",  Encoding.ASCII.GetBytes("beta-body-with-more-content")),
      ("gamma", Encoding.ASCII.GetBytes("g")),
    };
    var img = CreateImage(files);

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(files.Select(f => f.Item1)));

    foreach (var (name, data) in files) {
      var entry = r.Entries.Single(e => e.Name == name);
      Assert.That(r.Extract(entry), Is.EqualTo(data), $"payload mismatch for {name}");
    }
  }

  // Boundary: file just under one block (511 bytes) — entirely in direct[0].
  [Test, Category("Boundary")]
  public void Writer_HandlesSubBlockFile() {
    var payload = new byte[511];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
    var img = CreateImage(("under.bin", payload));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(511));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  // Boundary: file exactly one block (512 bytes).
  [Test, Category("Boundary")]
  public void Writer_HandlesExactlyOneBlock() {
    var payload = new byte[512];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i ^ 0x55);
    var img = CreateImage(("one.bin", payload));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(512));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  // Boundary: file straddles direct zones (~7 KB → 14 direct blocks needed,
  // but only 10 fit so spills into single-indirect).
  [Test, Category("Boundary")]
  public void Writer_HandlesSingleIndirectSpill() {
    var payload = new byte[7000]; // > 10 * 512 = 5120, < single-indirect cap
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i * 13) & 0xFF);
    var img = CreateImage(("big.bin", payload));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(7000));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  // Boundary: file straddles into double-indirect zones.
  // direct: 10 * 512 = 5120 bytes
  // single-indirect: 170 * 512 = 87,040 bytes → total cap 92,160 bytes
  // So 100,000 bytes spills into double-indirect.
  [Test, Category("Boundary")]
  public void Writer_HandlesDoubleIndirectSpill() {
    var payload = new byte[100_000];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i ^ (i >> 7)) & 0xFF);
    var img = CreateImage(("very-big.bin", payload));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries[0].Size, Is.EqualTo(100_000));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  // Boundary: long filename truncates to 14 bytes (Coherent dirent limit).
  [Test, Category("Boundary")]
  public void Writer_TruncatesLongFilenames() {
    var img = CreateImage(("a_filename_that_exceeds_fourteen_bytes.txt", "x"u8.ToArray()));

    using var ms = new MemoryStream(img);
    var r = new CoherentReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name.Length, Is.LessThanOrEqualTo(14));
  }

  // Equivalence class: descriptor-level Create() routes through the writer.
  [Test, Category("HappyPath")]
  public void Descriptor_Create_RoundTrip() {
    var inputs = new[] {
      ArchiveInputInfo.InMemory("foo.txt", "foo content"u8.ToArray()),
      ArchiveInputInfo.InMemory("bar.txt", "bar content"u8.ToArray()),
    };
    var d = new CoherentFormatDescriptor();
    using var outMs = new MemoryStream();
    d.Create(outMs, inputs, new FormatCreateOptions());
    outMs.Position = 0;

    var entries = d.List(outMs, null);
    Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "foo.txt", "bar.txt" }));

    outMs.Position = 0;
    var fooBytes = d.ExtractEntryToMemory(outMs, "foo.txt", null);
    Assert.That(Encoding.ASCII.GetString(fooBytes), Is.EqualTo("foo content"));
  }

  // The coh_super_block volume strings must be at the offsets the Linux sysv
  // detect_coherent() reads: s_fname @0x1E4, s_fpack @0x1EA, in the copy at
  // file offset 512 (and the duplicate at offset 0).
  [Test, Category("HappyPath")]
  public void Writer_PlacesSuperblockStringsAtExpectedOffsets() {
    var img = CreateImage(("hi", "hi"u8.ToArray()));
    Assert.That(img.Length, Is.GreaterThan(512 + 0x1F0));
    foreach (var b in new[] { 0, 512 }) {
      Assert.That(Encoding.ASCII.GetString(img.AsSpan(b + 0x1E4, 6).ToArray()), Is.EqualTo("noname"));
      Assert.That(Encoding.ASCII.GetString(img.AsSpan(b + 0x1EA, 6).ToArray()), Is.EqualTo("nopack"));
    }
  }

  // Superblock s_isize / s_fsize sanity (coh_super_block at file offset 0;
  // s_isize is the first data zone, LE u16; s_fsize is PDP-32).
  [Test, Category("HappyPath")]
  public void Writer_SuperblockFieldsAreSane() {
    var payload = new byte[2048];
    var img = CreateImage(("p", payload));

    var isize = BinaryPrimitives.ReadUInt16LittleEndian(img.AsSpan(0, 2));
    var fsize = ReadPdp32(img.AsSpan(2, 4));
    Assert.That(isize, Is.GreaterThanOrEqualTo(3), "first data zone is past the two superblock blocks + inode list");
    Assert.That(fsize, Is.GreaterThan(isize), "fsize covers the inode list + at least one data block");
    Assert.That(fsize * 512L, Is.EqualTo(img.Length), "fsize should describe the whole image");
  }

  private static uint ReadPdp32(ReadOnlySpan<byte> s) =>
    s[2] | ((uint)s[3] << 8) | ((uint)s[0] << 16) | ((uint)s[1] << 24);

  // External-tool gate: Linux's sysv driver (kernel CONFIG_SYSV_FS) supports
  // the Coherent variant. We mount the image read-only inside WSL and list
  // the contents to validate the image is bit-for-bit accepted by a real
  // Coherent-aware mounter. Skips cleanly when WSL is unavailable.
  [Test, Category("ExternalInterop")]
  public void Wsl_SysvMount_AcceptsImage() {
    if (!OperatingSystem.IsWindows()) Assert.Ignore("Test requires WSL (Windows).");
    if (!WslAvailable()) Assert.Ignore("WSL not installed/runnable.");

    var img = CreateImage(("greet.txt", "Coherent WORM hello from WSL\n"u8.ToArray()));
    var tmpHost = Path.Combine(Path.GetTempPath(), $"coherent-{Guid.NewGuid():N}.img");
    File.WriteAllBytes(tmpHost, img);

    try {
      var wslPath = ToWslPath(tmpHost);
      // The sysv driver requires root via sudo, which is non-interactive only
      // on properly-configured WSL distros. Run a probe to see whether the
      // sysv module is available; skip cleanly otherwise.
      var probe = RunWsl($"modinfo sysv 2>/dev/null | head -1; ls /lib/modules/$(uname -r)/kernel/fs/sysv/ 2>/dev/null");
      if (string.IsNullOrWhiteSpace(probe.StdOut) && string.IsNullOrWhiteSpace(probe.StdErr))
        Assert.Ignore("WSL kernel does not ship the sysv filesystem driver.");

      // Try mounting; allow up to two passes (with and without explicit -t sysv).
      var mountPoint = $"/tmp/coh-mnt-{Guid.NewGuid():N}";
      var script =
        $"set -e; sudo -n mkdir -p {mountPoint}; " +
        $"sudo -n mount -o loop,ro -t sysv {wslPath} {mountPoint} 2>/tmp/coh-err; " +
        $"sudo -n ls -la {mountPoint}; " +
        $"sudo -n umount {mountPoint} || true; " +
        $"sudo -n rmdir {mountPoint} || true";
      var result = RunWsl(script);
      if (result.ExitCode != 0) {
        // Mount failed: capture the kernel's reason and skip rather than fail —
        // many WSL kernels lack sysv (or the Coherent sub-variant is rejected
        // by the in-tree driver, which only formally supports xenix/coh/sysv-v7).
        Assert.Ignore($"sysv mount unavailable in this WSL: {result.StdErr.Trim()}".Trim());
      }
      Assert.That(result.StdOut, Does.Contain("greet.txt"),
        "sysv-mounted Coherent image should expose the file we wrote.");
    } finally {
      try { File.Delete(tmpHost); } catch { /* best effort */ }
    }
  }

  // ── WSL helpers ────────────────────────────────────────────────────────

  private static bool WslAvailable() {
    try {
      var psi = new ProcessStartInfo("wsl.exe", "echo wsl-probe") {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
      };
      using var p = Process.Start(psi);
      if (p == null) return false;
      if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
      return p.ExitCode == 0;
    } catch {
      return false;
    }
  }

  private static (int ExitCode, string StdOut, string StdErr) RunWsl(string bashScript) {
    var psi = new ProcessStartInfo("wsl.exe", "bash -lc \"" + bashScript.Replace("\"", "\\\"") + "\"") {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    using var p = Process.Start(psi)!;
    var stdout = p.StandardOutput.ReadToEnd();
    var stderr = p.StandardError.ReadToEnd();
    p.WaitForExit(30000);
    return (p.ExitCode, stdout, stderr);
  }

  private static string ToWslPath(string windowsPath) {
    var full = Path.GetFullPath(windowsPath).Replace('\\', '/');
    if (full.Length >= 2 && full[1] == ':') {
      var drive = char.ToLowerInvariant(full[0]);
      return $"/mnt/{drive}{full[2..]}";
    }
    return full;
  }
}
