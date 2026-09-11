#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgInPlaceModifyTests {
  private const int SectorSize = 2048;

  private static byte[] BuildNrg() {
    var descriptor = new NrgFormatDescriptor();
    using var image = new MemoryStream();
    ((IArchiveCreatable)descriptor).Create(
      image,
      [ArchiveInputInfo.InMemory("DATA.BIN", Enumerable.Range(0, 4096).Select(static i => (byte)i).ToArray())],
      new FormatCreateOptions());
    return image.ToArray();
  }

  private static int TrailerOffset(byte[] image)
    => checked((int)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(image.Length - 8)));

  [Test, Category("Regression")]
  public void DetectGeometry_UsesDescriptorOffset_NotFooterOffset() {
    var image = BuildNrg();
    using var stream = new MemoryStream(image, writable: true);

    var geometry = NrgInPlaceModifier.DetectGeometry(stream);

    Assert.Multiple(() => {
      Assert.That(geometry.SectorSize, Is.EqualTo(SectorSize));
      Assert.That(geometry.DataOffset, Is.Zero);
      Assert.That(geometry.DataAreaLength, Is.EqualTo(TrailerOffset(image)));
      Assert.That(geometry.DataAreaLength, Is.LessThan(image.Length - 12), "chunk table is not sector data");
    });
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_ExistingCookedSector_PreservesWholeDescriptor() {
    var image = BuildNrg();
    var trailer = TrailerOffset(image);
    var descriptorBytes = image.AsSpan(trailer).ToArray();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, SectorSize).Select(static i => (byte)(i * 29)).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 20, payload);

    Assert.That(image.AsSpan(20 * SectorSize, SectorSize).ToArray(), Is.EqualTo(payload));
    Assert.That(image.AsSpan(trailer).ToArray(), Is.EqualTo(descriptorBytes));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_ExistingCookedSector_PreservesWholeDescriptor() {
    var image = BuildNrg();
    var trailer = TrailerOffset(image);
    var descriptorBytes = image.AsSpan(trailer).ToArray();
    using var stream = new MemoryStream(image, writable: true);

    Assert.That(NrgInPlaceModifier.ZeroSector(stream, 20), Is.True);

    Assert.That(image.AsSpan(20 * SectorSize, SectorSize).ToArray(), Is.EqualTo(new byte[SectorSize]));
    Assert.That(image.AsSpan(trailer).ToArray(), Is.EqualTo(descriptorBytes));
  }

  [Test, Category("Regression")]
  public void WriteSector_PastTrackEnd_RefusesInsteadOfCorruptingChunkOffsets() {
    var image = BuildNrg();
    var trailer = TrailerOffset(image);
    using var stream = new MemoryStream();
    stream.Write(image);
    stream.Position = 0;

    var firstSectorPastTrack = trailer / SectorSize;
    var payload = new byte[SectorSize];

    Assert.That(
      () => NrgInPlaceModifier.WriteSector(stream, firstSectorPastTrack, payload),
      Throws.TypeOf<NotSupportedException>());
    Assert.That(stream.ToArray(), Is.EqualTo(image));
  }

  [Test]
  public void SectorEntryName_RoundTrips() {
    var name = NrgInPlaceModifier.FormatSectorEntryName(123);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName(name, out var lba), Is.True);
    Assert.That(lba, Is.EqualTo(123));
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("FILE.BIN", out _), Is.False);
  }
}
