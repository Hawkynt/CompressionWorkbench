using Compression.Registry;
using FileSystem.OrangeFs;

namespace Compression.Tests.OrangeFs;

[TestFixture]
public sealed class OrangeFsWriteTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void CreateReplaceRemove_RoundTripsObjectPayloadAndPreservesHeaderIdentity() {
    var descriptor = new OrangeFsFormatDescriptor();
    var first = Enumerable.Range(0, 97).Select(i => (byte)(i * 3)).ToArray();
    var replacement = Enumerable.Range(0, 513).Select(i => (byte)(i * 7)).ToArray();
    using var image = new MemoryStream();

    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory("object.bin", first)], new FormatCreateOptions());
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var before = image.ToArray()[..12];
    AssertPayload(image, first);

    image.Position = 0;
    ((IArchiveModifiable)descriptor).Add(image,
      [ArchiveInputInfo.InMemory("object.bin", replacement)]);
    Assert.That(image.ToArray()[..12], Is.EqualTo(before));
    AssertPayload(image, replacement);

    image.Position = 0;
    ((IArchiveModifiable)descriptor).Remove(image, ["object.bin"]);
    Assert.That(image.Length, Is.EqualTo(16));
    AssertPayload(image, []);
  }

  private static void AssertPayload(MemoryStream image, byte[] expected) {
    image.Position = 0;
    using var reader = new OrangeFsReader(image);
    var objectEntry = reader.Entries.FirstOrDefault(e => e.Name == "object.bin");
    if (expected.Length == 0) {
      Assert.That(objectEntry, Is.Null);
      Assert.That(reader.ObjectSize, Is.Zero);
      return;
    }
    Assert.That(objectEntry, Is.Not.Null);
    Assert.That(reader.Extract(objectEntry!), Is.EqualTo(expected));
  }
}
