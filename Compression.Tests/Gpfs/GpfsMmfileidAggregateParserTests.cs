using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

[TestFixture]
public class GpfsMmfileidAggregateParserTests {
  [Test]
  public void ParsesIndependentDiskQueriesWithoutLosingDiskIdentity() {
    const string text = """
      ===== 2:2201958 =====
      Address 2201958 is contained in the Block allocation map (inode 1)
      ===== 7:1076256 =====
      14336 1076256 0 /gpfsB/tesDir/testFile.out
      """;

    var queries = GpfsMmfileidAggregateParser.Parse(text);

    Assert.Multiple(() => {
      Assert.That(queries, Has.Count.EqualTo(2));
      Assert.That(queries[0].QueryAddress, Is.EqualTo(new GpfsDiskAddress(2, 2201958)));
      Assert.That(queries[0].Owners.Single().Address, Is.EqualTo(new GpfsDiskAddress(2, 2201958)));
      Assert.That(queries[0].Owners.Single().InodeNumber, Is.EqualTo(1));
      Assert.That(queries[1].QueryAddress, Is.EqualTo(new GpfsDiskAddress(7, 1076256)));
      Assert.That(queries[1].Owners.Single().Address, Is.EqualTo(new GpfsDiskAddress(7, 1076256)));
      Assert.That(queries[1].Owners.Single().Path, Is.EqualTo("/gpfsB/tesDir/testFile.out"));
    });
  }

  [Test]
  public void PreservesQueriesThatReturnNoOwnerLines() {
    const string text = """
      ===== 3:100 =====
      No file found for requested address
      ===== 3:200 =====
      Address 200 is contained in the Log File (inode 7, snapId 0)
      """;

    var queries = GpfsMmfileidAggregateParser.Parse(text);

    Assert.Multiple(() => {
      Assert.That(queries, Has.Count.EqualTo(2));
      Assert.That(queries[0].QueryAddress, Is.EqualTo(new GpfsDiskAddress(3, 100)));
      Assert.That(queries[0].Owners, Is.Empty);
      Assert.That(queries[1].Owners.Single().InodeNumber, Is.EqualTo(7));
    });
  }

  [Test]
  public void RejectsUnscopedOutputBeforeFirstQueryHeading()
    => Assert.Throws<InvalidDataException>(() =>
      GpfsMmfileidAggregateParser.Parse("Address 100 is contained in inode 1"));
}
