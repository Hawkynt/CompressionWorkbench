#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgInPlaceModifyTests {
  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int Mode1DataOffset = 16;
  private const int Ner5FooterSize = 12;
  private const int NeroFooterSize = 8;

  private static byte[] BuildRawMode1Nrg(int sectorCount = 32) {
    if (sectorCount <= 16)
      sectorCount = 20;
    var dataLength = sectorCount * RawSectorSize;
    var image = new byte[dataLength + Ner5FooterSize];

    for (var i = 0; i < dataLength; ++i)
      image[i] = (byte)((i * 31 + 7) & 0xFF);

    ReadOnlySpan<byte> sync = [
      0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
      0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    ];
    for (var sector = 0; sector < sectorCount; ++sector) {
      sync.CopyTo(image.AsSpan(sector * RawSectorSize, 12));
      image[sector * RawSectorSize + 12] = 0;
      image[sector * RawSectorSize + 13] = 0;
      image[sector * RawSectorSize + 14] = (byte)sector;
      image[sector * RawSectorSize + 15] = 0x01;
    }

    var pvdAt = 16 * RawSectorSize + Mode1DataOffset;
    image[pvdAt] = 1;
    "CD001"u8.CopyTo(image.AsSpan(pvdAt + 1));
    image[pvdAt + 6] = 1;

    "NER5"u8.CopyTo(image.AsSpan(dataLength));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(dataLength + 4), checked((ulong)dataLength));
    return image;
  }

  private static byte[] BuildCookedNrg(int sectorCount = 32) {
    if (sectorCount <= 16)
      sectorCount = 20;
    var dataLength = sectorCount * Iso9660SectorSize;
    var image = new byte[dataLength + Ner5FooterSize];

    for (var i = 0; i < dataLength; ++i)
      image[i] = (byte)((i * 17 + 3) & 0xFF);

    var pvdAt = 16 * Iso9660SectorSize;
    image[pvdAt] = 1;
    "CD001"u8.CopyTo(image.AsSpan(pvdAt + 1));
    image[pvdAt + 6] = 1;

    "NER5"u8.CopyTo(image.AsSpan(dataLength));
    BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(dataLength + 4), checked((ulong)dataLength));
    return image;
  }

  private static byte[] BuildRawMode1NrgV1(int sectorCount = 32) {
    if (sectorCount <= 16)
      sectorCount = 20;
    var dataLength = sectorCount * RawSectorSize;
    var image = new byte[dataLength + NeroFooterSize];

    for (var i = 0; i < dataLength; ++i)
      image[i] = (byte)((i * 31 + 7) & 0xFF);

    ReadOnlySpan<byte> sync = [
      0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
      0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    ];
    for (var sector = 0; sector < sectorCount; ++sector) {
      sync.CopyTo(image.AsSpan(sector * RawSectorSize, 12));
      image[sector * RawSectorSize + 14] = (byte)sector;
      image[sector * RawSectorSize + 15] = 0x01;
    }

    var pvdAt = 16 * RawSectorSize + Mode1DataOffset;
    image[pvdAt] = 1;
    "CD001"u8.CopyTo(image.AsSpan(pvdAt + 1));
    image[pvdAt + 6] = 1;

    "NERO"u8.CopyTo(image.AsSpan(dataLength));
    BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(dataLength + 4), checked((uint)dataLength));
    return image;
  }

  private static byte[] BuildRealNrg() {
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

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_RewritesUserDataOnly_FramingPreserved() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(static i => (byte)(i + 1)).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 20, payload);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
    Assert.That(image.AsSpan(20 * RawSectorSize, Mode1DataOffset).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, Mode1DataOffset).ToArray()));
    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize, RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize, RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
    Assert.That(image.AsSpan(image.Length - Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_OtherSectors_ByteIdentical() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Repeat((byte)0xA5, Iso9660SectorSize).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 20, payload);

    var sectorCount = (image.Length - Ner5FooterSize) / RawSectorSize;
    for (var lba = 0; lba < sectorCount; ++lba) {
      if (lba == 20)
        continue;
      var offset = lba * RawSectorSize;
      Assert.That(image.AsSpan(offset, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(offset, RawSectorSize).ToArray()), $"LBA {lba} unexpectedly changed.");
    }
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Cooked_RewritesUserDataAtSectorOffset() {
    var image = BuildCookedNrg();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(static i => (byte)i).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 22, payload);

    var sectorAt = 22 * Iso9660SectorSize;
    Assert.That(image.AsSpan(sectorAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
    Assert.That(image.AsSpan(0, sectorAt).ToArray(), Is.EqualTo(original.AsSpan(0, sectorAt).ToArray()));
    Assert.That(image.AsSpan(sectorAt + Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(sectorAt + Iso9660SectorSize).ToArray()));
  }

  [Test, Category("Regression")]
  public void WriteSector_PastTrackEnd_RefusesInsteadOfCorruptingChunkOffsets() {
    var image = BuildRawMode1Nrg(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream();
    stream.Write(image);
    stream.Position = 0;

    Assert.That(
      () => NrgInPlaceModifier.WriteSector(stream, 25, new byte[Iso9660SectorSize]),
      Throws.TypeOf<NotSupportedException>());
    Assert.That(stream.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_WipesUserData_PreservesFramingAndFooter() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);

    Assert.That(NrgInPlaceModifier.ZeroSector(stream, 19), Is.True);

    var userDataAt = 19 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(new byte[Iso9660SectorSize]));
    Assert.That(image.AsSpan(19 * RawSectorSize, Mode1DataOffset).ToArray(),
      Is.EqualTo(original.AsSpan(19 * RawSectorSize, Mode1DataOffset).ToArray()));
    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize, RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize, RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
    Assert.That(image.AsSpan(image.Length - Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_PastEof_ReturnsFalse() {
    var image = BuildRawMode1Nrg();
    using var stream = new MemoryStream(image, writable: true);
    Assert.That(NrgInPlaceModifier.ZeroSector(stream, 9999), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void DetectGeometry_StripsNeroV1Footer_FromDataAreaLength() {
    var image = BuildRawMode1NrgV1();
    using var stream = new MemoryStream(image, writable: true);
    var geometry = NrgInPlaceModifier.DetectGeometry(stream);

    Assert.Multiple(() => {
      Assert.That(geometry.SectorSize, Is.EqualTo(RawSectorSize));
      Assert.That(geometry.DataOffset, Is.EqualTo(Mode1DataOffset));
      Assert.That(geometry.DataAreaLength, Is.EqualTo(32 * RawSectorSize));
    });
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_NeroV1_FooterPreserved() {
    var image = BuildRawMode1NrgV1();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Repeat((byte)0x33, Iso9660SectorSize).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 20, payload);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
    Assert.That(image.AsSpan(image.Length - NeroFooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - NeroFooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RoundTripsSectorBytes() {
    var image = BuildRawMode1Nrg();
    using var stream = new MemoryStream();
    stream.Write(image);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(static i => (byte)(0xAA ^ i)).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 25, payload);

    stream.Position = 25L * RawSectorSize + Mode1DataOffset;
    var extracted = new byte[Iso9660SectorSize];
    stream.ReadExactly(extracted);
    Assert.That(extracted, Is.EqualTo(payload));
  }

  [Test, Category("Regression")]
  public void DetectGeometry_UsesDescriptorOffset_NotFooterOffset() {
    var image = BuildRealNrg();
    using var stream = new MemoryStream(image, writable: true);
    var geometry = NrgInPlaceModifier.DetectGeometry(stream);

    Assert.Multiple(() => {
      Assert.That(geometry.SectorSize, Is.EqualTo(Iso9660SectorSize));
      Assert.That(geometry.DataOffset, Is.Zero);
      Assert.That(geometry.DataAreaLength, Is.EqualTo(TrailerOffset(image)));
      Assert.That(geometry.DataAreaLength, Is.LessThan(image.Length - Ner5FooterSize), "chunk table is not sector data");
    });
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_RealChunkedImage_PreservesWholeDescriptor() {
    var image = BuildRealNrg();
    var trailer = TrailerOffset(image);
    var descriptorBytes = image.AsSpan(trailer).ToArray();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(static i => (byte)(i * 29)).ToArray();

    NrgInPlaceModifier.WriteSector(stream, 20, payload);

    Assert.That(image.AsSpan(20 * Iso9660SectorSize, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
    Assert.That(image.AsSpan(trailer).ToArray(), Is.EqualTo(descriptorBytes));
  }

  [Test, Category("Boundary")]
  public void WriteSector_WrongUserDataLength_Throws() {
    using var stream = new MemoryStream(BuildRawMode1Nrg(), writable: true);
    Assert.Throws<ArgumentException>(() => NrgInPlaceModifier.WriteSector(stream, 20, new byte[100]));
  }

  [Test, Category("Boundary")]
  public void WriteSector_NegativeLba_Throws() {
    using var stream = new MemoryStream(BuildRawMode1Nrg(), writable: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      NrgInPlaceModifier.WriteSector(stream, -1, new byte[Iso9660SectorSize]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var descriptor = new NrgFormatDescriptor();
    Assert.That(descriptor.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable()
    => Assert.That(new NrgFormatDescriptor(), Is.InstanceOf<IArchiveModifiable>());

  [Test, Category("HappyPath")]
  public void TryParseSectorEntryName_RoundTrips() {
    var name = NrgInPlaceModifier.FormatSectorEntryName(12345);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName(name, out var lba), Is.True);
    Assert.That(lba, Is.EqualTo(12345));
  }

  [Test, Category("Boundary")]
  public void TryParseSectorEntryName_BogusNames_Rejected() {
    Assert.Multiple(() => {
      Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("readme.txt", out _), Is.False);
      Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("sector-abc.bin", out _), Is.False);
      Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("sector-.bin", out _), Is.False);
      Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("", out _), Is.False);
    });
  }

  [Test, Category("Boundary")]
  public void DetectGeometry_StripsNer5Footer_FromDataAreaLength() {
    var image = BuildRawMode1Nrg();
    using var stream = new MemoryStream(image, writable: true);
    var geometry = NrgInPlaceModifier.DetectGeometry(stream);

    Assert.Multiple(() => {
      Assert.That(geometry.SectorSize, Is.EqualTo(RawSectorSize));
      Assert.That(geometry.DataOffset, Is.EqualTo(Mode1DataOffset));
      Assert.That(geometry.DataAreaLength, Is.EqualTo(32 * RawSectorSize));
    });
  }
}
