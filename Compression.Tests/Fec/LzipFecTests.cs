using System.Reflection;
using System.Text;
using FileFormat.LzipFec;

namespace Compression.Tests.Fec;

/// <summary>
/// lziprecover fec files. Both fixtures were written by lziprecover 1.25 for the same 5000-byte file
/// (GF(2^8) with 8 blocks, GF(2^16) with 3); <c>lziprecover -Fl -v</c>'s report of each is pinned here. See
/// ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class LzipFecTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FecVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded fec vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // lziprecover -Fl -v: "Protected size: 5000  Block size: 512  Data blocks: 10", "Fec blocks: 8|3",
  // "Fec numbers: 0-7|0-2", "Features: GF(2^8)|GF(2^16) CRC32 CRC32-C"; md5sum of the file 77441b2e….
  [TestCase("sample-gf8.fec", false, 8)]
  [TestCase("sample-gf16.fec", true, 3)]
  [Category("ThirdPartyVector")]
  public void Given_LziprecoverFec_When_Read_Then_MatchesLziprecoverListing(string vector, bool gf16, int fecBlocks) {
    var f = LzipFecFormatDescriptor.Read(Vector(vector));
    Assert.That((f.ProtectedSize, f.FecBlockSize, f.DataBlocks, f.Gf16), Is.EqualTo((5000L, 512L, 10L, gf16)));
    Assert.That(f.Blocks.Select(b => b.Number), Is.EqualTo(Enumerable.Range(0, fecBlocks)));
    Assert.That(f.Blocks.All(b => b.Data.Length == 512), Is.True);
    Assert.That(f.Crc32Cs, Is.Not.Null.And.Length.EqualTo(10), "CRC32-C feature");
    Assert.That(Convert.ToHexStringLower(f.ProtectedMd5), Is.EqualTo("77441b2e36f615b42cc509c78d0cb208"));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Fec_When_Listed_Then_MetadataCrcArraysAndBlocks() {
    var list = new LzipFecFormatDescriptor().List(new MemoryStream(Vector("sample-gf16.fec")), null).Select(e => e.Name).ToList();
    Assert.That(list, Is.EqualTo(new[] { "metadata.ini", "crc32.bin", "crc32c.bin", "fec_00000.bin", "fec_00001.bin", "fec_00002.bin" }));
  }

  [TestCase((ushort)0x0001, 512L)]
  [TestCase((ushort)0x0008, 4096L)]
  [TestCase((ushort)(31 << 11 | 2047), 2047L << 40)]
  [Category("Boundary")]
  public void Given_CodedBlockSize_When_Decoded_Then_MantissaTimesTwoToTheExponentPlusNine(ushort coded, long size)
    => Assert.That(LzipFecFormatDescriptor.DecodeFbs(coded), Is.EqualTo(size));

  [Test, Category("HappyPath")]
  public void Given_FecPath_When_Detected_Then_LzipFec() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_fec_{Guid.NewGuid():N}.fec");
    File.WriteAllBytes(path, Vector("sample-gf8.fec"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("LzipFec"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedFecBlock_When_Read_Then_PayloadCrcRefuses() {
    var bytes = Vector("sample-gf8.fec");
    bytes[^10] ^= 0x01;
    Assert.Throws<InvalidDataException>(() => LzipFecFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedChksumHeader_When_Read_Then_HeaderCrcRefuses() {
    var bytes = Vector("sample-gf8.fec");
    bytes[8] ^= 0x01; // protected size
    Assert.Throws<InvalidDataException>(() => LzipFecFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongMagicOrTruncation_When_Read_Then_InvalidData() {
    var bytes = Vector("sample-gf8.fec");
    Assert.Throws<InvalidDataException>(() => LzipFecFormatDescriptor.Read(bytes[..39]));
    Assert.Throws<InvalidDataException>(() => LzipFecFormatDescriptor.Read(bytes[..^5]));
    bytes[0] = 0;
    Assert.Throws<InvalidDataException>(() => LzipFecFormatDescriptor.Read(bytes));
  }

  private static byte[] Sample() => [.. Enumerable.Range(0, 5000).Select(i => (byte)((i * 13 + (i >> 8)) % 256))];

  // the README's commands: lziprecover -Fc -b 512 (8 blocks, GF(2^8)) and -Fc3 --gf16
  [TestCase("sample-gf8.fec", 8, false)]
  [TestCase("sample-gf16.fec", 3, true)]
  [Category("ThirdPartyVector")]
  public void Given_TheSampleFile_When_OurFecIsCreated_Then_ItIsByteForByteLziprecovers(string vector, int blocks, bool gf16)
    => Assert.That(LzipFecWriter.Create(Sample(), blocks, 512, gf16), Is.EqualTo(Vector(vector)));

  [TestCase(1, 1, 0, null), TestCase(511, 2, 0, null), TestCase(512, 1, 0, null), TestCase(513, 3, 0, null),
   TestCase(100_000, 9, 0, null), TestCase(100_000, 9, 0, true), TestCase(70_000, 200, 512, null), TestCase(1_000_000, 4, 8192, false)]
  [Category("RoundTrip")]
  public void Given_AFile_When_ItsFecIsCreated_Then_ItReadsBackDescribingIt(int size, int blocks, long blockSize, bool? gf16) {
    var data = new byte[size];
    new Random(size).NextBytes(data);
    var f = LzipFecFormatDescriptor.Read(LzipFecWriter.Create(data, blocks, blockSize, gf16));
    Assert.That(f.ProtectedSize, Is.EqualTo(size));
    Assert.That(f.Blocks.Select(b => b.Number), Is.EqualTo(Enumerable.Range(0, blocks)));
    Assert.That(f.ProtectedMd5, Is.EqualTo(System.Security.Cryptography.MD5.HashData(data)));
    Assert.That(f.Crc32Cs, Is.Not.Null);
    if (gf16 != null) Assert.That(f.Gf16, Is.EqualTo(gf16.Value));
    if (blocks > 128) Assert.That(f.Gf16, Is.True);
  }

  [TestCase(512L, (ushort)0x0001), TestCase(4096L, (ushort)0x0008), TestCase(2047L * 512, (ushort)2047), TestCase(2048L * 512, (ushort)(1 << 11 | 1024))]
  [Category("Boundary")]
  public void Given_ABlockSize_When_Encoded_Then_LargestMantissaSmallestExponent(long size, ushort coded) {
    Assert.That(LzipFecWriter.Encode(size), Is.EqualTo(coded));
    Assert.That(LzipFecFormatDescriptor.DecodeFbs(coded), Is.EqualTo(size));
  }

  [Test, Category("Boundary")]
  public void Given_AnUnrepresentableBlockSize_When_Rounded_Then_TheNextStorableOne()
    => Assert.That(LzipFecWriter.Representable(2049L * 512), Is.EqualTo(2050L * 512));

  [Test, Category("Exception")]
  public void Given_BadParameters_When_Created_Then_Refused() {
    Assert.Throws<ArgumentException>(() => LzipFecWriter.Create([]));
    Assert.Throws<ArgumentOutOfRangeException>(() => LzipFecWriter.Create([1], 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => LzipFecWriter.Create([1], 2049));
    Assert.Throws<ArgumentOutOfRangeException>(() => LzipFecWriter.Create([1], 1, 1000));
    Assert.Throws<ArgumentException>(() => LzipFecWriter.Create(new byte[200 * 512], 1, 512, gf16: false));
  }

  [Test, Category("HappyPath")]
  public void Given_TwoFiles_When_Created_Then_Refused()
    => Assert.Throws<NotSupportedException>(() => new LzipFecFormatDescriptor().Create(new MemoryStream(),
      [Compression.Registry.ArchiveInputInfo.InMemory("a", [1]), Compression.Registry.ArchiveInputInfo.InMemory("b", [2])], new Compression.Registry.FormatCreateOptions()));

  [Test, Category("Boundary")]
  public void Given_TheWriteConstraints_When_Asked_Then_OneFileIsAcceptedAndADirectoryIsNot() {
    var constraints = (Compression.Registry.IArchiveWriteConstraints)new LzipFecFormatDescriptor();
    Assert.That(constraints.CanAccept(Compression.Registry.ArchiveInputInfo.InMemory("a.lz", [1]), out _), Is.True);
    Assert.That(constraints.CanAccept(new Compression.Registry.ArchiveInputInfo("", "dir", true), out var reason), Is.False);
    Assert.That(reason, Does.Contain("exactly one file"));
  }

  [Test, Category("EndToEnd")]
  public void Given_OurFec_When_LziprecoverTestsAndRepairs_Then_TheFileComesBack() {
    var tool = Environment.GetEnvironmentVariable("CWB_LZIPRECOVER");
    if (string.IsNullOrEmpty(tool)) Assert.Ignore("Set CWB_LZIPRECOVER to lziprecover 1.25 or later (1.24 has no fec support).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_fec_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = new byte[300_000];
      new Random(8).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "d.bin"), data);
      using (var fec = File.Create(Path.Combine(dir, "d.bin.fec")))
        new LzipFecFormatDescriptor().Create(fec, [Compression.Registry.ArchiveInputInfo.InMemory("d.bin", data)],
          new Compression.Registry.FormatCreateOptions("gf16") { FormatSpecific = { ["FecBlocks"] = "6", ["BlockSize"] = "4096" } });
      var damaged = data.ToArray();
      Array.Clear(damaged, 5000, 9000);     // three data blocks
      damaged[250_000] ^= 0x40;              // and a fourth
      File.WriteAllBytes(Path.Combine(dir, "bad.bin"), damaged);
      var w = FsInteropToolbox.WinToWsl(dir);
      var bin = tool.StartsWith('/') ? tool : FsInteropToolbox.WinToWsl(tool);
      var run = FsInteropToolbox.RunWsl($"cd {w} && {bin} -Ft d.bin && echo TESTED && {bin} -Fr --fec-file=d.bin.fec bad.bin && echo REPAIRED");
      Assert.That(run.StdOut, Does.Contain("TESTED").And.Contain("REPAIRED"), run.StdOut + run.StdErr);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "bad.bin_fixed")), Is.EqualTo(data));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EndToEnd")]
  public void Given_FreshFecFromLziprecover_When_Read_Then_SameSizeMd5AndBlockCount() {
    var tool = Environment.GetEnvironmentVariable("CWB_LZIPRECOVER");
    if (string.IsNullOrEmpty(tool)) Assert.Ignore("Set CWB_LZIPRECOVER to lziprecover 1.25 or later (1.24 has no fec support).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_fec_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = new byte[12345];
      new Random(3).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "d.bin"), data);
      var w = FsInteropToolbox.WinToWsl(dir);
      var bin = tool.StartsWith('/') ? tool : FsInteropToolbox.WinToWsl(tool);
      var run = FsInteropToolbox.RunWsl($"cd {w} && {bin} -Fc5 -b 1024 d.bin 2>&1; md5sum d.bin");
      var f = LzipFecFormatDescriptor.Read(File.ReadAllBytes(Path.Combine(dir, "d.bin.fec")));
      Assert.That((f.ProtectedSize, f.FecBlockSize, f.Blocks.Count), Is.EqualTo((12345L, 1024L, 5)), run.StdOut);
      Assert.That(Convert.ToHexStringLower(f.ProtectedMd5), Is.EqualTo(run.StdOut.Trim().Split('\n')[^1].Split(' ')[0]));
    } finally { Directory.Delete(dir, true); }
  }
}
