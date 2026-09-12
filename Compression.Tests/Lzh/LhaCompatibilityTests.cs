using System.Buffers.Binary;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

[TestFixture]
public class LhaCompatibilityTests {
  [Test]
  public void ArchiverGeneration_IsIndependentFromHeaderLevel() {
    var data = Enumerable.Repeat((byte)'A', 4096).ToArray();

    foreach (var level in Enum.GetValues<LhaHeaderLevel>()) {
      var writer = new LhaWriter(LhaConstants.MethodLh1, LhaArchiverGeneration.LhArc1, level);
      writer.AddFile("TEST.TXT", data);
      var archive = writer.ToArray();

      Assert.That(archive[20], Is.EqualTo((byte)level), $"header level {level}");

      using var reader = new LhaReader(new MemoryStream(archive));
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
      Assert.That(reader.Entries[0].HeaderLevel, Is.EqualTo((byte)level));
      Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    }
  }

  [Test]
  public void Generations_AllowOnlyTheirMethodFamilies() {
    Assert.Multiple(() => {
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.LArc, LhaConstants.MethodLzs), Is.True);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.LArc, LhaConstants.MethodLh1), Is.False);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.LhArc1, LhaConstants.MethodLh1), Is.True);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.LhArc1, LhaConstants.MethodLh5), Is.False);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.Lha2, LhaConstants.MethodLh5), Is.True);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.Lha2, LhaConstants.MethodPm1), Is.False);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.PmArc1, LhaConstants.MethodPm1), Is.True);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.PmArc1, LhaConstants.MethodPm2), Is.False);
      Assert.That(LhaCompatibility.IsSupported(LhaArchiverGeneration.PmArc2, LhaConstants.MethodPm2), Is.True);
    });
  }

  [Test]
  public void Writer_RejectsMethodOutsideGeneration() {
    Assert.That(
      () => new LhaWriter(LhaConstants.MethodLh5, LhaArchiverGeneration.LhArc1),
      Throws.TypeOf<NotSupportedException>());
  }

  [Test]
  public void LArcStoredMethod_IsWrittenAsLz4AndRoundTrips() {
    var data = "LArc stored payload"u8.ToArray();
    var writer = new LhaWriter(
      LhaConstants.MethodLz4,
      LhaArchiverGeneration.LArc,
      LhaHeaderLevel.Level0);
    writer.AddFile("TEST.TXT", data);
    var archive = writer.ToArray();

    using var reader = new LhaReader(new MemoryStream(archive));
    Assert.Multiple(() => {
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
      Assert.That(reader.Entries[0].Method, Is.EqualTo(LhaConstants.MethodLz4));
      Assert.That(reader.Entries[0].HeaderLevel, Is.EqualTo((byte)LhaHeaderLevel.Level0));
      Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    });
  }

  [Test]
  public void LArcCompressionFallback_UsesLz4_NotLh0() {
    var writer = new LhaWriter(
      LhaConstants.MethodLzs,
      LhaArchiverGeneration.LArc,
      LhaHeaderLevel.Level0);
    writer.AddFile("TINY.BIN", [0x42]);
    var archive = writer.ToArray();

    Assert.That(System.Text.Encoding.ASCII.GetString(archive, 2, 5), Is.EqualTo(LhaConstants.MethodLz4));
  }

  [Test]
  public void Level1_RejectsNameBeyondBaseHeaderLimit() {
    var writer = new LhaWriter(
      LhaConstants.MethodLh0,
      LhaArchiverGeneration.LhArc1,
      LhaHeaderLevel.Level1);
    writer.AddFile(new string('A', 231), [0x01]);

    Assert.That(() => writer.ToArray(), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Level2_SupportsLongNameViaFilenameExtendedHeader() {
    var name = new string('A', 300) + ".TXT";
    var data = "long-name"u8.ToArray();
    var writer = new LhaWriter(
      LhaConstants.MethodLh0,
      LhaArchiverGeneration.Lha2,
      LhaHeaderLevel.Level2);
    writer.AddFile(name, data);
    var archive = writer.ToArray();

    using var reader = new LhaReader(new MemoryStream(archive));
    Assert.Multiple(() => {
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
      Assert.That(reader.Entries[0].FileName, Is.EqualTo(name));
      Assert.That(reader.Entries[0].HeaderLevel, Is.EqualTo((byte)LhaHeaderLevel.Level2));
      Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    });
  }

  [Test]
  public void Level2_PadsHeaderWhenLowSizeByteWouldBeZero() {
    // Base(26) + filename extension(type + 227-byte name + next-size = 230) = 256.
    // A leading zero byte denotes end-of-archive to LHA readers, so level 2 needs one padding byte.
    var name = new string('A', 227);
    var data = "boundary"u8.ToArray();
    var writer = new LhaWriter(
      LhaConstants.MethodLh0,
      LhaArchiverGeneration.Lha2,
      LhaHeaderLevel.Level2);
    writer.AddFile(name, data);
    var archive = writer.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(archive), Is.EqualTo(257));

    using var reader = new LhaReader(new MemoryStream(archive));
    Assert.Multiple(() => {
      Assert.That(reader.Entries, Has.Count.EqualTo(1));
      Assert.That(reader.Entries[0].FileName, Is.EqualTo(name));
      Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(data));
    });
  }
}
