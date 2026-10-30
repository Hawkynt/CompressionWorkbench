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
