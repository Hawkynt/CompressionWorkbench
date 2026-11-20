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

  // the README's image: 40 sectors, byte i = (i*7) ^ (i >> 11)
  private static byte[] TinyImage() => [.. Enumerable.Range(0, 81920).Select(i => (byte)((i * 7) ^ (i >> 11)))];

  // dvdisaster -i short.iso -e short-rs01.ecc -c -n 8: an image shorter than the fingerprint sector
  [Test, Category("ThirdPartyVector")]
  public void Given_AnImageShorterThanTheFingerprintSector_When_OurRs01FileIsCreated_Then_ItIsByteForByteDvdisasters() {
    var expected = Vector("short-rs01.ecc");
    var ours = DvdisasterEccWriter.CreateRs01(TinyImage()[..(10 * 2048)], 8, creatorVersion: 7910);
    ours[19] = expected[19];
    Assert.That(ours, Is.EqualTo(expected));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_TheTinyImage_When_OurRs01FileIsCreated_Then_ItIsByteForByteDvdisasters() {
    var expected = Vector("tiny.ecc");
    var ours = DvdisasterEccWriter.CreateRs01(TinyImage(), 8, creatorVersion: 7910);
    ours[19] = expected[19];   // dvdisaster's release flag: 1 for its development builds
    Assert.That(ours, Is.EqualTo(expected));
  }

  [TestCase(1, 8), TestCase(2047, 8), TestCase(2048, 32), TestCase(2049, 100), TestCase(16 * 2048 + 1, 32),
   TestCase(300_000, 40), TestCase(1_000_000, 64)]
  [Category("RoundTrip")]
  public void Given_AnImage_When_ItsRs01FileIsCreated_Then_ItReadsBackDescribingIt(int size, int roots) {
    var image = new byte[size];
    new Random(size).NextBytes(image);
    var ecc = DvdisasterEccWriter.CreateRs01(image, roots);
    var h = DvdisasterEccHeader.Read(ecc);
    Assert.That((h.Method, h.Sectors, h.EccBytes, h.DataBytes), Is.EqualTo(("RS01", (size + 2047L) / 2048, roots, 255 - roots)));
    Assert.That(h.MediumMd5, Is.EqualTo(System.Security.Cryptography.MD5.HashData(image)));
    Assert.That(h.NeededVersion, Is.EqualTo(size % 2048 == 0 ? 5500 : 6600));
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(ecc), Is.True);
    Assert.That(new DvdisasterEccFormatDescriptor().List(new MemoryStream(ecc), null), Has.Count.EqualTo(3));
  }

  [Test, Category("Boundary")]
  public void Given_TheWriteConstraints_When_Asked_Then_OneFileIsAcceptedAndADirectoryIsNot() {
    var constraints = (Compression.Registry.IArchiveWriteConstraints)new DvdisasterEccFormatDescriptor();
    Assert.That(constraints.CanAccept(Compression.Registry.ArchiveInputInfo.InMemory("disc.iso", [1]), out _), Is.True);
    Assert.That(constraints.CanAccept(new Compression.Registry.ArchiveInputInfo("", "dir", true), out var reason), Is.False);
    Assert.That(reason, Does.Contain("exactly one file"));
  }

  [Test, Category("Exception")]
  public void Given_BadParameters_When_Created_Then_Refused() {
    Assert.Throws<ArgumentException>(() => DvdisasterEccWriter.CreateRs01([]));
    Assert.Throws<ArgumentOutOfRangeException>(() => DvdisasterEccWriter.CreateRs01([1], 7));
    Assert.Throws<ArgumentOutOfRangeException>(() => DvdisasterEccWriter.CreateRs01([1], 101));
    Assert.Throws<ArgumentException>(() => DvdisasterEccWriter.CreateRs03([]));
    Assert.Throws<ArgumentOutOfRangeException>(() => DvdisasterEccWriter.CreateRs03([1], 7));
    Assert.Throws<ArgumentOutOfRangeException>(() => DvdisasterEccWriter.CreateRs03([1], 171));
    Assert.Throws<NotSupportedException>(() => new DvdisasterEccFormatDescriptor().Create(new MemoryStream(),
      [Compression.Registry.ArchiveInputInfo.InMemory("a.iso", [1])], new Compression.Registry.FormatCreateOptions("rs02")));
  }

  // ── RS03 ─────────────────────────────────────────────────────────────

  private static byte[] Image(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)((i * 7) ^ (i >> 11)))];

  // dvdisaster -i <image> -e <name> -c -mRS03 -o file -n<roots>r; see ReferenceVectors/README.md.
  [TestCase("tiny-rs03.ecc", 40 * 2048, 8)]
  [TestCase("odd-rs03.ecc", 1000 * 2048 + 333, 8)]
  [TestCase("short-rs03.ecc", 10 * 2048, 20)]
  [Category("ThirdPartyVector")]
  public void Given_AnImage_When_OurRs03FileIsCreated_Then_ItIsByteForByteDvdisasters(string vector, int size, int roots) {
    var ours = DvdisasterEccWriter.CreateRs03(Image(size), roots, creatorVersion: 7910, developmentBuild: true);
    Assert.That(ours, Is.EqualTo(Vector(vector)));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Rs03FileFromDvdisaster_When_Extracted_Then_CrcLayerParityAndMetadata() {
    var ecc = Vector("odd-rs03.ecc");
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(ecc), Is.True);
    var h = DvdisasterEccHeader.Read(ecc);
    Assert.That((h.Method, h.Sectors, h.EccBytes, h.DataBytes, h.NeededVersion), Is.EqualTo(("RS03", 1001L, 8, 247, 7900)));
    var files = Extract(ecc);
    Assert.That(files.Keys.Order(), Is.EqualTo(new[] { "crc-layer.bin", "metadata.ini", "parity.bin" }));
    Assert.That(files["crc-layer.bin"], Has.Length.EqualTo(5 * 2048));
    Assert.That(files["parity.bin"], Has.Length.EqualTo(8 * 5 * 2048));
  }

  [TestCase(96, TestName = "Damaged RS03 header")]
  [TestCase(4096 + 2048 + 10, TestName = "Damaged RS03 CRC block")]
  [Category("EdgeCase")]
  public void Given_DamagedRs03File_When_Listed_Then_RefusedByTheSelfCrc(int at) {
    var bytes = Vector("odd-rs03.ecc");
    bytes[at] ^= 0x01;
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(bytes), Is.False);
    Assert.Throws<InvalidDataException>(() => new DvdisasterEccFormatDescriptor().List(new MemoryStream(bytes), null));
  }

  [TestCase(1, 8), TestCase(2047, 170), TestCase(2048, 32), TestCase(17 * 2048, 100), TestCase(300_000, 40), TestCase(1_000_000, 64)]
  [Category("RoundTrip")]
  public void Given_AnImage_When_ItsRs03FileIsCreated_Then_ItReadsBackDescribingIt(int size, int roots) {
    var image = new byte[size];
    new Random(size).NextBytes(image);
    var ecc = DvdisasterEccWriter.CreateRs03(image, roots);
    var h = DvdisasterEccHeader.Read(ecc);
    var sectors = (size + 2047L) / 2048;
    var layer = (sectors + 255 - roots - 2) / (255 - roots - 1);
    Assert.That((h.Method, h.Sectors, h.EccBytes, h.DataBytes), Is.EqualTo(("RS03", sectors, roots, 255 - roots)));
    Assert.That(ecc, Has.Length.EqualTo(4096 + (roots + 1) * layer * 2048));
    Assert.That(DvdisasterEccFormatDescriptor.EccDataIntact(ecc), Is.True);
    Assert.That(new DvdisasterEccFormatDescriptor().List(new MemoryStream(ecc), null), Has.Count.EqualTo(3));
  }

  [Test, Category("EndToEnd")]
  public void Given_OurRs03File_When_DvdisasterTestsAndFixesTheImage_Then_TheImageComesBack() {
    if (!FsInteropToolbox.WslHasTool("dvdisaster")) Assert.Ignore("dvdisaster not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecc_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var img = new byte[2048 * 700 + 777];
      new Random(7).NextBytes(img);
      using (var ecc = File.Create(Path.Combine(dir, "img.ecc")))
        new DvdisasterEccFormatDescriptor().Create(ecc, [Compression.Registry.ArchiveInputInfo.InMemory("img.iso", img)],
          new Compression.Registry.FormatCreateOptions("rs03") { FormatSpecific = { ["Roots"] = "24" } });
      File.WriteAllBytes(Path.Combine(dir, "img.iso"), img);
      var damaged = img.ToArray();
      Array.Clear(damaged, 2048 * 10, 2048 * 5);   // five sectors gone
      damaged[2048 * 600 + 9] ^= 0x55;             // and one corrupted
      File.WriteAllBytes(Path.Combine(dir, "bad.iso"), damaged);
      var w = FsInteropToolbox.WinToWsl(dir);
      var run = FsInteropToolbox.RunWsl($"cd {w} && dvdisaster -i img.iso -e img.ecc -t 2>&1; echo TESTED=$?; dvdisaster -i bad.iso -e img.ecc -f 2>&1; echo FIXED=$?");
      // dvdisaster's RS03 repair exits with 1 once it has repaired sectors
      Assert.That(run.StdOut, Does.Contain("TESTED=0").And.Contain("All sectors are repaired"), run.StdOut + run.StdErr);
      Assert.That(run.StdOut, Does.Contain("all sectors present"), run.StdOut);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "bad.iso")), Is.EqualTo(img));
    } finally { Directory.Delete(dir, true); }
  }

  [Test, Category("EndToEnd")]
  public void Given_OurEccFile_When_DvdisasterTestsAndFixesTheImage_Then_TheImageComesBack() {
    if (!FsInteropToolbox.WslHasTool("dvdisaster")) Assert.Ignore("dvdisaster not in WSL.");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecc_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var img = new byte[2048 * 300 + 777];
      new Random(6).NextBytes(img);
      using (var ecc = File.Create(Path.Combine(dir, "img.ecc")))
        new DvdisasterEccFormatDescriptor().Create(ecc, [Compression.Registry.ArchiveInputInfo.InMemory("img.iso", img)],
          new Compression.Registry.FormatCreateOptions { FormatSpecific = { ["Roots"] = "24" } });
      File.WriteAllBytes(Path.Combine(dir, "img.iso"), img);
      var damaged = img.ToArray();
      Array.Clear(damaged, 2048 * 10, 2048 * 5);   // five sectors gone
      damaged[2048 * 200 + 9] ^= 0x55;             // and one corrupted
      File.WriteAllBytes(Path.Combine(dir, "bad.iso"), damaged);
      var w = FsInteropToolbox.WinToWsl(dir);
      var run = FsInteropToolbox.RunWsl($"cd {w} && dvdisaster -i img.iso -e img.ecc -t 2>&1; echo TESTED=$?; dvdisaster -i bad.iso -e img.ecc -f 2>&1; echo FIXED=$?");
      Assert.That(run.StdOut, Does.Contain("TESTED=0").And.Contain("FIXED=0"), run.StdOut + run.StdErr);
      Assert.That(run.StdOut, Does.Contain("- good").Or.Contain("good"), run.StdOut);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "bad.iso")), Is.EqualTo(img));
    } finally { Directory.Delete(dir, true); }
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
