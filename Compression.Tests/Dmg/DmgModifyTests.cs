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

  [Test, Category("HappyPath")]
  public void Descriptor_ExposesSupportedMaintenanceVerbs() {
    var descriptor = new DmgFormatDescriptor();

    Assert.That(descriptor, Is.InstanceOf<IArchiveDefragmentable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveShrinkable>());
    Assert.That(descriptor, Is.InstanceOf<IArchiveLayoutMap>());
    Assert.That(descriptor, Is.InstanceOf<IWipeEmpty>());
    Assert.That(descriptor, Is.InstanceOf<IArchivePurgeable>());
    Assert.That(descriptor, Is.Not.InstanceOf<ILayoutOptimizable>());
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Remove_LayoutAndWipe_ExposeAndZeroDeadPayload() {
    var descriptor = new DmgFormatDescriptor();
    var a = Enumerable.Repeat((byte)0xA1, 512).ToArray();
    var b = Enumerable.Repeat((byte)0xB2, 512).ToArray();
    using var image = CreateImage(descriptor, ("A.BIN", a), ("B.BIN", b));

    ((IArchiveModifiable)descriptor).Remove(image, ["B.BIN"]);

    image.Position = 0;
    var layout = ((IArchiveLayoutMap)descriptor).EnumerateLayout(image).ToArray();
    Assert.That(layout.Any(e => e.Kind == DefragBlockKind.Free && e.Offset <= 512 && e.Offset + e.Length >= 1024), Is.True);
    Assert.That(image.ToArray().AsSpan(512, 512).ToArray(), Is.All.EqualTo((byte)0xB2));

    image.Position = 0;
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);
    Assert.That(wiped, Is.EqualTo(512));
    Assert.That(image.ToArray().AsSpan(512, 512).ToArray(), Is.All.EqualTo((byte)0x00));
    AssertPayload(image, "A.BIN", a);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Defragment_AfterRemove_RebuildsWithoutDeadPayload() {
    var descriptor = new DmgFormatDescriptor();
    var a = Enumerable.Repeat((byte)0x31, 512).ToArray();
    var b = Enumerable.Repeat((byte)0x42, 512).ToArray();
    using var image = CreateImage(descriptor, ("A.BIN", a), ("B.BIN", b));

    ((IArchiveModifiable)descriptor).Remove(image, ["B.BIN"]);
    var before = image.Length;

    image.Position = 0;
    ((IArchiveDefragmentable)descriptor).Defragment(image);

    Assert.That(image.Length, Is.LessThan(before));
    AssertPayload(image, "A.BIN", a);
    image.Position = 0;
    Assert.That(((IArchiveLayoutMap)descriptor).EnumerateLayout(image).Any(e => e.Kind == DefragBlockKind.Free), Is.False);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Shrink_AfterRemove_EmitsTightImage() {
    var descriptor = new DmgFormatDescriptor();
    var a = Enumerable.Repeat((byte)0x51, 512).ToArray();
    var b = Enumerable.Repeat((byte)0x62, 512).ToArray();
    using var image = CreateImage(descriptor, ("A.BIN", a), ("B.BIN", b));

    ((IArchiveModifiable)descriptor).Remove(image, ["B.BIN"]);
    var before = image.Length;
    using var shrunk = new MemoryStream();

    image.Position = 0;
    ((IArchiveShrinkable)descriptor).Shrink(image, shrunk);

    Assert.That(shrunk.Length, Is.LessThan(before));
    AssertPayload(shrunk, "A.BIN", a);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Purge_LeavesValidEmptyDmg() {
    var descriptor = new DmgFormatDescriptor();
    using var image = CreateImage(descriptor,
      ("A.BIN", Enumerable.Repeat((byte)0x71, 512).ToArray()),
      ("B.BIN", Enumerable.Repeat((byte)0x82, 512).ToArray()));

    image.Position = 0;
    ((IArchivePurgeable)descriptor).Purge(image);

    image.Position = 0;
    using var reader = new DmgReader(image);
    Assert.That(reader.Entries, Is.Empty);
  }

  private static MemoryStream CreateImage(DmgFormatDescriptor descriptor,
      params (string Name, byte[] Data)[] entries) {
    var image = new MemoryStream();
    descriptor.Create(image,
      entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToArray(),
      new FormatCreateOptions());
    image.Position = 0;
    return image;
  }

  private static void AssertPayload(MemoryStream image, string name, byte[] expected) {
    image.Position = 0;
    using var reader = new DmgReader(image);
    var entry = reader.Entries.Single(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    Assert.That(entry.Size, Is.EqualTo(expected.Length));
    Assert.That(reader.Extract(entry), Is.EqualTo(expected));
  }
}
