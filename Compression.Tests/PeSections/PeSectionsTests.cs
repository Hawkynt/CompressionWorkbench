using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Compression.Analysis.ExternalTools;
using FileFormat.PeSections;

namespace Compression.Tests.PeSections;

/// <summary>
/// PE section view. Fixtures were linked by MinGW-w64; the oracle is 7-Zip's PE handler, whose view
/// is pinned in <c>7zip-listing.txt</c> and re-checked live against 7-Zip on the fixtures and on
/// the host's own system DLLs when both are present. See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class PeSectionsTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"PeSectionsVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded PE vector '{name}' is missing from the test assembly.");
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

  private static string Sha(byte[] image, PeSectionItem s)
    => Convert.ToHexStringLower(SHA256.HashData(image.AsSpan((int)s.Offset, (int)s.Size)));

  [TestCase("mingw-tiny.exe")]
  [TestCase("mingw-tiny-symbols.exe")]
  [TestCase("mingw-tiny-certificate-overlay.exe")]
  [Category("ThirdPartyVector")]
  public void Given_LinkerOutput_When_Read_Then_MatchesSevenZipListing(string file) {
    var image = Vector(file);
    var expected = Listing(file);
    // 7-Zip expands .rsrc into a resource tree and does not list the overlay; everything else must agree.
    var ours = new PeSectionReader(image).Items.Where(s => s.Name != ".rsrc" && s.Kind != PeSectionKind.Overlay).ToList();
    Assert.That(ours.Select(s => s.Name), Is.EqualTo(expected.Select(l => l[1])));
    for (var i = 0; i < ours.Count; ++i) {
      Assert.That(ours[i].Size, Is.EqualTo(long.Parse(expected[i][2])), ours[i].Name);
      Assert.That(ours[i].PhysicalSize, Is.EqualTo(long.Parse(expected[i][3])), ours[i].Name);
      Assert.That(ours[i].Offset, Is.EqualTo(long.Parse(expected[i][4])), ours[i].Name);
      Assert.That(Sha(image, ours[i]), Is.EqualTo(expected[i][5]), ours[i].Name);
    }
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_ResourceSection_When_Read_Then_RawSectionIsKept() {
    var image = Vector("mingw-tiny.exe");
    var rsrc = new PeSectionReader(image).Items.Single(s => s.Name == ".rsrc");
    Assert.That(rsrc.Kind, Is.EqualTo(PeSectionKind.Section));
    // IMAGE_RESOURCE_DIRECTORY: Characteristics 0, then one id entry (RT_VERSION = 16)
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)rsrc.Offset + 14)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan((int)rsrc.Offset + 16)), Is.EqualTo(16));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_AppendedBytes_When_Read_Then_TheyBecomeTheOverlayEntry() {
    var image = Vector("mingw-tiny-certificate-overlay.exe");
    var r = new PeSectionReader(image);
    var overlay = r.Items.Single(s => s.Kind == PeSectionKind.Overlay);
    Assert.That(overlay.Name, Is.EqualTo("[overlay]"));
    Assert.That(image.AsSpan((int)overlay.Offset, (int)overlay.Size).ToArray(), Is.EqualTo("OVERLAY!"u8.ToArray()));
    Assert.That(r.ImageSize, Is.EqualTo(image.Length - 8));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_Image_When_ListedAndExtractedThroughDescriptor_Then_EntriesMatchTheReader() {
    var image = Vector("mingw-tiny-symbols.exe");
    var d = new PeSectionsFormatDescriptor();
    var list = d.List(new MemoryStream(image), null);
    Assert.That(list.Select(e => e.Name), Is.EqualTo(new PeSectionReader(image).Items.Select(s => s.Name)));
    var dir = Path.Combine(Path.GetTempPath(), "pes_" + Guid.NewGuid().ToString("N"));
    try {
      d.Extract(new MemoryStream(image), dir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "COFF_SYMBOLS")).Length, Is.EqualTo(2555));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, ".text")).Length, Is.EqualTo(32));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Given_SectionStartingPastTheHeaderPage_When_Read_Then_TheHoleBecomesAGapEntry() {
    var image = Vector("mingw-tiny.exe");
    var grown = new byte[image.Length + 0x2000];
    image.CopyTo(grown, 0);
    var r0 = new PeSectionReader(image);
    // move the last section (.rsrc at 0x1000) to 0x3000: [0x1000, 0x3000) is now unclaimed
    var pe = BinaryPrimitives.ReadInt32LittleEndian(grown.AsSpan(0x3C));
    var optSize = BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(pe + 20));
    var count = BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(pe + 6));
    var last = pe + 24 + optSize + (count - 1) * 40;
    var oldPa = BinaryPrimitives.ReadInt32LittleEndian(grown.AsSpan(last + 20));
    Assert.That(oldPa, Is.EqualTo(0x1000), "fixture layout changed");
    var size = BinaryPrimitives.ReadInt32LittleEndian(grown.AsSpan(last + 16));
    grown.AsSpan(oldPa, size).CopyTo(grown.AsSpan(0x3000));
    grown.AsSpan(oldPa, 0x2000).Clear();
    BinaryPrimitives.WriteInt32LittleEndian(grown.AsSpan(last + 20), 0x3000);
    var r = new PeSectionReader(grown.AsSpan(0, 0x3000 + size));
    var gap = r.Items.Single(s => s.Kind == PeSectionKind.Gap);
    Assert.That((gap.Name, gap.Offset, gap.Size), Is.EqualTo(("[0]", 0x1000L, 0x2000L)));
    Assert.That(r.Items.Count, Is.EqualTo(r0.Items.Count + 1));
  }

  [Test, Category("EdgeCase")]
  public void Given_MzWithoutPeHeader_When_Read_Then_InvalidData() {
    var bytes = Vector("mingw-tiny.exe");
    var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
    bytes[pe] = (byte)'N';
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader(bytes));
  }

  [TestCase(0x3Fu)]
  [TestCase(0x1001u)]
  [Category("Boundary")]
  public void Given_PeOffsetOutsideTheAcceptedRange_When_Read_Then_InvalidData(uint lfanew) {
    var bytes = Vector("mingw-tiny.exe");
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), lfanew);
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_UnknownOptionalHeaderMagic_When_Read_Then_InvalidData() {
    var bytes = Vector("mingw-tiny.exe");
    var pe = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x3C));
    bytes[pe + 24] = 0x07;
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_TruncatedImage_When_Read_Then_InvalidData() {
    var bytes = Vector("mingw-tiny.exe")[..2000];
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_NonExecutable_When_Read_Then_InvalidData() {
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader(new byte[64]));
    Assert.Throws<InvalidDataException>(() => _ = new PeSectionReader("MZ"u8));
  }

  /// <summary>
  /// Live oracle: every non-resource entry 7-Zip lists for the host's own system DLLs (sections,
  /// CERTIFICATE, debug data, gaps) must match ours in name, size and content.
  /// </summary>
  [TestCase("kernel32.dll")]
  [TestCase("shlwapi.dll")]
  [TestCase("notepad.exe")]
  [Category("ExternalTool")]
  public void Given_SystemBinary_When_ComparedWithSevenZip_Then_EveryNonResourceEntryAgrees(string name) {
    var sevenZip = ToolDiscovery.GetToolPath("7z") ?? ToolDiscovery.GetToolPath("7za");
    if (sevenZip == null) Assert.Ignore("7-Zip not found.");
    var path = Path.Combine(Environment.SystemDirectory, name);
    if (!OperatingSystem.IsWindows() || !File.Exists(path)) Assert.Ignore($"{name} is not available on this host.");
    var image = File.ReadAllBytes(path);

    var psi = new ProcessStartInfo(sevenZip, ["l", "-slt", "-tPE", path]) { RedirectStandardOutput = true, UseShellExecute = false };
    using var p = Process.Start(psi)!;
    var output = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    var theirs = output.Split("----------", 2)[1]
      .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
      .Select(b => b.Split('\n').Select(l => l.Trim()).Where(l => l.Contains(" = ")).ToDictionary(l => l[..l.IndexOf(" = ")], l => l[(l.IndexOf(" = ") + 3)..]))
      .Where(kv => kv.ContainsKey("Path") && !kv["Path"].StartsWith(".rsrc\\", StringComparison.Ordinal))
      .Select(kv => (kv["Path"], long.Parse(kv["Size"]), long.Parse(kv["Offset"])))
      .ToList();
    var ours = new PeSectionReader(image).Items
      .Where(s => s.Kind != PeSectionKind.Overlay && !(s.Name == ".rsrc" && theirs.All(t => t.Item1 != ".rsrc")))
      .Select(s => (s.Name, s.Size, s.Offset))
      .ToList();
    Assert.That(ours, Is.EqualTo(theirs));
  }
}
