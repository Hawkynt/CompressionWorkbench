using System.Reflection;
using System.Text;
using FileFormat.DvdisasterEcc;

namespace Compression.Tests.Ecc;

/// <summary>
/// dvdisaster error-correction files. The fixture was written by dvdisaster 0.79.10 (RS01, 8 roots) for a
/// 40-sector test image; dvdisaster's own <c>-t</c> report of it is pinned here. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class DvdisasterEccTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EccVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded ecc vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> Extract(byte[] ecc) {
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecc_{Guid.NewGuid():N}");
    try {
      new DvdisasterEccFormatDescriptor().Extract(new MemoryStream(ecc), dir, null, null);
      return Directory.GetFiles(dir).ToDictionary(f => Path.GetFileName(f), File.ReadAllBytes);
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Rs01FileFromDvdisaster_When_HeaderRead_Then_MatchesDvdisasterReport() {
    var h = DvdisasterEccHeader.Read(Vector("tiny.ecc"));
    // dvdisaster -t: "created by dvdisaster-0.79.10", "requires dvdisaster-0.55", "medium sectors: 40",
    // "image md5sum: fc0a12726edaa590185c0b3488e17e82", 8 roots.
    Assert.That((h.Method, h.Sectors, h.EccBytes, h.DataBytes), Is.EqualTo(("RS01", 40L, 8, 247)));
    Assert.That(DvdisasterEccHeader.Version(h.CreatorVersion), Is.EqualTo("0.79.10"));
    Assert.That(DvdisasterEccHeader.Version(h.NeededVersion), Is.EqualTo("0.55"));
    Assert.That(Convert.ToHexStringLower(h.MediumMd5), Is.EqualTo("fc0a12726edaa590185c0b3488e17e82"));
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(Vector("tiny.ecc")), Is.True);
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Rs01File_When_Extracted_Then_CrcTableParityAndMetadata() {
    var files = Extract(Vector("tiny.ecc"));
    Assert.That(files.Keys.Order(), Is.EqualTo(new[] { "metadata.ini", "parity.bin", "sector-crc32.bin" }));
    Assert.That(files["sector-crc32.bin"], Has.Length.EqualTo(40 * 4));
    Assert.That(files["parity.bin"], Has.Length.EqualTo(8 * 2048));
    Assert.That(Encoding.UTF8.GetString(files["metadata.ini"]), Does.Contain("image_md5=fc0a12726edaa590185c0b3488e17e82"));
  }

  [Test, Category("HappyPath")]
  public void Given_EccPath_When_Detected_Then_DvdisasterEcc() {
    var path = Path.Combine(Path.GetTempPath(), $"cwb_ecc_{Guid.NewGuid():N}.ecc");
    File.WriteAllBytes(path, Vector("tiny.ecc"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("DvdisasterEcc"));
    } finally { File.Delete(path); }
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedParity_When_Listed_Then_RefusedByTheEccMd5() {
    var bytes = Vector("tiny.ecc");
    bytes[^1] ^= 0xFF;
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(bytes), Is.False);
    Assert.Throws<InvalidDataException>(() => new DvdisasterEccFormatDescriptor().List(new MemoryStream(bytes), null));
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongCookie_When_Read_Then_InvalidData() {
    var bytes = Vector("tiny.ecc");
    bytes[0] = (byte)'#';
    Assert.Throws<InvalidDataException>(() => DvdisasterEccHeader.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedFile_When_Read_Then_InvalidData()
    => Assert.Throws<InvalidDataException>(() => DvdisasterEccHeader.Read(Vector("tiny.ecc")[..4000]));

  [Test, Category("EdgeCase")]
  public void Given_SectorCountThatDoesNotFit_When_Listed_Then_InvalidData() {
    var bytes = Vector("tiny.ecc");
    BitConverter.GetBytes(41L).CopyTo(bytes, 68); // header now claims 41 sectors (the MD5 covers only the body)
    Assert.Throws<InvalidDataException>(() => new DvdisasterEccFormatDescriptor().List(new MemoryStream(bytes), null));
  }

  [Test, Category("EndToEnd")]
  public void Given_ImageAndOurReading_When_DvdisasterVerifies_Then_SameMd5AndSectors() {
    if (!FsInteropToolbox.WslHasTool("dvdisaster")) Assert.Ignore("dvdisaster not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecc_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var img = new byte[2048 * 23];
      new Random(5).NextBytes(img);
      File.WriteAllBytes(Path.Combine(dir, "img.iso"), img);
      var w = FsInteropToolbox.WinToWsl(dir);
      var run = FsInteropToolbox.RunWsl($"cd {w} && dvdisaster -i img.iso -e img.ecc -c -n 16 >/dev/null 2>&1; md5sum img.iso");
      var md5 = run.StdOut.Split(' ')[0];
      var h = DvdisasterEccHeader.Read(File.ReadAllBytes(Path.Combine(dir, "img.ecc")));
      Assert.That((h.Sectors, h.EccBytes, Convert.ToHexStringLower(h.MediumMd5)), Is.EqualTo((23L, 16, md5)));
      Assert.That(new DvdisasterEccFormatDescriptor().List(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, "img.ecc"))), null), Has.Count.EqualTo(3));
    } finally { Directory.Delete(dir, true); }
  }
}
