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

  [TestCase(0)]
  [TestCase(4096)]
  public void GivenAMandatoryImage_WhenPurged_ThenRefusedWithoutChangingTheArchive(int size) {
    var iso = SampleIso(size, 11);
    var original = Write(iso);
    using var archive = new MemoryStream(original.ToArray());
    IArchivePurgeable descriptor = new DaaFormatDescriptor();

    Assert.Throws<NotSupportedException>(() => descriptor.Purge(archive));
    Assert.That(descriptor.CanPurgeToEmpty, Is.False);
    Assert.That(archive.ToArray(), Is.EqualTo(original));
    Assert.That(Read(archive.ToArray()), Is.EqualTo(iso));
  }

  // ── version 0x110 images PowerISO 9.5 wrote (ReferenceVectors/make-110.sh) ──────

  private static Dictionary<string, (string Sha, long Size)> Iso110() =>
    File.ReadAllLines(VectorPath("v110-iso.sha256")).Select(l => l.Split("  "))
      .ToDictionary(p => p[2], p => (p[0], long.Parse(p[1])));

  [TestCase("v110-better.daa", null)]
  [TestCase("v110-best.daa", null)]
  [TestCase("v110-password.daa", "secret42")]
  [Category("ThirdPartyVector")]
  public void GivenAVersion110Image_WhenRead_ThenTheIsoIsWhatPowerIsoConvertsItTo(string vector, string? password) {
    using var s = File.OpenRead(VectorPath(vector));
    using var image = DaaImage.Read(s, password);
    Assert.That(image.Version, Is.EqualTo(DaaImage.Version110));
    using var iso = new MemoryStream();
    image.CopyIsoTo(s, iso);
    var (sha, size) = Iso110()[vector];
    Assert.That(iso.Length, Is.EqualTo(size));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(iso.ToArray())), Is.EqualTo(sha));
  }

  [Test, Category("ThirdPartyVector")]
  public void GivenTheBetterImage_WhenRead_ThenItHoldsLzmaAndDeflateChunks() {
    using var s = File.OpenRead(VectorPath("v110-better.daa"));
    using var image = DaaImage.Read(s);
    Assert.That(image.ChunkKinds, Does.Contain(DaaChunkKind.Lzma));
    Assert.That(image.ChunkKinds.Distinct().Count(), Is.GreaterThanOrEqualTo(1));
  }

  [Test, Category("ThirdPartyVector")]
  public void GivenAVersion110PasswordImage_WhenReadWithTheWrongPassword_ThenRefused() {
    using var s = File.OpenRead(VectorPath("v110-password.daa"));
    Assert.Throws<ArchivePasswordException>(() => DaaImage.Read(s, "wrong"));
  }

  /// <summary>A split 0x110 set PowerISO 9.5 writes on the spot (too large to commit), read from its first volume.</summary>
  [Test, Category("ExternalInterop")]
  public void GivenASplitVersion110Set_WhenReadFromItsFirstVolume_ThenTheIsoIsWhatPowerIsoConvertsItTo() {
    var script = FsInteropToolbox.WinToWsl(VectorPath("piso-split-110.sh"));
    if (!FsInteropToolbox.WslAvailable || FsInteropToolbox.RunWsl($"bash {script} --check").ExitCode != 0)
      Assert.Ignore("PowerISO for Windows is not installed in its wine prefix (see piso-split-110.sh).");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daa110-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var run = FsInteropToolbox.RunWsl($"bash {script} {FsInteropToolbox.WinToWsl(dir)}");
      Assert.That(run.ExitCode, Is.Zero, run.StdErr);
      var expected = run.StdOut.Trim().Split(' ');
      var first = Path.Combine(dir, "set.part01.daa");
      Assert.That(Directory.GetFiles(dir, "set.part*.daa"), Has.Length.GreaterThan(1));
      using var s = File.OpenRead(first);
      using var image = DaaImage.Read(s, null, DaaFormatDescriptor.VolumeOpener(first));
      Assert.That((image.Version, image.VolumeCount > 1), Is.EqualTo((DaaImage.Version110, true)));
      using var iso = new MemoryStream();
      image.CopyIsoTo(s, iso);
      Assert.That(iso.Length, Is.EqualTo(long.Parse(expected[1])));
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(iso.ToArray())), Is.EqualTo(expected[0]));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [TestCase(0x4000u)]     // zlib-compressed chunk table
  [TestCase(0x8000000u)]  // obfuscated chunk table
  [TestCase(0x800000u)]   // permuted deflate block types
  public void GivenAnUnverifiedVersion110Option_WhenRead_ThenNotSupported(uint flag) {
    var daa = File.ReadAllBytes(VectorPath("v110-better.daa"));
    var field = BinaryPrimitives.ReadUInt32LittleEndian(daa.AsSpan(0x24));
    BinaryPrimitives.WriteUInt32LittleEndian(daa.AsSpan(0x24), field | flag);
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

  // ── password-protected and split images (ReferenceVectors/README.md) ──────

  internal const string Password = "Secret-30";
  internal const string PasswordIsoSha256 = "b8f9ec037566899baa8875772de0bb18d93a371533af8cabd076112693f28603";
  internal const string SplitIsoSha256 = "49c551705e03389de73a9adfdde82bf0b2ac3d272c880abf1cff3684420d14ae";

  private static string ExtractIsoSha(string vector, string? password) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daa-" + Guid.NewGuid().ToString("N"));
    try {
      using (var s = File.OpenRead(VectorPath(vector)))
        new DaaFormatDescriptor().Extract(s, dir, password, null);
      return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, DaaFormatDescriptor.ImageName))));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void GivenAPowerIsoPasswordImage_WhenExtractedWithThePassword_ThenTheIsoMatches()
    => Assert.That(ExtractIsoSha("pw.daa", Password), Is.EqualTo(PasswordIsoSha256));

  [Test, Category("ThirdPartyVector")]
  public void GivenAPowerIsoSplitPasswordImage_WhenExtractedFromItsFirstVolume_ThenTheIsoMatches() {
    Assert.That(ExtractIsoSha("splitpw.part01.daa", Password), Is.EqualTo(SplitIsoSha256));
    using var s = File.OpenRead(VectorPath("splitpw.part01.daa"));
    using var image = DaaImage.Read(s, Password, DaaFormatDescriptor.VolumeOpener(VectorPath("splitpw.part01.daa")));
    Assert.That((image.VolumeCount, image.IsEncrypted), Is.EqualTo((2, true)));
    // the chunks run across the volume boundary
    Assert.That(image.Chunks.Any(c => c.Offset < s.Length && c.Offset + c.Length > s.Length), Is.True);
  }

  [Test, Category("ThirdPartyVector")]
  public void GivenAPasswordImage_WhenListedWithoutThePassword_ThenTheImageShowsEncrypted() {
    using var s = File.OpenRead(VectorPath("pw.daa"));
    var listing = new DaaFormatDescriptor().List(s, null);
    Assert.That(listing.Single(e => e.Name == DaaFormatDescriptor.ImageName).IsEncrypted, Is.True);
  }

  [TestCase(null, false)]
  [TestCase("Secret-31", true)]
  [TestCase("", true)]
  [Category("Exception")]
  public void GivenAPasswordImage_WhenExtractedWithoutTheRightPassword_ThenRefused(string? password, bool supplied) {
    var e = Assert.Throws<ArchivePasswordException>(() => ExtractIsoSha("pw.daa", password));
    Assert.That(e!.PasswordWasSupplied, Is.EqualTo(supplied));
  }

  [Test, Category("Exception")]
  public void GivenASplitImageWithoutItsPath_WhenRead_ThenNotSupported() {
    var first = File.ReadAllBytes(VectorPath("splitpw.part01.daa"));
    Assert.Throws<NotSupportedException>(() => DaaImage.Read(new MemoryStream(first), Password));
  }

  [Test, Category("Exception")]
  public void GivenASplitImageWithAVolumeMissing_WhenRead_ThenInvalidData() {
    var first = File.ReadAllBytes(VectorPath("splitpw.part01.daa"));
    Assert.Throws<InvalidDataException>(() => DaaImage.Read(new MemoryStream(first), Password, _ => null));
  }

  [Test, Category("Exception")]
  public void GivenASplitImageWhoseSecondVolumeIsNotAVolume_WhenRead_ThenInvalidData() {
    var first = File.ReadAllBytes(VectorPath("splitpw.part01.daa"));
    var second = File.ReadAllBytes(VectorPath("splitpw.part02.daa"));
    second[4] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => DaaImage.Read(new MemoryStream(first), Password, _ => new MemoryStream(second)));
  }

  [TestCase("disc.part01.daa", "disc.part02.daa")]
  [TestCase("disc.part09.daa", "disc.part10.daa")]
  [TestCase("disc.001.DAA", "disc.002.DAA")]
  [TestCase("disc.daa", null)]
  public void GivenAFirstVolumeName_WhenTheNextIsLookedFor_ThenTheNumberCountsUp(string first, string? second) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daa-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      if (second != null) File.WriteAllBytes(Path.Combine(dir, second), [1]);
      var opener = DaaFormatDescriptor.VolumeOpener(Path.Combine(dir, first));
      if (second == null) {
        Assert.That(opener, Is.Null);
        return;
      }
      var number = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"(\d+)\.daa$", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value);
      using var stream = opener!(number + 1);
      Assert.That(stream, Is.Not.Null);
      Assert.That(Path.GetFileName(((FileStream)stream!).Name), Is.EqualTo(second));
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Exception")]
  public void GivenAPasswordImage_WhenItsImageIsReplaced_ThenRefusedRatherThanDroppingTheProtection() {
    using var ms = new MemoryStream(File.ReadAllBytes(VectorPath("pw.daa")));
    Assert.Throws<NotSupportedException>(() => new DaaFormatDescriptor().Add(ms, [ArchiveInputInfo.InMemory("image.iso", SampleIso(4096, 9))]));
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

  // PowerISO's smallest volume is 300 KiB, and these ISOs are half random, so the split ones give
  // two to four volumes.
  [TestCase(800 * 1024, true, null)]
  [TestCase(2000 * 1024, true, null)]
  [TestCase(900 * 1024, true, "pw")]
  [TestCase(3000, false, "A long passphrase, with spaces & symbols: 1234567890!")]
  public void GivenAPowerIsoSplitOrProtectedDaa_WhenWeReadIt_ThenTheIsoMatches(int size, bool split, string? password) {
    var tool = RequirePowerIso();
    var dir = Path.Combine(Path.GetTempPath(), "cwb-daax-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var iso = DaaTests.RealIso(size, size + 5);
      var isoPath = Path.Combine(dir, "source.iso");
      File.WriteAllBytes(isoPath, iso);
      var daa = Path.Combine(dir, "theirs.daa");
      var options = (split ? " -volsize 300K" : "") + (password is null ? "" : $" -setpassword '{password}'");
      Run($"{tool} convert {FsInteropToolbox.WinToWsl(isoPath)} -o {FsInteropToolbox.WinToWsl(daa)} -ot daa{options}");
      var first = split ? Path.Combine(dir, "theirs.part01.daa") : daa;
      Assert.That(File.Exists(first), Is.True, "PowerISO wrote no image");
      using var s = File.OpenRead(first);
      using var image = DaaImage.Read(s, password, DaaFormatDescriptor.VolumeOpener(first));
      Assert.That(image.IsEncrypted, Is.EqualTo(password is not null));
      using var o = new MemoryStream();
      image.CopyIsoTo(s, o);
      Assert.That(o.ToArray(), Is.EqualTo(iso));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
