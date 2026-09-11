#pragma warning disable CS1591
using FileFormat.Mdf;

namespace Compression.Tests.Mdf;

[TestFixture]
public class MdfInPlaceModifyTests {

  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int Mode1DataOffset = 16;

  private static byte[] BuildRawMode1Mdf(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    using var stream = new MemoryStream();
    var geometry = new MdfInPlaceModifier.SectorGeometry(RawSectorSize, Mode1DataOffset);
    for (var lba = 0; lba < sectorCount; ++lba) {
      var payload = new byte[Iso9660SectorSize];
      if (lba == 16) {
        payload[0] = 1;
        "CD001"u8.CopyTo(payload.AsSpan(1));
        payload[6] = 1;
      }
      MdfInPlaceModifier.AppendSector(stream, lba, payload, geometry);
    }
    return stream.ToArray();
  }

  private static byte[] BuildCookedMdf(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var image = new byte[sectorCount * Iso9660SectorSize];
    var pvdAt = 16 * Iso9660SectorSize;
    image[pvdAt] = 1;
    "CD001"u8.CopyTo(image.AsSpan(pvdAt + 1));
    image[pvdAt + 6] = 1;
    return image;
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_RewritesPayloadAndRegeneratesIntegrity() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(i => (byte)(i + 1)).ToArray();

    MdfInPlaceModifier.WriteSector(stream, 20, payload);

    var dataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.Multiple(() => {
      Assert.That(image.AsSpan(dataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
      Assert.That(image.AsSpan(20 * RawSectorSize, Mode1DataOffset).ToArray(),
        Is.EqualTo(original.AsSpan(20 * RawSectorSize, Mode1DataOffset).ToArray()),
        "sync/header framing must stay stable for an existing sector");
      Assert.That(image.AsSpan(dataAt + Iso9660SectorSize).ToArray(),
        Is.Not.EqualTo(original.AsSpan(dataAt + Iso9660SectorSize).ToArray()),
        "EDC/ECC must change when the protected payload changes");
    });
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_LeavesNeighbourSectorsByteIdentical() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);

    MdfInPlaceModifier.WriteSector(stream, 20, Enumerable.Repeat((byte)0xA5, Iso9660SectorSize).ToArray());

    for (var lba = 0; lba < image.Length / RawSectorSize; ++lba) {
      if (lba == 20) continue;
      var offset = lba * RawSectorSize;
      Assert.That(image.AsSpan(offset, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(offset, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly changed.");
    }
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Cooked_RewritesExactlyOneLogicalSector() {
    var image = BuildCookedMdf();
    var original = (byte[])image.Clone();
    using var stream = new MemoryStream(image, writable: true);
    var payload = Enumerable.Range(0, Iso9660SectorSize).Select(i => (byte)i).ToArray();

    MdfInPlaceModifier.WriteSector(stream, 22, payload);

    var at = 22 * Iso9660SectorSize;
    Assert.Multiple(() => {
      Assert.That(image.AsSpan(at, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
      Assert.That(image.AsSpan(0, at).ToArray(), Is.EqualTo(original.AsSpan(0, at).ToArray()));
      Assert.That(image.AsSpan(at + Iso9660SectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(at + Iso9660SectorSize).ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_PastEof_AppendsParityStampedMode1Sectors() {
    var original = BuildRawMode1Mdf(sectorCount: 20);
    using var stream = new MemoryStream();
    stream.Write(original);
    var payload = Enumerable.Repeat((byte)0x5A, Iso9660SectorSize).ToArray();

    MdfInPlaceModifier.WriteSector(stream, 25, payload);

    var grown = stream.ToArray();
    Assert.That(grown.Length, Is.EqualTo(26 * RawSectorSize));
    Assert.That(grown.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original));
    Assert.That(grown.AsSpan(25 * RawSectorSize + Mode1DataOffset, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    for (var lba = 20; lba <= 25; ++lba) {
      var offset = lba * RawSectorSize;
      Assert.Multiple(() => {
        Assert.That(grown[offset], Is.Zero, $"LBA {lba} sync start");
        Assert.That(grown[offset + 1], Is.EqualTo(0xFF), $"LBA {lba} sync body");
        Assert.That(grown[offset + 11], Is.Zero, $"LBA {lba} sync end");
        Assert.That(grown[offset + 15], Is.EqualTo(0x01), $"LBA {lba} mode");
        Assert.That(grown.AsSpan(offset + 2076, 276).ToArray(), Is.Not.All.Zero,
          $"LBA {lba} must carry generated ECC");
      });
    }
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_Raw_WipesPayloadAndRegeneratesIntegrity() {
    var image = BuildRawMode1Mdf();
    using var stream = new MemoryStream(image, writable: true);
    var geometry = MdfInPlaceModifier.DetectGeometry(stream);
    MdfInPlaceModifier.WriteSector(stream, 19, Enumerable.Repeat((byte)0x7E, Iso9660SectorSize).ToArray(), geometry);
    var afterFill = (byte[])image.Clone();

    Assert.That(MdfInPlaceModifier.ZeroSector(stream, 19, geometry), Is.True);

    var dataAt = 19 * RawSectorSize + Mode1DataOffset;
    Assert.Multiple(() => {
      Assert.That(image.AsSpan(dataAt, Iso9660SectorSize).ToArray(), Is.All.Zero);
      Assert.That(image.AsSpan(19 * RawSectorSize, Mode1DataOffset).ToArray(),
        Is.EqualTo(afterFill.AsSpan(19 * RawSectorSize, Mode1DataOffset).ToArray()));
      Assert.That(image.AsSpan(dataAt + Iso9660SectorSize).ToArray(),
        Is.Not.EqualTo(afterFill.AsSpan(dataAt + Iso9660SectorSize).ToArray()));
    });
  }

  [Test, Category("RoundTrip")]
  public void LowLevelSectorApi_ReplacesAndRemovesExplicitLbaEntries() {
    var image = BuildRawMode1Mdf();
    using var stream = new MemoryStream(image, writable: true);
    var name = MdfInPlaceModifier.FormatSectorEntryName(21);
    var first = Enumerable.Repeat((byte)0x11, Iso9660SectorSize).ToArray();
    var second = Enumerable.Repeat((byte)0x22, Iso9660SectorSize).ToArray();

    MdfInPlaceModifier.AddOrReplaceSectors(stream, [(name, first)]);
    MdfInPlaceModifier.AddOrReplaceSectors(stream, [(name, second)]);

    var geometry = MdfInPlaceModifier.DetectGeometry(stream);
    var readBack = new byte[Iso9660SectorSize];
    MdfInPlaceModifier.ReadSector(stream, 21, readBack, geometry);
    Assert.That(readBack, Is.EqualTo(second));

    MdfInPlaceModifier.RemoveSectors(stream, [name]);
    MdfInPlaceModifier.ReadSector(stream, 21, readBack, geometry);
    Assert.That(readBack, Is.All.Zero);
  }

  [Test, Category("Boundary")]
  public void ZeroSector_PastEof_ReturnsFalse() {
    using var stream = new MemoryStream(BuildRawMode1Mdf(), writable: true);
    Assert.That(MdfInPlaceModifier.ZeroSector(stream, 9999), Is.False);
  }

  [Test, Category("Boundary")]
  public void WriteSector_WrongPayloadLength_Throws() {
    using var stream = new MemoryStream(BuildRawMode1Mdf(), writable: true);
    Assert.Throws<ArgumentException>(() => MdfInPlaceModifier.WriteSector(stream, 20, new byte[100]));
  }

  [Test, Category("Boundary")]
  public void WriteSector_NegativeLba_Throws() {
    using var stream = new MemoryStream(BuildRawMode1Mdf(), writable: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      MdfInPlaceModifier.WriteSector(stream, -1, new byte[Iso9660SectorSize]));
  }

  [Test, Category("Boundary")]
  public void ExplicitSectorNameParser_RejectsNonSectorNames() {
    Assert.Multiple(() => {
      Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("sector-000123.bin", out var lba), Is.True);
      Assert.That(lba, Is.EqualTo(123));
      Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("FILE.TXT", out _), Is.False);
    });
  }
}
