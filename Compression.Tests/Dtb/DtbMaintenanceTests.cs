#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Dtb;

namespace Compression.Tests.Dtb;

[TestFixture]
public sealed class DtbMaintenanceTests {
  private const ulong ReservationAddress = 0x0000_0001_2345_6000UL;
  private const ulong ReservationSize = 0x0000_0000_0002_0000UL;
  private const uint BootCpu = 0x17;

  [Test, Category("RoundTrip")]
  public void Shrink_RemovesSlackAndPreservesReservationsBootCpuAndProperties() {
    var image = BuildWithReservationAndSlack();
    var before = DtbReader.Read(image);
    AssertFixture(before);

    using var source = new MemoryStream(image, writable: false);
    using var target = new MemoryStream();
    ((IArchiveShrinkable)new DtbFormatDescriptor()).Shrink(source, target);

    Assert.That(target.Length, Is.LessThan(source.Length));
    var after = DtbReader.Read(target.ToArray());
    AssertFixture(after);
    AssertPropertiesEqual(before, after);
  }

  [Test, Category("RoundTrip")]
  public void Defrag_CanonicalizesInPlaceAndPreservesMetadata() {
    var image = BuildWithReservationAndSlack();
    var before = DtbReader.Read(image);
    using var stream = new MemoryStream();
    stream.Write(image);

    ((IArchiveDefragmentable)new DtbFormatDescriptor()).Defragment(stream);

    Assert.That(stream.Length, Is.LessThan(image.LongLength));
    var after = DtbReader.Read(stream.ToArray());
    AssertFixture(after);
    AssertPropertiesEqual(before, after);
  }

  private static byte[] BuildWithReservationAndSlack() {
    using var canonical = new MemoryStream();
    DtbWriter.Write(canonical, [
      ("soc/serial@1000/compatible.bin", "vendor,board\0"u8.ToArray()),
      ("soc/serial@1000/reg.bin", new byte[] { 0, 0, 0, 1, 0, 0, 0, 32 }),
    ]);
    var source = canonical.ToArray();

    var oldTotalSize = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(4, 4));
    var oldStructOffset = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(8, 4));
    var oldStringsOffset = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(12, 4));
    const int insertedReservationBytes = 16;
    const int trailingSlack = 128;

    var result = new byte[source.Length + insertedReservationBytes + trailingSlack];
    source.AsSpan(0, 40).CopyTo(result);
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(40, 8), ReservationAddress);
    BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(48, 8), ReservationSize);
    source.AsSpan(40).CopyTo(result.AsSpan(56));

    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), oldTotalSize + insertedReservationBytes);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), oldStructOffset + insertedReservationBytes);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12, 4), oldStringsOffset + insertedReservationBytes);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(28, 4), BootCpu);
    result.AsSpan(source.Length + insertedReservationBytes).Fill(0xA5);
    return result;
  }

  private static void AssertFixture(DtbReader.Fdt fdt) {
    Assert.That(fdt.Header.BootCpuidPhys, Is.EqualTo(BootCpu));
    Assert.That(fdt.Reservations, Has.Count.EqualTo(1));
    Assert.That(fdt.Reservations[0].Address, Is.EqualTo(ReservationAddress));
    Assert.That(fdt.Reservations[0].Size, Is.EqualTo(ReservationSize));
  }

  private static void AssertPropertiesEqual(DtbReader.Fdt before, DtbReader.Fdt after) {
    var expected = before.Properties
      .Select(p => (p.NodePath, p.Name, Data: Convert.ToHexString(p.Data)))
      .OrderBy(p => p.NodePath, StringComparer.Ordinal)
      .ThenBy(p => p.Name, StringComparer.Ordinal)
      .ToArray();
    var actual = after.Properties
      .Select(p => (p.NodePath, p.Name, Data: Convert.ToHexString(p.Data)))
      .OrderBy(p => p.NodePath, StringComparer.Ordinal)
      .ThenBy(p => p.Name, StringComparer.Ordinal)
      .ToArray();
    Assert.That(actual, Is.EqualTo(expected));
  }
}
