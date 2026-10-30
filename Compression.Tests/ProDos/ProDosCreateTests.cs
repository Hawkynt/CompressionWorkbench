#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.ProDos;

namespace Compression.Tests.ProDos;

[TestFixture]
public sealed class ProDosCreateTests {

  [Test]
  public void GivenInputsInFolders_WhenCreated_ThenTheFoldersAreRealSubdirectories() {
    var d = new ProDosFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [
      ArchiveInputInfo.InMemory("TOP", "t"u8.ToArray()),
      ArchiveInputInfo.InMemory("DOCS/README", "r"u8.ToArray()),
      ArchiveInputInfo.InMemory("DOCS/DEEP/LEAF", new byte[700]),
    ], new FormatCreateOptions());
    ms.Position = 0;
    using var r = new ProDosReader(ms);
    var byPath = r.Entries.ToDictionary(e => e.FullPath, StringComparer.Ordinal);
    Assert.That(byPath["DOCS"].IsDirectory, Is.True);
    Assert.That(byPath["DOCS/DEEP"].IsDirectory, Is.True);
    Assert.That(byPath["DOCS/DEEP/LEAF"].Size, Is.EqualTo(700));
    Assert.That(byPath.Keys.Where(k => !k.Contains('/')), Is.EquivalentTo(new[] { "TOP", "DOCS" }), "nothing is flattened into the root");
  }
}
