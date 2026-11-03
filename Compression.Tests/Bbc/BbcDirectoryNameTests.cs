using Compression.Registry;
using FileSystem.Bbc;

namespace Compression.Tests.Bbc;

/// <summary>
/// Acorn DFS names are "D.NAME": the directory letter tells files apart. Removing or
/// replacing "C.TXT" used to delete "A.TXT" as well (both are "TXT"), an added "C.TXT" went
/// into directory '$', and extraction wrote "D/NAME" where the listing said "D.NAME".
/// </summary>
/// <remarks>Upstreamed from KsmBl/failBrauwser 6b1065e, e154ed4, b967e36.</remarks>
[TestFixture]
public sealed class BbcDirectoryNameTests {
  private static MemoryStream Image(params (string Name, string Text)[] files) {
    var stream = new MemoryStream();
    new BbcFormatDescriptor().Create(stream,
      [.. files.Select(f => ArchiveInputInfo.InMemory(f.Name, System.Text.Encoding.ASCII.GetBytes(f.Text)))],
      new FormatCreateOptions());
    var expandable = new MemoryStream();
    expandable.Write(stream.ToArray());
    expandable.Position = 0;
    return expandable;
  }

  private static string[] Names(Stream image) {
    image.Position = 0;
    return [.. new BbcFormatDescriptor().List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase)];
  }

  [Test, Category("HappyPath")]
  public void GivenFilesInDifferentDirectories_WhenOneIsRemoved_ThenTheSameNameInAnotherDirectoryStays() {
    using var image = Image(("a.txt", "alpha"), ("b.txt", "beta"));
    new BbcFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("c.txt", "gamma"u8)]);
    var before = Names(image);

    new BbcFormatDescriptor().Remove(image, [before.Single(n => n.StartsWith("c", StringComparison.OrdinalIgnoreCase))]);

    Assert.That(Names(image).Select(n => n.ToUpperInvariant()), Is.EquivalentTo(new[] { "A.TXT", "B.TXT" }));
  }

  [Test, Category("HappyPath")]
  public void GivenANameWithADirectoryLetter_WhenAdded_ThenItLandsInThatDirectory() {
    using var image = Image(("a.txt", "alpha"));

    new BbcFormatDescriptor().Add(image, [ArchiveInputInfo.InMemory("c.txt", "gamma"u8)]);

    Assert.That(Names(image).Select(n => n.ToUpperInvariant()), Is.EquivalentTo(new[] { "A.TXT", "C.TXT" }));
  }

  [Test, Category("HappyPath")]
  public void GivenAListedName_WhenExtracted_ThenTheFileIsWrittenUnderThatName() {
    using var image = Image(("a.txt", "alpha"), ("plain", "p"));
    var listed = Names(image).Single(n => n.StartsWith("a", StringComparison.OrdinalIgnoreCase));
    var outDir = Directory.CreateTempSubdirectory("cwb_bbcx_").FullName;
    try {
      image.Position = 0;
      new BbcFormatDescriptor().Extract(image, outDir, null, [listed]);

      Assert.That(File.ReadAllText(Path.Combine(outDir, listed)), Is.EqualTo("alpha"));
    } finally {
      Directory.Delete(outDir, true);
    }
  }

  [TestCase("$.NAME", '$', "NAME")]
  [TestCase("NAME", '$', "NAME")]
  [TestCase("A.TXT", 'A', "TXT")]
  [TestCase("A.B.C", '$', "A_B")]
  [Category("Boundary")]
  public void GivenAPublishedName_WhenSplit_ThenDirectoryAndCatalogNameFollowTheDfsConvention(string name, char directory, string catalogName) {
    var (dir, bare) = BbcModifier.SplitName(name);

    Assert.Multiple(() => {
      Assert.That(dir, Is.EqualTo(directory));
      Assert.That(bare, Is.EqualTo(catalogName));
    });
  }
}
