using System.Buffers.Binary;
using System.Diagnostics;
using Compression.Registry;
using FileFormat.Isz;

namespace Compression.Tests.Isz;

[TestFixture]
public class IszTests {
  internal static byte[] SampleIso(int sectors, int seed) {
    var data = new byte[sectors * 2048];
    var random = new Random(seed);
    random.NextBytes(data.AsSpan(0, data.Length / 4));
    for (var i = data.Length / 2; i < data.Length; ++i) data[i] = (byte)"CD001 volume "[i % 13];
    return data;
  }

  private static byte[] Write(byte[] iso, int blockSize = IszImage.DefaultBlockSize) {
    using var ms = new MemoryStream();
    IszImage.Write(ms, iso, blockSize);
    return ms.ToArray();
  }

  private static byte[] Read(byte[] isz) {
    using var s = new MemoryStream(isz);
    using var o = new MemoryStream();
    IszImage.Read(s).CopyImageTo(s, o);
    return o.ToArray();
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(16)]
  [TestCase(17)]
  [TestCase(200)]
  public void GivenAnIso_WhenRoundTripped_ThenIdentical(int sectors) {
    var iso = SampleIso(sectors, sectors);
    var isz = Write(iso);
    Assert.That(isz.AsSpan(0, 4).SequenceEqual("IsZ!"u8), Is.True);
    Assert.That(isz[4], Is.EqualTo(64));
    Assert.That(Read(isz), Is.EqualTo(iso));
  }

  [Test]
  public void GivenAZeroChunk_WhenWritten_ThenItStoresNoDataAndTheTableIsObfuscated() {
    var isz = Write(new byte[0x8000]);
    Assert.That(isz.Length, Is.EqualTo(64 + 3));
    // value = kind 0 << 22 | 0x8000 = 0x008000, XOR B6 8C A5.
    Assert.That(isz.AsSpan(64, 3).ToArray(), Is.EqualTo(new byte[] { 0x00 ^ 0xB6, 0x80 ^ 0x8C, 0x00 ^ 0xA5 }));
    Assert.That(Read(isz), Is.EqualTo(new byte[0x8000]));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndReplacing_ThenTheNewImageReads() {
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, SampleIso(40, 1));
      using var ms = new MemoryStream();
      var d = new IszFormatDescriptor();
      d.Create(ms, [new ArchiveInputInfo(path, "image.iso", false)], new FormatCreateOptions());
      var replacement = SampleIso(60, 2);
      File.WriteAllBytes(path, replacement);
      d.Add(ms, [new ArchiveInputInfo(path, "image.iso", false)]);
      Assert.That(Read(ms.ToArray()), Is.EqualTo(replacement));
      Assert.That(d.List(new MemoryStream(ms.ToArray()), null).Select(e => e.Name), Is.EquivalentTo(new[] { "metadata.ini", "image.iso" }));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenAPartialSector_WhenWritten_ThenRefused()
    => Assert.Throws<ArgumentException>(() => Write(new byte[1000]));

  [Test]
  public void GivenACorruptedChunk_WhenRead_ThenTheCrcRefusesIt() {
    var isz = Write(SampleIso(64, 3));
    isz[^1] ^= 0x20;
    Assert.Throws<InvalidDataException>(() => Read(isz));
  }

  [Test]
  public void GivenAnEncryptedHeader_WhenRead_ThenNotSupportedAndListFallsBack() {
    var isz = Write(SampleIso(4, 4));
    isz[16] = 4;
    Assert.Throws<NotSupportedException>(() => IszImage.Read(new MemoryStream(isz)));
    Assert.That(new IszFormatDescriptor().List(new MemoryStream(isz), null).Single().Name, Is.EqualTo("FULL.isz"));
  }

  [Test]
  public void GivenASegmentedHeader_WhenRead_ThenNotSupported() {
    var isz = Write(SampleIso(4, 5));
    BinaryPrimitives.WriteUInt32LittleEndian(isz.AsSpan(39), 64);
    Assert.Throws<NotSupportedException>(() => IszImage.Read(new MemoryStream(isz)));
  }

  [Test]
  public void GivenATruncatedFile_WhenRead_ThenInvalidData() {
    var isz = Write(SampleIso(64, 6));
    Assert.Throws<InvalidDataException>(() => Read(isz[..^100]));
  }

  [Test]
  public void GivenNoSignature_WhenRead_ThenInvalidData()
    => Assert.Throws<InvalidDataException>(() => IszImage.Read(new MemoryStream(new byte[64])));
}

/// <summary>
/// isz-tool (GPL-3.0 Python, github.com/oserres/isz-tool, used as a black box) as the oracle: it must
/// verify both CRCs of what we write and extract the same ISO. Its own reader is the only
/// independent ISZ implementation available off Windows. The script path comes from
/// <c>CWB_ISZ_TOOL</c>; skipped when unset unless <c>CWB_REQUIRE_ISZ_TOOL=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class IszExternalConformanceTests {
  private static (string Python, string Script) RequireTool() {
    var script = Environment.GetEnvironmentVariable("CWB_ISZ_TOOL");
    var python = OperatingSystem.IsWindows() ? "python" : "python3";
    if (!string.IsNullOrEmpty(script) && File.Exists(script))
      return (python, script);
    const string hint = "isz-tool not configured: set CWB_ISZ_TOOL to isz-tool.py (https://github.com/oserres/isz-tool).";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_ISZ_TOOL") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return default;
  }

  private static (string Out, int Exit) Run(string python, params string[] args) {
    var psi = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    p.WaitForExit();
    TestContext.Out.WriteLine($"{string.Join(' ', args)} -> {p.ExitCode}\n{output}");
    return (output, p.ExitCode);
  }

  [TestCase(1)]
  [TestCase(100)]
  [TestCase(700)]
  public void GivenOurIsz_WhenIszToolVerifiesAndExtracts_ThenBothCrcsPassAndTheIsoMatches(int sectors) {
    var (python, script) = RequireTool();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-iszx-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var iso = IszTests.SampleIso(sectors, sectors + 9);
      var isz = Path.Combine(dir, "ours.isz");
      using (var fs = File.Create(isz)) IszImage.Write(fs, iso);

      var verify = Run(python, script, "verify", "--slow", isz);
      Assert.That(verify.Exit, Is.EqualTo(0));
      Assert.That(verify.Out, Does.Not.Contain("ERROR"));
      Assert.That(verify.Out.Split("PASS").Length - 1, Is.EqualTo(2), "both the packed and the decoded CRC must pass");

      var outIso = Path.Combine(dir, "theirs.iso");
      Assert.That(Run(python, script, "isz2iso", isz, outIso).Exit, Is.EqualTo(0));
      Assert.That(File.ReadAllBytes(outIso), Is.EqualTo(iso));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
