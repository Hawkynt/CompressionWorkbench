using System.Text;
using FileSystem.Os9Rbf;

namespace Compression.Tests.Os9Rbf;

[TestFixture]
public class Os9RbfSubdirectoryTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_NestedPaths_RoundTripAtNestedLocations() {
    var readme = Encoding.ASCII.GetBytes("top-level readme");
    var guide = Encoding.ASCII.GetBytes("the guide body, somewhat longer to span content");
    var reference = Enumerable.Range(0, 700).Select(i => (byte)(i & 0xFF)).ToArray();

    var image = Os9RbfWriter.Build([
      ("readme.txt", readme),
      ("docs/guide.txt", guide),
      ("docs/api/reference.txt", reference),
    ]);

    var v = Os9RbfReader.Read(image);

    // Every leaf file must surface at its full nested path.
    var leaves = v.Files
      .Where(f => !f.IsDirectory)
      .ToDictionary(f => f.Name);

    Assert.That(leaves.Keys, Is.EquivalentTo(new[] {
      "readme.txt", "docs/guide.txt", "docs/api/reference.txt",
    }));

    Assert.That(Os9RbfReader.Extract(v, leaves["readme.txt"]), Is.EqualTo(readme).AsCollection);
    Assert.That(Os9RbfReader.Extract(v, leaves["docs/guide.txt"]), Is.EqualTo(guide).AsCollection);
    Assert.That(Os9RbfReader.Extract(v, leaves["docs/api/reference.txt"]), Is.EqualTo(reference).AsCollection);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_NestedPaths_IntermediateDirectoriesExist() {
    var image = Os9RbfWriter.Build([
      ("docs/guide.txt", Encoding.ASCII.GetBytes("g")),
      ("docs/api/reference.txt", Encoding.ASCII.GetBytes("r")),
    ]);

    var v = Os9RbfReader.Read(image);

    var dirs = v.Files.Where(f => f.IsDirectory).Select(f => f.Name).ToHashSet();
    Assert.That(dirs, Does.Contain("docs"));
    Assert.That(dirs, Does.Contain("docs/api"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void BuildRead_SharedIntermediateDirectory_CreatedOnce() {
    // Two files in the same subdirectory must reuse the single "docs" directory.
    var image = Os9RbfWriter.Build([
      ("docs/a.txt", Encoding.ASCII.GetBytes("aaa")),
      ("docs/b.txt", Encoding.ASCII.GetBytes("bbb")),
    ]);

    var v = Os9RbfReader.Read(image);

    Assert.That(v.Files.Count(f => f.IsDirectory && f.Name == "docs"), Is.EqualTo(1));

    var a = v.Files.Single(f => f.Name == "docs/a.txt");
    var b = v.Files.Single(f => f.Name == "docs/b.txt");
    Assert.That(Encoding.ASCII.GetString(Os9RbfReader.Extract(v, a)), Is.EqualTo("aaa"));
    Assert.That(Encoding.ASCII.GetString(Os9RbfReader.Extract(v, b)), Is.EqualTo("bbb"));
  }
}
