#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

[TestFixture]
public class FatShrinkDefragTests {

  [Test]
  public void FatDescriptorImplementsShrinkable() {
    var desc = new FatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(desc.CanonicalSizes, Has.Count.EqualTo(3));
    Assert.That(desc.CanonicalSizes[0], Is.EqualTo(737280));
    Assert.That(desc.CanonicalSizes[^1], Is.EqualTo(2949120));
  }

  [Test]
  public void FatDescriptorImplementsDefragmentable() {
    var desc = new FatFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveDefragmentable>());
  }

  [Test]
  public void Defragment_PreservesOuterSize() {
    var w = new FatWriter();
    w.AddFile("A.TXT", new byte[] { 1, 2, 3 });
    w.AddFile("B.TXT", new byte[] { 4, 5, 6 });
    var image = w.Build();

    using var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    var originalSize = ms.Length;

    new FatFormatDescriptor().Defragment(ms);
    Assert.That(ms.Length, Is.EqualTo(originalSize));

    // All files must still be readable.
    ms.Position = 0;
    var reader = new FatReader(ms);
    Assert.That(reader.Entries.Count(e => !e.IsDirectory), Is.EqualTo(2));
  }

  [Test]
  public void Shrink_ProducesReadableOutput() {
    var w = new FatWriter();
    w.AddFile("X.BIN", new byte[] { 42 });
    var image = w.Build();
    using var input = new MemoryStream(image);
    using var output = new MemoryStream();

    new FatFormatDescriptor().Shrink(input, output);
    output.Position = 0;
    var reader = new FatReader(output);
    Assert.That(reader.Entries.Any(e => e.Name == "X.BIN"), Is.True);
  }

  [Test]
  public void ChooseTargetSize_PicksSmallestFit() {
    Assert.That(ArchiveShrinker.ChooseTargetSize([737280, 1474560, 2949120], 500_000),
      Is.EqualTo(737280));
    Assert.That(ArchiveShrinker.ChooseTargetSize([737280, 1474560, 2949120], 800_000),
      Is.EqualTo(1474560));
    Assert.That(ArchiveShrinker.ChooseTargetSize([737280, 1474560], 5_000_000),
      Is.EqualTo(1474560), "oversize payloads return the largest available");
  }
}
