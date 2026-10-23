using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Registry;
using FileFormat.Daa;

namespace Compression.Tests.Daa;

[TestFixture]
public class DaaTests {
  /// <summary>SHA-256 of the ISO PowerISO 1.3 (Linux) was given to produce <c>ReferenceVectors/small.daa</c>.</summary>
  internal const string SmallIsoSha256 = "806dd11243038abdb3e987694cd7cc951d1789e70cc2282e5aa3045c99bf1497";

  internal static string VectorPath(string name)
    => Path.Combine(Documentation.FilesystemReadmeIsCurrentTests.RepositoryRoot(), "Compression.Tests", "Daa", "ReferenceVectors", name);

  /// <summary>A real ISO 9660 image (PowerISO parses the filesystem) holding roughly <paramref name="payload"/> bytes.</summary>
  internal static byte[] RealIso(int payload, int seed) {
    var iso = new FileSystem.Iso.IsoWriter();
    var random = new byte[payload / 2];
    new Random(seed).NextBytes(random);
    iso.AddFile("RANDOM.BIN", random);
    iso.AddFile("TEXT.TXT", System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("compressible daa line ", payload / 44 + 1))));
    return iso.Build();
  }

  internal static byte[] SampleIso(int size, int seed) {
    var data = new byte[size];
    new Random(seed).NextBytes(data.AsSpan(0, size / 3));
    for (var i = 2 * size / 3; i < size; ++i) data[i] = (byte)"daa chunk text "[i % 15];
    return data;
  }

  private static byte[] Write(byte[] iso, int chunk = DaaImage.DefaultChunkSize) {
    using var ms = new MemoryStream();
    DaaImage.Write(ms, iso, chunk);
    return ms.ToArray();
  }

  private static byte[] Read(byte[] daa) {
    using var s = new MemoryStream(daa);
    using var o = new MemoryStream();
    DaaImage.Read(s).CopyIsoTo(s, o);
    return o.ToArray();
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(0x10000)]
  [TestCase(0x10001)]
  [TestCase(300_000)]
  public void GivenAnIso_WhenRoundTripped_ThenIdentical(int size) {
    var iso = SampleIso(size, size);
    var daa = Write(iso);
    Assert.That(daa.AsSpan(0, 4).SequenceEqual("DAA\0"u8), Is.True);
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(daa.AsSpan(0x14)), Is.EqualTo(0x100));
    Assert.That(BinaryPrimitives.ReadInt64LittleEndian(daa.AsSpan(0x30)), Is.EqualTo(daa.Length));
    Assert.That(Read(daa), Is.EqualTo(iso));
  }

  [Test]
  public void GivenThePowerIsoSample_WhenRead_ThenTheIsoMatchesWhatPowerIsoWasGiven() {
    var daa = File.ReadAllBytes(VectorPath("small.daa"));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(Read(daa))), Is.EqualTo(SmallIsoSha256));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndReplacing_ThenTheNewImageReads() {
    var path = Path.GetTempFileName();
    try {
      File.WriteAllBytes(path, SampleIso(100_000, 1));
      using var ms = new MemoryStream();
      var d = new DaaFormatDescriptor();
      d.Create(ms, [new ArchiveInputInfo(path, "image.iso", false)], new FormatCreateOptions());
      var replacement = SampleIso(200_000, 2);
      File.WriteAllBytes(path, replacement);
      d.Add(ms, [new ArchiveInputInfo(path, "image.iso", false)]);
      Assert.That(Read(ms.ToArray()), Is.EqualTo(replacement));
    } finally {
      File.Delete(path);
    }
  }

  [Test]
  public void GivenABadHeaderCrc_WhenRead_ThenInvalidDataAndListFallsBack() {
    var daa = Write(SampleIso(5000, 3));
    daa[0x20] ^= 1;
    Assert.Throws<InvalidDataException>(() => DaaImage.Read(new MemoryStream(daa)));
    Assert.That(new DaaFormatDescriptor().List(new MemoryStream(daa), null).Single().Name, Is.EqualTo("FULL.daa"));
  }

  [Test]
  public void GivenVersion110_WhenRead_ThenNotSupported() {
    var daa = Write(SampleIso(5000, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(daa.AsSpan(0x14), 0x110);
    BinaryPrimitives.WriteUInt32LittleEndian(daa.AsSpan(0x48), Crc(daa.AsSpan(0, 0x48)));
    Assert.Throws<NotSupportedException>(() => DaaImage.Read(new MemoryStream(daa)));
  }

  [Test]
  public void GivenATruncatedFile_WhenRead_ThenNotSupportedAsSplit()
    => Assert.Throws<NotSupportedException>(() => DaaImage.Read(new MemoryStream(Write(SampleIso(5000, 5))[..^10])));

  [Test]
  public void GivenACorruptChunk_WhenRead_ThenInvalidData() {
    var daa = Write(SampleIso(200_000, 6));
    daa[^20] ^= 0x55;
    Assert.Catch(() => Read(daa));
  }

  [Test]
  public void GivenGbiOrVolumeSignatures_WhenRead_ThenNotSupported() {
    var daa = Write(SampleIso(100, 7));
    "GBI\0"u8.CopyTo(daa);
    Assert.Throws<NotSupportedException>(() => DaaImage.Read(new MemoryStream(daa)));
    "DAA VOL"u8.CopyTo(daa);
    Assert.Throws<NotSupportedException>(() => DaaImage.Read(new MemoryStream(daa)));
  }

  private static uint Crc(ReadOnlySpan<byte> data) {
    var crc = 0xFFFFFFFFu;
    foreach (var b in data) {
      crc ^= b;
      for (var k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
  }
}

/// <summary>
/// PowerISO for Linux 1.3 (freeware, used as a black box) as the oracle, both ways: it converts our
/// DAA back to the identical ISO, and we read the DAA it writes. The binary comes from
/// <c>CWB_POWERISO</c> (a WSL/Linux path); skipped when unset unless <c>CWB_REQUIRE_POWERISO=1</c>.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class DaaExternalConformanceTests {
  private static string RequirePowerIso() {
    var tool = Environment.GetEnvironmentVariable("CWB_POWERISO");
    if (!string.IsNullOrEmpty(tool) && FsInteropToolbox.WslAvailable)
      return tool;
    const string hint = "PowerISO for Linux not configured: set CWB_POWERISO to the poweriso binary (https://www.poweriso.com/poweriso-1.3.tar.gz).";
    if (Environment.GetEnvironmentVariable("CWB_REQUIRE_POWERISO") == "1") Assert.Fail(hint);
    Assert.Ignore(hint);
    return "";
  }

  private static (string StdOut, string StdErr, int ExitCode) Run(string command) {
    var r = FsInteropToolbox.RunWsl(command);
    TestContext.Out.WriteLine($"{command} -> {r.ExitCode}\n{r.StdOut}\n{r.StdErr}");
    return r;
  }

  [TestCase(2048)]
  [TestCase(147 * 2048)]
  [TestCase(489 * 2048)]
  public void GivenOurDaa_WhenPowerIsoConvertsItToIso_ThenTheIsoMatches(int size) {
    var tool = RequirePowerIso();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daax-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var iso = DaaTests.RealIso(size, size + 3);
      var daa = Path.Combine(dir, "ours.daa");
      using (var fs = File.Create(daa)) DaaImage.Write(fs, iso);
      var outIso = Path.Combine(dir, "theirs.iso");
      Run($"{tool} convert {FsInteropToolbox.WinToWsl(daa)} -o {FsInteropToolbox.WinToWsl(outIso)} -ot iso");
      Assert.That(File.Exists(outIso), Is.True, "PowerISO produced no ISO from our DAA");
      Assert.That(File.ReadAllBytes(outIso), Is.EqualTo(iso));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [TestCase(4096)]
  [TestCase(342 * 2048)]
  public void GivenAPowerIsoDaa_WhenWeReadIt_ThenTheIsoMatches(int size) {
    var tool = RequirePowerIso();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daax-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var iso = DaaTests.RealIso(size, size + 4);
      var isoPath = Path.Combine(dir, "source.iso");
      File.WriteAllBytes(isoPath, iso);
      var daa = Path.Combine(dir, "theirs.daa");
      Run($"{tool} convert {FsInteropToolbox.WinToWsl(isoPath)} -o {FsInteropToolbox.WinToWsl(daa)} -ot daa");
      using var s = File.OpenRead(daa);
      using var o = new MemoryStream();
      DaaImage.Read(s).CopyIsoTo(s, o);
      Assert.That(o.ToArray(), Is.EqualTo(iso));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
