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
  public void Defragment_ConsolidateAtStart_PreservesAllEntries() {
    var ms = BuildImageWithFiles(("A.TXT", "alpha"u8.ToArray()), ("B.TXT", "beta"u8.ToArray()));
    new FatFormatDescriptor().Defragment(ms,
      new DefragOptions { Mode = DefragMode.ConsolidateAtStart });
    AssertContents(ms, ("A.TXT", "alpha"), ("B.TXT", "beta"));
  }

  [Test]
  public void Defragment_ConsolidateAtEnd_PreservesAllEntries() {
    var ms = BuildImageWithFiles(("S.TXT", "small"u8.ToArray()), ("L.TXT", new byte[200]));
    new FatFormatDescriptor().Defragment(ms,
      new DefragOptions { Mode = DefragMode.ConsolidateAtEnd });
    AssertContents(ms, ("S.TXT", "small"), ("L.TXT", new string('\0', 200)));
  }

  [Test]
  public void Defragment_FillHolesLazy_PreservesAllEntries() {
    var ms = BuildImageWithFiles(("A.TXT", "one"u8.ToArray()), ("B.TXT", "two"u8.ToArray()));
    new FatFormatDescriptor().Defragment(ms,
      new DefragOptions { Mode = DefragMode.FillHolesLazy });
    AssertContents(ms, ("A.TXT", "one"), ("B.TXT", "two"));
  }

  [Test]
  public void Defragment_CarveHole_PreservesAllEntries() {
    var ms = BuildImageWithFiles(("A.TXT", "alpha"u8.ToArray()));
    new FatFormatDescriptor().Defragment(ms,
      new DefragOptions { Mode = DefragMode.CarveHole, HoleSize = 1024 });
    // File still readable; the hole eats into the trailing free region.
    AssertContents(ms, ("A.TXT", "alpha"));
  }

  [Test]
  public void Defragment_CarveHole_TooBig_Throws() {
    // FatWriter defaults to a 1.44MB floppy. File ~1.3MB leaves ~140KB free;
    // request a 500KB hole — too big.
    var ms = BuildImageWithFiles(("A.TXT", new byte[1_300_000]));
    Assert.Throws<ArgumentException>(() =>
      new FatFormatDescriptor().Defragment(ms,
        new DefragOptions { Mode = DefragMode.CarveHole, HoleSize = 500_000 }));
  }

  [Test]
  public void Defragment_CarveHole_ZeroSize_Throws() {
    var ms = BuildImageWithFiles(("A.TXT", "x"u8.ToArray()));
    Assert.Throws<ArgumentException>(() =>
      new FatFormatDescriptor().Defragment(ms,
        new DefragOptions { Mode = DefragMode.CarveHole, HoleSize = 0 }));
  }

  private static MemoryStream BuildImageWithFiles(params (string Name, byte[] Data)[] files) {
    var w = new FatWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image);
    ms.SetLength(image.Length);
    return ms;
  }

  private static void AssertContents(MemoryStream ms, params (string Name, string Content)[] expected) {
    ms.Position = 0;
    var reader = new FatReader(ms);
    var byName = reader.Entries
      .Where(e => !e.IsDirectory)
      .ToDictionary(e => e.Name, e => System.Text.Encoding.ASCII.GetString(reader.Extract(e)));
    foreach (var (name, content) in expected) {
      Assert.That(byName, Contains.Key(name));
      Assert.That(byName[name], Is.EqualTo(content));
    }
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
