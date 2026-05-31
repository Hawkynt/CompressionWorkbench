#pragma warning disable CS1591
namespace Compression.Tests.Udf;

[TestFixture]
public class UdfSubdirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_NestedPaths_RoundTripAtNestedLocations() {
    var rootContent = "root readme"u8.ToArray();
    var guideContent = "the guide"u8.ToArray();
    var refContent = "api reference body"u8.ToArray();

    var w = new FileSystem.Udf.UdfWriter();
    w.AddFile("readme.txt", rootContent);
    w.AddFile("docs/guide.txt", guideContent);
    w.AddFile("docs/api/reference.txt", refContent);

    using var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;

    var r = new FileSystem.Udf.UdfReader(ms);

    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToList();

    // Intermediate directories must exist as real directory entries.
    Assert.That(dirs, Does.Contain("docs"));
    Assert.That(dirs, Does.Contain("docs/api"));

    // Each file must round-trip at its exact nested path with intact content.
    var readme = files.Single(e => e.Name == "readme.txt");
    Assert.That(r.Extract(readme), Is.EqualTo(rootContent));

    var guide = files.Single(e => e.Name == "docs/guide.txt");
    Assert.That(r.Extract(guide), Is.EqualTo(guideContent));

    var reference = files.Single(e => e.Name == "docs/api/reference.txt");
    Assert.That(r.Extract(reference), Is.EqualTo(refContent));
  }
}
