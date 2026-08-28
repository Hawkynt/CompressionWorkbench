using Compression.Registry;
using FileFormat.Ewf;

namespace Compression.Tests.Ewf;

[TestFixture]
public sealed class EwfPurgeTests {
  [Test]
  public void Purge_LeavesValidEmptyImageAndGeneratedDiagnostics() {
    var media = new byte[EwfWriter.ChunkSize * 2];
    for (var i = 0; i < media.Length; ++i) media[i] = (byte)(i * 31 + 7);
    using var image = new MemoryStream();
    image.Write(new EwfWriter { CompressChunks = true }.Build(media));
    image.Position = 0;

    var descriptor = new EwfFormatDescriptor();
    ((IArchivePurgeable)descriptor).Purge(image);

    var parsed = EwfReader.Read(image.ToArray());
    Assert.That(EwfReader.ExtractMedia(parsed), Is.Empty);
    var names = descriptor.List(image, null).Select(e => e.Name).ToArray();
    Assert.That(names, Does.Contain("media.raw"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names.Any(n => n.StartsWith("section_", StringComparison.Ordinal)), Is.True);
  }
}
