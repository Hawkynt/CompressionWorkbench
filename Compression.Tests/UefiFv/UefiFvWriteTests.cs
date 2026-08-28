using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.UefiFv;

namespace Compression.Tests.UefiFv;

[TestFixture]
public sealed class UefiFvWriteTests {
  private const string DriverName = "11223344-5566-7788-99aa-bbccddeeff00_DRIVER.bin";
  private const string RawName = "01234567-89ab-cdef-0123-456789abcdef_RAW.bin";

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Create_ProducesChecksummedFixedCapacityFv() {
    var payload = Enumerable.Range(0, 333).Select(i => (byte)(i * 13)).ToArray();
    var descriptor = new UefiFvFormatDescriptor();
    using var image = new MemoryStream();

    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, payload)], new FormatCreateOptions());

    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(image.Length, Is.GreaterThan(payload.Length + 64 * 1024));

    var bytes = image.ToArray();
    var fv = UefiFvReader.Read(bytes);
    Assert.That(fv.Files, Has.Count.EqualTo(1));
    Assert.That(fv.Files[0].Contents, Is.EqualTo(payload));

    uint sum = 0;
    for (var i = 0; i < fv.Header.HeaderLength; i += 2)
      sum += BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i, 2));
    Assert.That((ushort)sum, Is.Zero, "FV header checksum must sum to zero");
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_AddReplaceRemove_ReusesErasedSpaceWithoutGrowingVolume() {
    var descriptor = new UefiFvFormatDescriptor();
    var first = Enumerable.Range(0, 257).Select(i => (byte)i).ToArray();
    var second = Enumerable.Range(0, 721).Select(i => (byte)(i * 5)).ToArray();
    var replacement = Enumerable.Range(0, 1023).Select(i => (byte)(255 - i)).ToArray();

    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(image,
      [ArchiveInputInfo.InMemory(DriverName, first)], new FormatCreateOptions());
    var originalLength = image.Length;

    var modifier = (IArchiveModifiable)descriptor;
    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory(RawName, second)]);
    Assert.That(image.Length, Is.EqualTo(originalLength));
    AssertFiles(image, (DriverName, first), (RawName, second));

    image.Position = 0;
    modifier.Add(image, [ArchiveInputInfo.InMemory(DriverName, replacement)]);
    Assert.That(image.Length, Is.EqualTo(originalLength));
    AssertFiles(image, (DriverName, replacement), (RawName, second));

    image.Position = 0;
    modifier.Remove(image, [RawName]);
    Assert.That(image.Length, Is.EqualTo(originalLength));
    AssertFiles(image, (DriverName, replacement));
  }

  private static void AssertFiles(MemoryStream image, params (string Name, byte[] Data)[] expected) {
    var bytes = image.ToArray();
    var fv = UefiFvReader.Read(bytes);
    var actual = fv.Files.Where(f => f.Type != 0xF0)
      .ToDictionary(f => $"{f.Name:D}_{UefiFvReader.ShortTypeTag(f.Type)}.bin", f => f.Contents,
        StringComparer.OrdinalIgnoreCase);
    Assert.That(actual.Keys, Is.EquivalentTo(expected.Select(e => e.Name)));
    foreach (var (name, data) in expected)
      Assert.That(actual[name], Is.EqualTo(data), name);
  }
}
