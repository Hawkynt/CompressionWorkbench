#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Mdf;

namespace Compression.Tests.Mdf;

[TestFixture]
public class MdfInPlaceModifyTests {

  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int Mode1DataOffset = 16;

  // ── geometry fixtures ───────────────────────────────────────────────

  /// <summary>
  /// Builds a raw 2 352-byte/sector Mode 1 MDF image: PVD at LBA 16 with
  /// <c>CD001</c>. MDF has no internal header or footer — it's a flat byte
  /// stream of sectors.
  /// </summary>
  private static byte[] BuildRawMode1Mdf(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var buf = new byte[sectorCount * RawSectorSize];

    for (var i = 0; i < buf.Length; i++)
      buf[i] = (byte)((i * 31 + 7) & 0xFF);

    Span<byte> sync = stackalloc byte[12] {
      0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    };
    for (var s = 0; s < sectorCount; s++) {
      sync.CopyTo(buf.AsSpan(s * RawSectorSize, 12));
      buf[s * RawSectorSize + 12] = 0;
      buf[s * RawSectorSize + 13] = 0;
      buf[s * RawSectorSize + 14] = (byte)s;
      buf[s * RawSectorSize + 15] = 0x01;
    }

    var pvdAt = 16 * RawSectorSize + Mode1DataOffset;
    buf[pvdAt + 0] = 1;
    buf[pvdAt + 1] = (byte)'C';
    buf[pvdAt + 2] = (byte)'D';
    buf[pvdAt + 3] = (byte)'0';
    buf[pvdAt + 4] = (byte)'0';
    buf[pvdAt + 5] = (byte)'1';
    buf[pvdAt + 6] = 1;

    return buf;
  }

  /// <summary>
  /// Builds a cooked 2 048-byte/sector MDF image.
  /// </summary>
  private static byte[] BuildCookedMdf(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var buf = new byte[sectorCount * Iso9660SectorSize];
    for (var i = 0; i < buf.Length; i++)
      buf[i] = (byte)((i * 17 + 3) & 0xFF);
    var pvdAt = 16 * Iso9660SectorSize;
    buf[pvdAt + 0] = 1;
    buf[pvdAt + 1] = (byte)'C';
    buf[pvdAt + 2] = (byte)'D';
    buf[pvdAt + 3] = (byte)'0';
    buf[pvdAt + 4] = (byte)'0';
    buf[pvdAt + 5] = (byte)'1';
    buf[pvdAt + 6] = 1;
    return buf;
  }

  // ── WriteSector — happy path ─────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_RewritesUserDataOnly_FramingPreserved() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i + 1) & 0xFF);

