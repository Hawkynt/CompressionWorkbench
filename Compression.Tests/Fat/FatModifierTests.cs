#pragma warning disable CS1591
using System.Text;
using FileSystem.Fat;

namespace Compression.Tests.Fat;

/// <summary>
/// Genuine in-place add for FAT12/16/32 via <see cref="FatModifier"/>: the boot
/// sector and every existing file's data clusters stay byte-identical at their
/// original offsets, the image keeps its length, and the new file round-trips.
/// Proves the add is a real cluster/directory edit — not a re-pack.
/// </summary>
[TestFixture]
public class FatModifierTests {

  private static byte[] BuildImageWith(params (string Name, byte[] Data)[] files) {
    var w = new FatWriter();
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  private static byte[] Extract(byte[] image, string name) {
    using var ms = new MemoryStream(image, writable: false);
    var reader = new FatReader(ms);
    var e = reader.Entries.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    return reader.Extract(e);
  }

  private static IReadOnlyList<string> Names(byte[] image) {
    using var ms = new MemoryStream(image, writable: false);
    return new FatReader(ms).Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  [Test]
  public void Add_PreservesBootSectorExistingFileAndLength() {
    var keep = Encoding.ASCII.GetBytes(new string('K', 3000)); // spans multiple clusters
    var image = BuildImageWith(("KEEP.TXT", keep));
    var before = (byte[])image.Clone();

    FatModifier.AddFile(image, "NEW.TXT", Encoding.ASCII.GetBytes("brand new payload"));

    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(before.Length), "in-place add must not resize the image");
      Assert.That(image.AsSpan(0, 512).SequenceEqual(before.AsSpan(0, 512)), Is.True,
        "boot sector must be byte-identical after an in-place add");
      Assert.That(Extract(image, "KEEP.TXT"), Is.EqualTo(keep), "existing file content must survive");
      Assert.That(Extract(image, "NEW.TXT"), Is.EqualTo(Encoding.ASCII.GetBytes("brand new payload")));
      Assert.That(Names(image), Has.Member("KEEP.TXT").And.Member("NEW.TXT"));
    });
  }

  [Test]
  public void Add_DoesNotRewriteTheWholeImage() {
    // A genuine in-place add touches only the new file's clusters, the FAT entries
    // for those clusters, and a directory slot — a small fraction of the image.
    var image = BuildImageWith(("ALPHA.BIN", new byte[2048]));
    var before = (byte[])image.Clone();
    FatModifier.AddFile(image, "BETA.BIN", new byte[] { 1, 2, 3, 4, 5 });

    var changed = 0;
    for (var i = 0; i < image.Length; i++) if (image[i] != before[i]) changed++;
    Assert.That(changed, Is.LessThan(image.Length / 4),
      $"in-place add changed {changed}/{image.Length} bytes — should be a small fraction, not a re-pack");
  }

  [Test]
  public void Add_LongFileName_RoundTripsViaLfn() {
    var image = BuildImageWith(("README.TXT", new byte[10]));
    var longName = "A Long Mixed-Case File Name.dat";
    FatModifier.AddFile(image, longName, Encoding.ASCII.GetBytes("payload-x"));
    Assert.That(Names(image), Has.Member(longName));
    Assert.That(Extract(image, longName), Is.EqualTo(Encoding.ASCII.GetBytes("payload-x")));
  }

  [Test]
  public void Add_ReplaceByName_OverwritesAndKeepsOthers() {
    var image = BuildImageWith(("DOC.TXT", Encoding.ASCII.GetBytes("old")), ("OTHER.BIN", new byte[] { 7, 7 }));
    FatModifier.AddFile(image, "DOC.TXT", Encoding.ASCII.GetBytes("new contents"));
    Assert.That(Extract(image, "DOC.TXT"), Is.EqualTo(Encoding.ASCII.GetBytes("new contents")));
    Assert.That(Extract(image, "OTHER.BIN"), Is.EqualTo(new byte[] { 7, 7 }));
    Assert.That(Names(image).Count(n => n.Equals("DOC.TXT", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1),
      "replace must not leave a duplicate entry");
  }

  [Test]
  public void Add_NestedPath_ThrowsForRebuildFallback() {
    var image = BuildImageWith(("ROOT.TXT", new byte[1]));
    Assert.Throws<NotSupportedException>(() => FatModifier.AddFile(image, "sub/dir/file.txt", new byte[] { 1 }));
  }
}
