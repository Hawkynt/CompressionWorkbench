using System.Buffers.Binary;
using System.Text;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// The BKF reader against media built here, field by field, from MTF 1.00a (§4–§6) —
/// independently of <see cref="BkfWriter"/> — covering what NTBackup writes and what the
/// spec allows: ANSI and Unicode strings, names in FNAM/PNAM streams, checksummed STAN
/// streams, 512- and 1024-byte Format Logical Blocks, soft filemarks, several Data Sets,
/// damaged blocks and streams that are not decoded.
/// </summary>
[TestFixture]
public sealed class BkfReaderTests {
  [Test, Category("HappyPath")]
  public void GivenAMediumWithFoldersAndFiles_WhenRead_ThenPathsAndBytesFollowTheDirbs() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("top.txt", "top"u8.ToArray())
      .Dirb("docs\0").File("a.txt", "in docs"u8.ToArray())
      .Dirb("docs\0deep\0").File("b.bin", [1, 2, 3]));
    var r = Read(m.Build());

    Assert.Multiple(() => {
      Assert.That(r.Entries.Select(e => (e.Name, e.IsDirectory)), Is.EqualTo(new[] {
        ("top.txt", false), ("docs", true), ("docs/a.txt", false), ("docs/deep", true), ("docs/deep/b.bin", false),
      }));
      Assert.That(r.Extract(r.Entries.Single(e => e.Name == "docs/deep/b.bin")), Is.EqualTo(new byte[] { 1, 2, 3 }));
    });
  }

  [TestCase(512)]
  [TestCase(1024)]
  [Category("Boundary")]
  public void GivenEitherFormatLogicalBlockSize_WhenRead_ThenALargeFileReadsBack(int flb) {
    var data = new byte[5 * flb + 7];
    new Random(flb).NextBytes(data);
    var m = new MtfFixture(flb);
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("big.bin", data).File("after.txt", "after"u8.ToArray()));
    var r = Read(m.Build());

    Assert.That(r.LogicalBlockSize, Is.EqualTo(flb));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "big.bin")), Is.EqualTo(data));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "after.txt")), Is.EqualTo("after"u8.ToArray()));
  }

  [Test, Category("EquivalenceClass")]
  public void GivenAnsiStringsAndNamesInStreams_WhenRead_ThenTheNamesComeFromThere() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:", ansi: true).Dirb("long folder\0", inStream: true).File("long file name.txt", "x"u8.ToArray(), inStream: true, ansi: true));
    var r = Read(m.Build());
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "long folder", "long folder/long file name.txt" }));
  }

  [Test, Category("ErrorHandling")]
  public void GivenDataThatDoesNotMatchItsCsumStream_WhenExtracted_ThenItIsReported() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("f.bin", [1, 2, 3, 4], badChecksum: true));
    var r = Read(m.Build());
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.Single()));
  }

  [Test, Category("ErrorHandling")]
  public void GivenACompressedDataStream_WhenExtracted_ThenItIsListedButRefused() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("c.bin", [9, 9, 9], compressed: true));
    var r = Read(m.Build());
    Assert.That(r.Entries.Single().Size, Is.EqualTo(3));
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries.Single()));
  }

  [Test, Category("EquivalenceClass")]
  public void GivenTwoDataSets_WhenRead_ThenEachIsListedUnderItsOwnSetFolder() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("same.txt", "one"u8.ToArray()));
    m.Set(2, s => s.Volb("C:").Dirb("\0").File("same.txt", "two"u8.ToArray()));
    var r = Read(m.Build());
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "set-01/same.txt", "set-02/same.txt" }));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo("two"u8.ToArray()));
  }

  [Test, Category("ErrorHandling")]
  public void GivenADamagedBlock_WhenRead_ThenItIsSteppedOverAndTheNextFileStillReads() {
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("first.txt", "1"u8.ToArray()).Garbage().File("second.txt", "2"u8.ToArray()));
    var r = Read(m.Build());
    Assert.That(r.Entries.Select(e => e.Name), Is.EqualTo(new[] { "first.txt", "second.txt" }));
  }

  [TestCase(new byte[0])]
  [TestCase(new byte[] { 1, 2, 3 })]
  [Category("ErrorHandling")]
  public void GivenNoTapeBlock_WhenRead_ThenItIsInvalid(byte[] bytes) {
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new BkfReader(ms));
  }

  [TestCase(256)]
  [TestCase(2048)]
  [Category("Boundary")]
  public void GivenAFormatLogicalBlockSizeOutsideTheSpec_WhenRead_ThenItIsInvalid(int flb) {
    var bytes = new MtfFixture().Build();
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(84), (ushort)flb);
    MtfFixture.Seal(bytes.AsSpan(0, 52));
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new BkfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void GivenATapeBlockWithABadChecksum_WhenRead_ThenItIsInvalid() {
    var bytes = new MtfFixture().Build();
    bytes[50] ^= 0xFF;
    using var ms = new MemoryStream(bytes);
    Assert.Throws<InvalidDataException>(() => _ = new BkfReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void GivenNullArguments_WhenUsed_ThenTheyAreRejected() {
    Assert.Throws<ArgumentNullException>(() => _ = new BkfReader(null!));
    var m = new MtfFixture();
    m.Set(1, s => s.Volb("C:").Dirb("\0").File("f", [1]));
    Assert.Throws<ArgumentNullException>(() => Read(m.Build()).Extract(null!));
  }

  private static BkfReader Read(byte[] bytes) => new(new MemoryStream(bytes));
}
