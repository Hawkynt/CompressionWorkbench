using Compression.Registry;
using FileFormat.Dmg;

namespace Compression.Tests.Dmg;

[TestFixture]
public sealed class DmgModifyTests {
  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Writer_NonSectorAlignedPartition_RoundTripsExactLength() {
    var payload = new byte[517];
    new Random(19).NextBytes(payload);

    var writer = new DmgWriter();
    writer.AddPartition("odd.bin", payload);
    using var image = new MemoryStream();
    writer.WriteTo(image);

    image.Position = 0;
    using var reader = new DmgReader(image);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
    Assert.That(reader.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_AddReplaceRemove_MutatesRawUdifProfile() {
    var descriptor = new DmgFormatDescriptor();
    Assert.That(descriptor, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    var a = Enumerable.Range(0, 517).Select(i => (byte)(i * 11)).ToArray();
    var b = Enumerable.Range(0, 733).Select(i => (byte)(i * 7)).ToArray();
    var c = Enumerable.Range(0, 91).Select(i => (byte)(255 - i)).ToArray();
    var a2 = Enumerable.Range(0, 1025).Select(i => (byte)(i * 3)).ToArray();

    using var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("A.BIN", a),
      ArchiveInputInfo.InMemory("B.BIN", b),
    ], new FormatCreateOptions());

    var modifier = (IArchiveModifiable)descriptor;
    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory("C.BIN", c)]);
    AssertPayload(image, "A.BIN", a);
    AssertPayload(image, "B.BIN", b);
    AssertPayload(image, "C.BIN", c);

    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory("A.BIN", a2)]);
    AssertPayload(image, "A.BIN", a2);
    AssertPayload(image, "B.BIN", b);
    AssertPayload(image, "C.BIN", c);

    image.Position = 0;
    modifier.Remove(image, ["B.BIN"]);
    image.Position = 0;
    using var reader = new DmgReader(image);
    Assert.That(reader.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "A.BIN", "C.BIN" }));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "A.BIN")), Is.EqualTo(a2));
    Assert.That(reader.Extract(reader.Entries.Single(e => e.Name == "C.BIN")), Is.EqualTo(c));
  }

  private static void AssertPayload(MemoryStream image, string name, byte[] expected) {
    image.Position = 0;
    using var reader = new DmgReader(image);
    var entry = reader.Entries.Single(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    Assert.That(entry.Size, Is.EqualTo(expected.Length));
    Assert.That(reader.Extract(entry), Is.EqualTo(expected));
  }
}
