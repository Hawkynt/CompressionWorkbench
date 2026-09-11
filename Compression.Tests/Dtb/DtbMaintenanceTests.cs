using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

[TestFixture]
public sealed class DtbMaintenanceTests {
  [Test, Category("RoundTrip")]
  public void CreateAndModify_PreserveEmptyNodesReservationsAndBootCpu() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);

    image.Position = 0;
    descriptor.Add(image, [ArchiveInputInfo.InMemory("soc/uart@1000/reg.bin", new byte[] { 0, 0, 0, 1 })]);
    image.Position = 0;
    descriptor.Remove(image, ["soc/uart@1000/reg.bin"]);

    var fdt = Parse(image);
    Assert.Multiple(() => {
      Assert.That(fdt.Header.BootCpuidPhys, Is.EqualTo(0x2Au));
      Assert.That(fdt.Reservations, Is.EqualTo(new[] { new DtbReader.Reservation(0x10000000, 0x1000) }));
      Assert.That(fdt.Nodes.Select(node => node.Path), Does.Contain("/soc/empty"));
      Assert.That(fdt.Nodes.Select(node => node.Path), Does.Contain("/soc/uart@1000"));
      Assert.That(fdt.Properties.Any(property => property.Name == "reg"), Is.False);
    });
  }

  [Test, Category("EdgeCase")]
  public void Reader_RejectsStructureThatEscapesDeclaredStructureSize() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);
    var bytes = image.ToArray();

    // Only the FDT_BEGIN_NODE token remains inside size_dt_struct. The previous
    // reader kept walking into following blocks instead of respecting the size.
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(36, 4), 4);

    Assert.That(() => DtbReader.Read(bytes), Throws.InstanceOf<InvalidDataException>());
  }

  [Test, Category("RoundTrip")]
  public void ZeroLengthBooleanProperty_RemainsZeroLengthAcrossArchiveView() {
    var descriptor = new DtbFormatDescriptor();
    using var source = new MemoryStream();
    descriptor.Create(source, [ArchiveInputInfo.InMemory("_root/wakeup-source.bin", [])], new FormatCreateOptions());

    source.Position = 0;
    var entries = descriptor.List(source, null);
    Assert.That(entries.Select(entry => entry.Name), Does.Contain("_root/wakeup-source.bin"));

    using var rebuilt = new MemoryStream();
    descriptor.Create(rebuilt, [ArchiveInputInfo.InMemory("_root/wakeup-source.bin", [])], new FormatCreateOptions());
    var property = Parse(rebuilt).Properties.Single(item => item.Name == "wakeup-source");
    Assert.That(property.Data, Is.Empty);
  }

  [Test, Category("RoundTrip")]
  public void Defragment_PacksGrowthSlackWithoutChangingTree() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);
    AppendGrowthSlack(image, 64, 0xA5);
    var before = Parse(image);
    var originalLength = image.Length;

    ((IArchiveDefragmentable)descriptor).Defragment(image);

    var after = Parse(image);
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(originalLength - 64));
      Assert.That(after.Header.TotalSize, Is.EqualTo((uint)image.Length));
      Assert.That(after.Header.BootCpuidPhys, Is.EqualTo(before.Header.BootCpuidPhys));
      Assert.That(after.Reservations, Is.EqualTo(before.Reservations));
      Assert.That(after.Nodes.Select(node => node.Path), Is.EqualTo(before.Nodes.Select(node => node.Path)));
      Assert.That(PropertySnapshot(after), Is.EqualTo(PropertySnapshot(before)));
    });
  }

  [Test, Category("RoundTrip")]
  public void Wipe_ZeroesOnlyProvenGrowthSlackAndDoesNotResize() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);
    var packedLength = image.Length;
    AppendGrowthSlack(image, 32, 0xA5);
    var originalLength = image.Length;

    image.Position = 0;
    var wiped = ((IWipeEmpty)descriptor).WipeUnusedSpace(image);

    var bytes = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(wiped, Is.EqualTo(32));
      Assert.That(image.Length, Is.EqualTo(originalLength));
      Assert.That(bytes.AsSpan(checked((int)packedLength), 32).ToArray(), Is.All.EqualTo((byte)0));
      Assert.That(Parse(image).Properties, Is.Not.Empty);
    });
  }

  [Test, Category("RoundTrip")]
  public void Shrink_WritesPackedMinimumAndPreservesSemantics() {
    var descriptor = new DtbFormatDescriptor();
    using var source = CreateRichImage(descriptor);
    AppendGrowthSlack(source, 48, 0x5A);
    var before = Parse(source);
    using var target = new MemoryStream();

    ((IArchiveShrinkable)descriptor).Shrink(source, target);

    var after = Parse(target);
    Assert.Multiple(() => {
      Assert.That(target.Length, Is.EqualTo(source.Length - 48));
      Assert.That(after.Header.TotalSize, Is.EqualTo((uint)target.Length));
      Assert.That(after.Header.BootCpuidPhys, Is.EqualTo(before.Header.BootCpuidPhys));
      Assert.That(after.Reservations, Is.EqualTo(before.Reservations));
      Assert.That(after.Nodes.Select(node => node.Path), Is.EqualTo(before.Nodes.Select(node => node.Path)));
      Assert.That(PropertySnapshot(after), Is.EqualTo(PropertySnapshot(before)));
    });
  }

  [Test, Category("RoundTrip")]
  public void Purge_LeavesValidRootOnlyBlobAndPreservesContainerMetadata() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);

    ((IArchivePurgeable)descriptor).Purge(image);

    var fdt = Parse(image);
    Assert.Multiple(() => {
      Assert.That(fdt.Nodes.Select(node => node.Path), Is.EqualTo(new[] { "/" }));
      Assert.That(fdt.Properties, Is.Empty);
      Assert.That(fdt.Header.BootCpuidPhys, Is.EqualTo(0x2Au));
      Assert.That(fdt.Reservations, Is.EqualTo(new[] { new DtbReader.Reservation(0x10000000, 0x1000) }));
    });
  }

  [Test, Category("RoundTrip")]
  public void Maintenance_PreservesBytesOutsideHeaderTotalSize() {
    var descriptor = new DtbFormatDescriptor();
    using var image = CreateRichImage(descriptor);
    var suffix = "outer-container-sentinel"u8.ToArray();
    image.Position = image.Length;
    image.Write(suffix);
    var lengthWithSuffix = image.Length;

    ((IArchiveDefragmentable)descriptor).Defragment(image);

    var fdt = Parse(image);
    var bytes = image.ToArray();
    Assert.Multiple(() => {
      Assert.That(image.Length, Is.EqualTo(lengthWithSuffix));
      Assert.That(bytes.AsSpan(checked((int)fdt.Header.TotalSize)).ToArray(), Is.EqualTo(suffix));
      Assert.That(((IArchiveLayoutMap)descriptor).EnumerateLayout(image).Last().Kind,
        Is.EqualTo(DefragBlockKind.MetadataReserved));
    });
  }

  private static MemoryStream CreateRichImage(DtbFormatDescriptor descriptor) {
    var metadata = Encoding.UTF8.GetBytes("""
      [fdt]
      boot_cpuid_phys = 0x0000002A

      [memory_reservations]
      reserve_0 = 0x0000000010000000 + 0x0000000000001000 bytes
      """);
    var image = new MemoryStream();
    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("metadata.ini", metadata),
      new ArchiveInputInfo("", "soc/empty/", true),
      ArchiveInputInfo.InMemory("soc/uart@1000/status.txt", "okay"u8.ToArray()),
      ArchiveInputInfo.InMemory("_root/wakeup-source.bin", []),
    ], new FormatCreateOptions());
    image.Position = 0;
    return image;
  }

  private static void AppendGrowthSlack(MemoryStream image, int count, byte value) {
    var oldTotal = checked((uint)Parse(image).Header.TotalSize);
    image.Position = image.Length;
    image.Write(Enumerable.Repeat(value, count).Select(item => (byte)item).ToArray());
    var newTotal = checked(oldTotal + (uint)count);
    Span<byte> encoded = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(encoded, newTotal);
    image.Position = 4;
    image.Write(encoded);
    image.Position = 0;
  }

  private static DtbReader.Fdt Parse(MemoryStream image)
    => DtbReader.Read(image.ToArray());

  private static string[] PropertySnapshot(DtbReader.Fdt fdt)
    => fdt.Properties
      .Select(property => $"{property.NodePath}\0{property.Name}\0{Convert.ToHexString(property.Data)}")
      .ToArray();
}
