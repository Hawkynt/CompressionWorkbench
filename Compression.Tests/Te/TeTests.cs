using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Analysis.ExternalTools;
using FileFormat.Te;

namespace Compression.Tests.Te;

/// <summary>
/// Terse Executable images. Fixtures are real PEI modules carved from Debian's EDK2 firmware; the
/// oracle is 7-Zip's TE handler, whose view of each fixture is pinned in <c>7zip-listing.txt</c>
/// and re-checked live when 7-Zip is installed. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class TeTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TeVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded TE vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<string[]> Listing(string file)
    => System.Text.Encoding.ASCII.GetString(Vector("7zip-listing.txt"))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(l => l.Split(' '))
      .Where(l => l[0] == file)
      .ToList();

  [TestCase("aavmf-aarch64-CpuPei.te", "ARM64")]
  [TestCase("aavmf-aarch64-PlatformPei.te", "ARM64")]
  [TestCase("aavmf-arm-CpuPei.te", "ARM-Thumb")]
  [Category("ThirdPartyVector")]
  public void Given_EdkTeImage_When_Read_Then_MatchesSevenZipListing(string file, string machine) {
    var image = Vector(file);
    var r = new TeReader(image);
    var expected = Listing(file);

    Assert.That(r.MachineName, Is.EqualTo(machine));
    Assert.That(r.SubsystemName, Is.EqualTo("EFI Boot"));
    Assert.That(r.ImageSize, Is.EqualTo(image.Length));
    Assert.That(r.Sections.Select(s => s.Name), Is.EqualTo(expected.Select(l => l[1])));
    for (var i = 0; i < r.Sections.Count; ++i) {
      var s = r.Sections[i];
      Assert.That(s.Size, Is.EqualTo(long.Parse(expected[i][2])), s.Name);
      Assert.That(s.PhysicalSize, Is.EqualTo(long.Parse(expected[i][3])), s.Name);
      Assert.That(s.Offset, Is.EqualTo(long.Parse(expected[i][4])), s.Name);
      Assert.That(Convert.ToHexStringLower(SHA256.HashData(image.AsSpan((int)s.Offset, (int)s.Size))), Is.EqualTo(expected[i][5]), s.Name);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_EdkTeImage_When_ExtractedThroughDescriptor_Then_FilesAreTheSections() {
    var image = Vector("aavmf-arm-CpuPei.te");
    var d = new TeFormatDescriptor();
    Assert.That(d.List(new MemoryStream(image), null).Select(e => e.Name), Is.EqualTo(new[] { ".text", ".data", ".reloc" }));
    var dir = Path.Combine(Path.GetTempPath(), "te_" + Guid.NewGuid().ToString("N"));
    try {
      d.Extract(new MemoryStream(image), dir, null, [".data"]);
      Assert.That(Directory.GetFiles(dir).Select(Path.GetFileName), Is.EqualTo(new[] { ".data" }));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, ".data")), Is.EqualTo(image.AsSpan(3856, 320).ToArray()));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("ExternalTool")]
  public void Given_SevenZip_When_ItListsTheFixtures_Then_ItStillAgreesWithThePinnedListing() {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null) Assert.Ignore("7-Zip not found.");
    foreach (var file in new[] { "aavmf-aarch64-CpuPei.te", "aavmf-arm-CpuPei.te" }) {
      var path = Path.Combine(Path.GetTempPath(), "te_" + Guid.NewGuid().ToString("N") + ".te");
      File.WriteAllBytes(path, Vector(file));
      try {
        var psi = new ProcessStartInfo(sevenZip, ["l", "-slt", "-tTE", path]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var paths = output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("Path = .", StringComparison.Ordinal)).Select(l => l[7..]);
        Assert.That(paths, Is.EqualTo(new TeReader(Vector(file)).Sections.Select(s => s.Name)), file);
      } finally {
        File.Delete(path);
      }
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_WrongSignature_When_Read_Then_InvalidData() {
    var bytes = Vector("aavmf-arm-CpuPei.te");
    bytes[1] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownMachine_When_Read_Then_InvalidData() {
    var bytes = Vector("aavmf-arm-CpuPei.te");
    bytes[2] = 0x34; bytes[3] = 0x12;
    Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
  }

  [TestCase((byte)4)]   // a hole in the subsystem table
  [TestCase((byte)18)]  // past its end
  [Category("EdgeCase")]
  public void Given_UnknownSubsystem_When_Read_Then_InvalidData(byte subsystem) {
    var bytes = Vector("aavmf-arm-CpuPei.te");
    bytes[5] = subsystem;
    Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
  }

  [Test, Category("Boundary")]
  public void Given_SectionCountAtTheLimit_When_Read_Then_ThirtyTwoPassTheHeaderCheckAndThirtyThreeDoNot() {
    var bytes = new byte[TeReader.HeaderSize + 32 * 40];
    bytes[0] = (byte)'V'; bytes[1] = (byte)'Z'; bytes[2] = 0x4C; bytes[3] = 0x01; bytes[5] = 10;
    bytes[4] = 32;
    // 32 all-zero section headers: rejected per section (raw data inside the headers), not for the count
    var ex = Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
    Assert.That(ex!.Message, Does.Not.Contain("at most 32"));
    bytes[4] = 33;
    ex = Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
    Assert.That(ex!.Message, Does.Contain("at most 32"));
  }

  [Test, Category("EdgeCase")]
  public void Given_SectionStartingInsideStrippedHeaders_When_Read_Then_InvalidData() {
    var bytes = Vector("aavmf-arm-CpuPei.te");
    // first section header at 40; PointerToRawData at +20 — point it below StrippedSize (0x178)
    bytes[40 + 20] = 0x10; bytes[40 + 21] = 0x00;
    Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedImage_When_Read_Then_InvalidData() {
    var bytes = Vector("aavmf-arm-CpuPei.te")[..4000];
    Assert.Throws<InvalidDataException>(() => _ = new TeReader(bytes));
  }

  [Test, Category("Boundary")]
  public void Given_HeaderOnly_When_Read_Then_InvalidDataRatherThanAnIndexError() {
    Assert.Throws<InvalidDataException>(() => _ = new TeReader("VZ"u8.ToArray()));
    Assert.Throws<InvalidDataException>(() => _ = new TeReader([]));
  }

  [Test, Category("Boundary")]
  public void Given_ZeroSections_When_Read_Then_EmptyListing() {
    var bytes = Vector("aavmf-arm-CpuPei.te")[..TeReader.HeaderSize];
    bytes[4] = 0;
    var r = new TeReader(bytes);
    Assert.That(r.Sections, Is.Empty);
    Assert.That(r.ImageSize, Is.EqualTo(TeReader.HeaderSize));
  }
}
