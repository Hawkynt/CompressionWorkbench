using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.NuFx;

namespace Compression.Tests.NuFx;

[TestFixture]
public sealed class NuFxSqueezeTests {
  [Test]
  public void SqueezeThread_IsHeaderlessAndRoundTripsRleEdgeCases() {
    var data = new List<byte>();
    data.AddRange(Enumerable.Repeat((byte)'A', 255));
    data.AddRange(Enumerable.Repeat((byte)'A', 17));
    data.AddRange(Enumerable.Repeat((byte)0x90, 8));
    data.AddRange(Enumerable.Range(0, 256).Select(i => (byte)i));
    var expected = data.ToArray();

    var descriptor = new NuFxFormatDescriptor();
    using var archive = new MemoryStream();
    descriptor.Create(archive, [ArchiveInputInfo.InMemory("A", expected)],
      new FormatCreateOptions { MethodName = "squeeze" });

    var bytes = archive.ToArray();
    const int recordStart = 48;
    var attribCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(recordStart + 6, 2));
    var threadCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(recordStart + 0x0A, 4));
    Assert.That(threadCount, Is.EqualTo(2u));

    var filenameThread = recordStart + attribCount;
    var filenameFieldLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(filenameThread + 12, 4));
    var compressedStart = checked(recordStart + attribCount + (int)threadCount * 16 + (int)filenameFieldLength);

    // NuFX thread format 1 starts directly with the Squeeze node count. A standalone
    // Squeeze file would start 76 FF and is not legal in this thread representation.
    Assert.That(bytes.AsSpan(compressedStart, 2).SequenceEqual(new byte[] { 0x76, 0xFF }), Is.False);
    var nodeCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(compressedStart, 2));
    Assert.That(nodeCount, Is.InRange(1, 256));

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "A", null), Is.EqualTo(expected));
  }

  [Test]
  public void SqueezeThread_EmptyPayloadUsesZeroNodeTree() {
    var descriptor = new NuFxFormatDescriptor();
    using var archive = new MemoryStream();
    descriptor.Create(archive, [ArchiveInputInfo.InMemory("EMPTY", Array.Empty<byte>())],
      new FormatCreateOptions { MethodName = "squeeze" });

    archive.Position = 0;
    var entry = descriptor.List(archive, null).Single();
    Assert.That(entry.Method, Is.EqualTo("Squeeze"));
    Assert.That(entry.CompressedSize, Is.EqualTo(2));

    archive.Position = 0;
    Assert.That(descriptor.ExtractEntryToMemory(archive, "EMPTY", null), Is.Empty);
  }
}