    MdfInPlaceModifier.WriteSector(ms, lba: 20, payload);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_OtherSectors_ByteIdentical() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0xA5);

    MdfInPlaceModifier.WriteSector(ms, lba: 20, payload);

    for (var lba = 0; lba < image.Length / RawSectorSize; lba++) {
      if (lba == 20) continue;
      var sectorOff = lba * RawSectorSize;
      Assert.That(image.AsSpan(sectorOff, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(sectorOff, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly changed.");
    }
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Cooked_RewritesUserDataAtSectorOffset() {
    var image = BuildCookedMdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

    MdfInPlaceModifier.WriteSector(ms, lba: 22, payload);

    var sectorAt = 22 * Iso9660SectorSize;
    Assert.That(image.AsSpan(sectorAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(image.AsSpan(0, sectorAt).ToArray(),
      Is.EqualTo(original.AsSpan(0, sectorAt).ToArray()));
    Assert.That(image.AsSpan(sectorAt + Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(sectorAt + Iso9660SectorSize).ToArray()));
  }

  // ── WriteSector — append path ────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void WriteSector_PastEof_AppendsWithCorrectFraming() {
    var image = BuildRawMode1Mdf(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x5A);

    MdfInPlaceModifier.WriteSector(ms, lba: 25, payload);

    var grown = ms.ToArray();
    Assert.That(grown.Length, Is.EqualTo(26 * RawSectorSize));

    Assert.That(grown.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original));

    var userDataAt = 25 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    for (var lba = 20; lba <= 24; lba++) {
      var sectorOff = lba * RawSectorSize;
      Assert.That(grown[sectorOff + 0], Is.EqualTo(0x00));
      Assert.That(grown[sectorOff + 1], Is.EqualTo(0xFF));
      Assert.That(grown[sectorOff + 15], Is.EqualTo(0x01), $"LBA {lba} mode byte");
    }
  }

  // ── ZeroSector ──────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void ZeroSector_WipesUserData_PreservesFraming() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.That(MdfInPlaceModifier.ZeroSector(ms, lba: 19), Is.True);

    var userDataAt = 19 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(),
      Is.EqualTo(new byte[Iso9660SectorSize]));

    Assert.That(image.AsSpan(19 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(19 * RawSectorSize, 16).ToArray()));

    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_PastEof_ReturnsFalse() {
    var image = BuildRawMode1Mdf();
    using var ms = new MemoryStream(image, writable: true);
    Assert.That(MdfInPlaceModifier.ZeroSector(ms, lba: 9999), Is.False);
  }

  // ── Descriptor-level Add/Remove ─────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RewritesNamedSector() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

    ((IArchiveModifiable)new MdfFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(MdfInPlaceModifier.FormatSectorEntryName(20), payload),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    for (var lba = 0; lba < image.Length / RawSectorSize; lba++) {
      if (lba == 20) continue;
      var off = lba * RawSectorSize;
      Assert.That(image.AsSpan(off, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(off, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly mutated.");
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_AppendsPastEofPreservingOriginal() {
    var image = BuildRawMode1Mdf(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x77);

    ((IArchiveModifiable)new MdfFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(MdfInPlaceModifier.FormatSectorEntryName(30), payload),
    ]);

    var grown = ms.ToArray();
    Assert.That(grown.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original));

    var userDataAt = 30 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_ZerosUserData_LeavesEverythingElseIdentical() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    ((IArchiveModifiable)new MdfFormatDescriptor()).Remove(ms, [
      MdfInPlaceModifier.FormatSectorEntryName(20),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(),
      Is.EqualTo(new byte[Iso9660SectorSize]));

    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    for (var lba = 0; lba < image.Length / RawSectorSize; lba++) {
      if (lba == 20) continue;
      var off = lba * RawSectorSize;
      Assert.That(image.AsSpan(off, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(off, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly mutated.");
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Replace_RewritesUserDataPreservingFramingAndNeighbours() {
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var v1 = new byte[Iso9660SectorSize];
    Array.Fill(v1, (byte)0x11);
    var v2 = new byte[Iso9660SectorSize];
    Array.Fill(v2, (byte)0x22);

    var modifier = (IArchiveModifiable)new MdfFormatDescriptor();
    modifier.Add(ms, [ArchiveInputInfo.InMemory(MdfInPlaceModifier.FormatSectorEntryName(21), v1)]);
    modifier.Add(ms, [ArchiveInputInfo.InMemory(MdfInPlaceModifier.FormatSectorEntryName(21), v2)]);

    var userDataAt = 21 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(v2));

    Assert.That(image.AsSpan(21 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(21 * RawSectorSize, 16).ToArray()));

    Assert.That(image.AsSpan(20 * RawSectorSize, RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, RawSectorSize).ToArray()));
    Assert.That(image.AsSpan(22 * RawSectorSize, RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(22 * RawSectorSize, RawSectorSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RoundTrip() {
    var image = BuildRawMode1Mdf();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0xAA ^ (i & 0xFF));

    MdfInPlaceModifier.WriteSector(ms, lba: 25, payload);

    ms.Position = 25L * RawSectorSize + Mode1DataOffset;
    var extract = new byte[Iso9660SectorSize];
    var got = 0;
    while (got < extract.Length) {
      var r = ms.Read(extract, got, extract.Length - got);
      if (r == 0) break;
      got += r;
    }
    Assert.That(extract, Is.EqualTo(payload));
  }

  // ── Boundary / contract ────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void WriteSector_WrongUserDataLength_Throws() {
    using var ms = new MemoryStream(BuildRawMode1Mdf(), writable: true);
    Assert.Throws<ArgumentException>(() =>
      MdfInPlaceModifier.WriteSector(ms, 20, new byte[100]));
  }

  [Test, Category("Boundary")]
  public void WriteSector_NegativeLba_Throws() {
    using var ms = new MemoryStream(BuildRawMode1Mdf(), writable: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      MdfInPlaceModifier.WriteSector(ms, -1, new byte[Iso9660SectorSize]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new MdfFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    Assert.That(new MdfFormatDescriptor(), Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void TryParseSectorEntryName_RoundTrips() {
    var name = MdfInPlaceModifier.FormatSectorEntryName(12345);
    Assert.That(MdfInPlaceModifier.TryParseSectorEntryName(name, out var lba), Is.True);
    Assert.That(lba, Is.EqualTo(12345));
  }

  [Test, Category("Boundary")]
  public void TryParseSectorEntryName_BogusNames_Rejected() {
    Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("readme.txt", out _), Is.False);
    Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("sector-abc.bin", out _), Is.False);
    Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("sector-.bin", out _), Is.False);
    Assert.That(MdfInPlaceModifier.TryParseSectorEntryName("", out _), Is.False);
  }

  [Test, Category("Boundary")]
  public void Descriptor_Add_NonSectorEntry_IsRefusedAndPreservesImage() {
    // This used to assert the image was untouched, and it was -- because the
    // entry was dropped on the floor. Leaving the image alone is right; doing it
    // without a word is not, and it made the shared "a fresh volume takes one
    // more file" check pass on a volume that had taken nothing.
    var image = BuildRawMode1Mdf();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new MdfFormatDescriptor()).Add(ms, [
        ArchiveInputInfo.InMemory("readme.txt", "hello"u8.ToArray()),
      ]));

    Assert.That(image, Is.EqualTo(original), "a refused add must leave the image as it was");
  }

  [Test, Category("RoundTrip")]
  public void DetectGeometry_FlatStream_NoHeaderOrFooter() {
    var image = BuildRawMode1Mdf(sectorCount: 32);
    using var ms = new MemoryStream(image, writable: true);
    var geom = MdfInPlaceModifier.DetectGeometry(ms);
    Assert.That(geom.SectorSize, Is.EqualTo(RawSectorSize));
    Assert.That(geom.DataOffset, Is.EqualTo(Mode1DataOffset));
  }
}
