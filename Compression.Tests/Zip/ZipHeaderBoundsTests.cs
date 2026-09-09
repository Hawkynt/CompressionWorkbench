using FileFormat.Zip;

namespace Compression.Tests.Zip;

[TestFixture]
public class ZipHeaderBoundsTests {
  [Test]
  public void Writer_RejectsFileNameWhoseUtf8EncodingExceeds16BitLength() {
    using var stream = new MemoryStream();
    using var writer = new ZipWriter(stream, leaveOpen: true);
    var name = new string('A', ushort.MaxValue + 1);

    Assert.That(
      () => writer.AddEntry(name, [], ZipCompressionMethod.Store),
      Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void Writer_RejectsArchiveCommentWhoseUtf8EncodingExceeds16BitLength() {
    using var stream = new MemoryStream();
    using var writer = new ZipWriter(stream, leaveOpen: true) {
      Comment = new string('A', ushort.MaxValue + 1),
    };

    Assert.That(() => writer.Finish(), Throws.TypeOf<InvalidDataException>());
  }
}
