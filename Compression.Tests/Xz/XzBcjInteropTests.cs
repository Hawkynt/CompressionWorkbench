using System.Buffers.Binary;
using Compression.Core.Streams;
using FileFormat.Xz;

namespace Compression.Tests.Xz;

/// <summary>
/// Round-trips XZ blocks that use the newer BCJ pre-filters (ARM64, RISC-V),
/// and — where a compatible system <c>xz</c> is reachable via WSL — cross-checks
/// our ARM64 BCJ transform against liblzma byte-for-byte. The ARM64 filter is
/// supported by xz 5.4+ so this is a real external gate; RISC-V (xz 5.6+) is
/// validated by self-round-trip on aligned instruction patterns because the
/// installed xz predates it.
/// </summary>
[TestFixture]
public class XzBcjInteropTests {

  // ── self round-trip through our own XzStream ─────────────────────────

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithBcjArm64Filter() {
    var data = BuildArm64Code();
    var preFilters = new List<(ulong, byte[])> { (XzConstants.FilterBcjArm64, []) };
    var compressed = CompressWithFilters(data, preFilters);
    Assert.That(DecompressWithOurs(compressed), Is.EqualTo(data));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_WithBcjRiscVFilter() {
    var data = BuildRiscVCode();
    var preFilters = new List<(ulong, byte[])> { (XzConstants.FilterBcjRiscV, []) };
    var compressed = CompressWithFilters(data, preFilters);
    Assert.That(DecompressWithOurs(compressed), Is.EqualTo(data));
  }

  // ── external gate: our ARM64 .xz must decode byte-identically in xz ──

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void OurArm64Xz_DecodesInSystemXz_ByteIdentical() {
    RequireXzWithArm64();

    var data = BuildArm64Code();
    var preFilters = new List<(ulong, byte[])> { (XzConstants.FilterBcjArm64, []) };
    var compressed = CompressWithFilters(data, preFilters);

    var dir = MakeTempDir();
    try {
      var xzPath = Path.Combine(dir, "ours.xz");
      var outPath = Path.Combine(dir, "ours.out");
      File.WriteAllBytes(xzPath, compressed);

      var r = FsInteropToolbox.RunWsl(
        $"xz -dc {FsInteropToolbox.WinToWsl(xzPath)} > {FsInteropToolbox.WinToWsl(outPath)}");
      Assert.That(r.ExitCode, Is.Zero,
        $"system xz failed to decode our ARM64 BCJ stream:\n{r.StdErr}");

      var decoded = File.ReadAllBytes(outPath);
      Assert.That(decoded, Is.EqualTo(data),
        "system xz decode of our ARM64 BCJ stream differs — our transform does not match liblzma.");
    } finally {
      TryDelete(dir);
    }
  }

  // ── external gate (reverse): xz --arm64 output must decode in ours ───

  [Category("OsIntegration")]
  [Category("Wsl")]
  [Test]
  public void SystemXzArm64_DecodesInOurs_ByteIdentical() {
    RequireXzWithArm64();

    var data = BuildArm64Code();
    var dir = MakeTempDir();
    try {
      var inPath = Path.Combine(dir, "in.bin");
      var xzPath = Path.Combine(dir, "sys.xz");
      File.WriteAllBytes(inPath, data);

      var r = FsInteropToolbox.RunWsl(
        $"xz --arm64 --lzma2=preset=6 -c {FsInteropToolbox.WinToWsl(inPath)} " +
        $"> {FsInteropToolbox.WinToWsl(xzPath)}");
      Assert.That(r.ExitCode, Is.Zero,
        $"system xz failed to compress with --arm64:\n{r.StdErr}");

      var decoded = DecompressWithOurs(File.ReadAllBytes(xzPath));
      Assert.That(decoded, Is.EqualTo(data),
        "our XzStream decode of an xz --arm64 stream differs — our transform does not match liblzma.");
    } finally {
      TryDelete(dir);
    }
  }

  // ── helpers ──────────────────────────────────────────────────────────

  private static void RequireXzWithArm64() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("xz"))
      Assert.Ignore("xz not reachable. On Windows: `wsl --install` then `sudo apt install -y xz-utils`.");
    var help = FsInteropToolbox.RunWsl("xz --long-help 2>&1 | grep -- --arm64");
    if (help.ExitCode != 0 || string.IsNullOrWhiteSpace(help.StdOut))
      Assert.Ignore("Installed xz lacks the --arm64 BCJ filter (needs xz 5.4+).");
  }

  /// <summary>Builds a buffer of little-endian ARM64 words: BL, ADRP, and filler.</summary>
  private static byte[] BuildArm64Code() {
    var words = new List<uint>();
    for (var i = 0; i < 256; ++i) {
      switch (i % 4) {
        case 0: words.Add(0x94000000u | (uint)(i & 0x03FFFFFF)); break;      // BL +
        case 1: words.Add(0x97FFFFF0u | (uint)(i & 0xF)); break;             // BL -
        case 2: words.Add(0x90000000u | ((uint)(i & 3) << 29) | ((uint)(i & 0x3F) << 5)); break; // ADRP x0
        default: words.Add(0xD503201Fu); break;                              // NOP
      }
    }
    var buf = new byte[words.Count * 4];
    for (var i = 0; i < words.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4), words[i]);
    return buf;
  }

  /// <summary>Builds a buffer of RISC-V instructions: JAL, AUIPC+ADDI pairs, and filler.</summary>
  private static byte[] BuildRiscVCode() {
    var words = new List<uint>();
    for (var i = 0; i < 256; ++i) {
      switch (i % 4) {
        case 0: words.Add(0x000000EFu | ((uint)(i & 0xFF) << 12)); break;    // JAL x1
        case 1: words.Add(0x00000517u | ((uint)(i & 0xFFF) << 12)); break;   // AUIPC x10
        case 2: words.Add(0x00050513u | ((uint)(i & 0xFFF) << 20)); break;   // ADDI x10,x10,imm
        default: words.Add(0x00000013u); break;                              // NOP (ADDI x0,x0,0)
      }
    }
    var buf = new byte[words.Count * 4];
    for (var i = 0; i < words.Count; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4), words[i]);
    return buf;
  }

  private static byte[] CompressWithFilters(byte[] data,
    List<(ulong FilterId, byte[] Properties)> preFilters) {
    using var ms = new MemoryStream();
    using (var xz = new XzStream(ms, CompressionStreamMode.Compress,
      1 << 20, XzConstants.CheckCrc64, preFilters, leaveOpen: true))
      xz.Write(data, 0, data.Length);
    return ms.ToArray();
  }

  private static byte[] DecompressWithOurs(byte[] compressed) {
    using var ms = new MemoryStream(compressed);
    using var xz = new XzStream(ms, CompressionStreamMode.Decompress);
    using var output = new MemoryStream();
    xz.CopyTo(output);
    return output.ToArray();
  }

  private static string MakeTempDir() {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_xz_bcj_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
  }

  private static void TryDelete(string dir) {
    try { Directory.Delete(dir, true); } catch { /* best effort */ }
  }
}
