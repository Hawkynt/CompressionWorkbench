#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

[TestFixture]
public sealed class ProDosModifierFolderTests {

  private static MemoryStream Empty() {
    var ms = new MemoryStream();
    ms.Write(new ProDosWriter().Build());
    return ms;
  }

  [Test]
  public void GivenAPathWithFolders_WhenAdded_ThenTheFoldersAreRealSubdirectories() {
    using var ms = Empty();
    ProDosModifier.AddFile(ms, "DOCS/DEEP/LEAF", new byte[700]);
    ProDosModifier.AddFile(ms, "DOCS/README", "r"u8.ToArray());
    ms.Position = 0;
    using var r = new ProDosReader(ms);
    var byPath = r.Entries.ToDictionary(e => e.FullPath, StringComparer.Ordinal);
    Assert.That(byPath["DOCS"].IsDirectory, Is.True);
    Assert.That(byPath["DOCS/DEEP"].IsDirectory, Is.True);
    Assert.That(r.Extract(byPath["DOCS/DEEP/LEAF"]), Has.Length.EqualTo(700));
    Assert.That(byPath.Keys.Where(k => !k.Contains('/')), Is.EqualTo(new[] { "DOCS" }), "nothing lands in the root");
  }

  [Test]
  public void GivenMoreFilesThanAKeyBlockHolds_WhenAddedToAFolder_ThenTheDirectoryChainsAnotherBlock() {
    using var ms = Empty();
    for (var i = 0; i < 30; ++i) ProDosModifier.AddFile(ms, $"MANY/F{i:D2}", [(byte)i]);
    ms.Position = 0;
    using var r = new ProDosReader(ms);
    var files = r.Entries.Where(e => e.FullPath.StartsWith("MANY/", StringComparison.Ordinal)).ToList();
    Assert.That(files, Has.Count.EqualTo(30));
    foreach (var e in files) Assert.That(r.Extract(e)[0], Is.EqualTo(byte.Parse(e.Name[1..])));
  }

  [Test]
  public void GivenANestedFile_WhenRemoved_ThenItIsGoneAndItsFolderStays() {
    using var ms = Empty();
    ProDosModifier.AddFile(ms, "A/B.TXT", "b"u8.ToArray());
    Assert.That(ProDosModifier.RemoveFile(ms, "A/B.TXT"), Is.True);
    Assert.That(ProDosModifier.RemoveFile(ms, "A/MISSING"), Is.False);
    ms.Position = 0;
    using var r = new ProDosReader(ms);
    Assert.That(r.Entries.Select(e => e.FullPath), Is.EqualTo(new[] { "A" }));
  }

  [Test]
  public void GivenAFileWhereAFolderIsNeeded_WhenAdded_ThenItIsRefusedAndTheImageIsUntouched() {
    using var ms = Empty();
    ProDosModifier.AddFile(ms, "PLAIN", "p"u8.ToArray());
    var before = ms.ToArray();
    Assert.Throws<InvalidOperationException>(() => ProDosModifier.AddFile(ms, "PLAIN/CHILD", "c"u8.ToArray()));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }

  [Test]
  public void GivenAFullVolumeDirectory_WhenAnotherFileIsAdded_ThenItIsRefusedAndTheImageIsUntouched() {
    using var ms = Empty();
    var added = 0;
    try {
      for (; added < 100; ++added) ProDosModifier.AddFile(ms, $"R{added:D3}", [1]);
    } catch (InvalidOperationException) {
    }
    Assert.That(added, Is.EqualTo(51), "the volume directory's four blocks hold 51 entries");
    var before = ms.ToArray();
    Assert.Throws<InvalidOperationException>(() => ProDosModifier.AddFile(ms, "ONE.MORE", new byte[5000]));
    Assert.That(ms.ToArray(), Is.EqualTo(before));
  }
}
