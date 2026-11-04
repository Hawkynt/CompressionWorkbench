using System.Reflection;
using Compression.Registry;
using FileFormat.Cba;

namespace Compression.Tests.ComicBook;

/// <summary>
/// .cba comic book archives: an extension-only view of the ACE container. Oracle: acefile lists and
/// tests the archive our writer produced (ReferenceVectors/acefile-cba-listing.txt).
/// </summary>
[TestFixture]
public class CbaTests {
  private static readonly byte[] Page1 = "first comic page bytes"u8.ToArray();
  private static readonly byte[] Page2 = [.. Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251))];
  private static readonly byte[] Page3 = "third page, added later"u8.ToArray();

  internal static void WriteOurs(Stream output) =>
    new CbaFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("page01.png", Page1), ArchiveInputInfo.InMemory("page02.png", Page2)],
      new FormatCreateOptions());

  private static MemoryStream Created() {
    var ms = new MemoryStream();
    WriteOurs(ms);
    ms.Position = 0;
    return ms;
  }

  private static byte[] Read(IArchiveFormatOperations d, Stream archive, string name) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb_cba_" + Guid.NewGuid().ToString("N")[..8]);
    try {
      archive.Position = 0;
      d.Extract(archive, dir, null, [name]);
      return File.ReadAllBytes(Path.Combine(dir, name));
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("RoundTrip")]
  public void Given_Pages_When_Created_Then_ListedAndExtractedInOrder() {
    var d = new CbaFormatDescriptor();
    using var ms = Created();
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "page01.png", "page02.png" }));
    Assert.That(Read(d, ms, "page02.png"), Is.EqualTo(Page2));
  }

  [Test, Category("RoundTrip")]
  public void Given_Archive_When_PageAddedAndRemoved_Then_ListReflectsBoth() {
    var d = new CbaFormatDescriptor();
    using var ms = Created();
    d.Add(ms, [ArchiveInputInfo.InMemory("page03.png", Page3)]);
    d.Remove(ms, ["page01.png"]);
    ms.Position = 0;
    Assert.That(d.List(ms, null).Select(e => e.Name), Is.EqualTo(new[] { "page02.png", "page03.png" }));
    Assert.That(Read(d, ms, "page03.png"), Is.EqualTo(Page3));
  }

  [Test, Category("HappyPath")]
  public void Given_Cba_When_Inspected_Then_ItIsAnAceArchive() {
    using var ms = Created();
    var bytes = ms.ToArray();
    Assert.That(bytes.AsSpan(7, 7).ToArray(), Is.EqualTo("**ACE**"u8.ToArray()));
    Assert.That(new CbaFormatDescriptor().MagicSignatures, Is.Empty, "a content scan resolves to Ace");
  }

  // ours.cba is this writer's output; acefile listed and tested it.
  [Test, Category("ThirdPartyVector")]
  public void Given_OurFixture_When_Read_Then_PagesMatchWhatAcefileVerified() {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CbaVectors.ours.cba")
      ?? throw new FileNotFoundException("Embedded CBA vector is missing.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var d = new CbaFormatDescriptor();
    Assert.That(d.List(ms, null).Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(new[] { ("page01.png", 22L), ("page02.png", 5000L) }));
    Assert.That(Read(d, ms, "page01.png"), Is.EqualTo(Page1));
    Assert.That(Read(d, ms, "page02.png"), Is.EqualTo(Page2));
  }
}
