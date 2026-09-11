#pragma warning disable CS1591
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

  // ── geometry fixtures ───────────────────────────────────────────────

  /// <summary>
  /// Builds a raw 2 352-byte/sector Mode 1 NRG v2 image: PVD at LBA 16 with
  /// <c>CD001</c> + 12-byte "NER5" footer at EOF.
  /// </summary>
  private static byte[] BuildRawMode1Nrg(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var dataLen = sectorCount * RawSectorSize;
    var buf = new byte[dataLen + Ner5FooterSize];

    for (var i = 0; i < dataLen; i++)
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

    // NER5 footer: "NER5" + uint64 BE chunk-table offset (points at end of data).
    buf[dataLen + 0] = (byte)'N';
    buf[dataLen + 1] = (byte)'E';
    buf[dataLen + 2] = (byte)'R';
    buf[dataLen + 3] = (byte)'5';
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
      buf.AsSpan(dataLen + 4, 8), (ulong)dataLen);

    return buf;
  }

  /// <summary>
  /// Builds a cooked 2 048-byte/sector NRG v2 image.
  /// </summary>
  private static byte[] BuildCookedNrg(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var dataLen = sectorCount * Iso9660SectorSize;
    var buf = new byte[dataLen + Ner5FooterSize];

    for (var i = 0; i < dataLen; i++)
      buf[i] = (byte)((i * 17 + 3) & 0xFF);

    var pvdAt = 16 * Iso9660SectorSize;
    buf[pvdAt + 0] = 1;
    buf[pvdAt + 1] = (byte)'C';
    buf[pvdAt + 2] = (byte)'D';
    buf[pvdAt + 3] = (byte)'0';
    buf[pvdAt + 4] = (byte)'0';
    buf[pvdAt + 5] = (byte)'1';
    buf[pvdAt + 6] = 1;

    buf[dataLen + 0] = (byte)'N';
    buf[dataLen + 1] = (byte)'E';
    buf[dataLen + 2] = (byte)'R';
    buf[dataLen + 3] = (byte)'5';
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(
      buf.AsSpan(dataLen + 4, 8), (ulong)dataLen);
    return buf;
  }

  /// <summary>
  /// Builds a raw NRG v1 image: 8-byte "NERO" footer at EOF.
  /// </summary>
  private static byte[] BuildRawMode1NrgV1(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var dataLen = sectorCount * RawSectorSize;
    var buf = new byte[dataLen + NeroFooterSize];

    for (var i = 0; i < dataLen; i++)
      buf[i] = (byte)((i * 31 + 7) & 0xFF);

    Span<byte> sync = stackalloc byte[12] {
      0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    };
    for (var s = 0; s < sectorCount; s++) {
      sync.CopyTo(buf.AsSpan(s * RawSectorSize, 12));
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

    buf[dataLen + 0] = (byte)'N';
    buf[dataLen + 1] = (byte)'E';
    buf[dataLen + 2] = (byte)'R';
    buf[dataLen + 3] = (byte)'O';
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
      buf.AsSpan(dataLen + 4, 4), (uint)dataLen);
    return buf;
  }

  // ── WriteSector — happy path ─────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_RewritesUserDataOnly_FramingPreserved() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i + 1) & 0xFF);

    NrgInPlaceModifier.WriteSector(ms, lba: 20, payload);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));

    // NER5 footer byte-identical.
    Assert.That(image.AsSpan(image.Length - Ner5FooterSize, Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize, Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_OtherSectors_ByteIdentical() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0xA5);

    NrgInPlaceModifier.WriteSector(ms, lba: 20, payload);

    var sectorCount = (image.Length - Ner5FooterSize) / RawSectorSize;
    for (var lba = 0; lba < sectorCount; lba++) {
      if (lba == 20) continue;
      var sectorOff = lba * RawSectorSize;
      Assert.That(image.AsSpan(sectorOff, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(sectorOff, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly changed.");
    }
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Cooked_RewritesUserDataAtSectorOffset() {
    var image = BuildCookedNrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

    NrgInPlaceModifier.WriteSector(ms, lba: 22, payload);

    var sectorAt = 22 * Iso9660SectorSize;
    Assert.That(image.AsSpan(sectorAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(image.AsSpan(0, sectorAt).ToArray(),
      Is.EqualTo(original.AsSpan(0, sectorAt).ToArray()));
    Assert.That(image.AsSpan(sectorAt + Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(sectorAt + Iso9660SectorSize).ToArray()));
  }

  // ── WriteSector — append path ────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void WriteSector_PastEof_AppendsWithCorrectFraming_FooterRelocated() {
    var image = BuildRawMode1Nrg(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x5A);

    NrgInPlaceModifier.WriteSector(ms, lba: 25, payload);

    var grown = ms.ToArray();
    Assert.That(grown.Length, Is.EqualTo(26 * RawSectorSize + Ner5FooterSize));

    Assert.That(grown.AsSpan(0, 20 * RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(0, 20 * RawSectorSize).ToArray()));

    var userDataAt = 25 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    for (var lba = 20; lba <= 24; lba++) {
      var sectorOff = lba * RawSectorSize;
      Assert.That(grown[sectorOff + 0], Is.EqualTo(0x00));
      Assert.That(grown[sectorOff + 1], Is.EqualTo(0xFF));
      Assert.That(grown[sectorOff + 15], Is.EqualTo(0x01), $"LBA {lba} mode byte");
    }

    Assert.That(grown.AsSpan(grown.Length - Ner5FooterSize, Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize, Ner5FooterSize).ToArray()));
  }

  // ── ZeroSector ──────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void ZeroSector_WipesUserData_PreservesFramingAndFooter() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.That(NrgInPlaceModifier.ZeroSector(ms, lba: 19), Is.True);

    var userDataAt = 19 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(),
      Is.EqualTo(new byte[Iso9660SectorSize]));

    Assert.That(image.AsSpan(19 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(19 * RawSectorSize, 16).ToArray()));

    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));

    Assert.That(image.AsSpan(image.Length - Ner5FooterSize, Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize, Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_PastEof_ReturnsFalse() {
    var image = BuildRawMode1Nrg();
    using var ms = new MemoryStream(image, writable: true);
    Assert.That(NrgInPlaceModifier.ZeroSector(ms, lba: 9999), Is.False);
  }

  // ── NRG v1 (NERO) footer ───────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void DetectGeometry_StripsNeroV1Footer_FromDataAreaLength() {
    var image = BuildRawMode1NrgV1(sectorCount: 32);
    using var ms = new MemoryStream(image, writable: true);
    var geom = NrgInPlaceModifier.DetectGeometry(ms);
    Assert.That(geom.SectorSize, Is.EqualTo(RawSectorSize));
    Assert.That(geom.DataOffset, Is.EqualTo(Mode1DataOffset));
    Assert.That(geom.DataAreaLength, Is.EqualTo(32 * RawSectorSize));
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_NeroV1_FooterPreserved() {
    var image = BuildRawMode1NrgV1();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x33);

    NrgInPlaceModifier.WriteSector(ms, lba: 20, payload);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(image.AsSpan(image.Length - NeroFooterSize, NeroFooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - NeroFooterSize, NeroFooterSize).ToArray()));
  }

  // ── Descriptor-level Add/Remove ─────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RewritesNamedSector() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

    ((IArchiveModifiable)new NrgFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(NrgInPlaceModifier.FormatSectorEntryName(20), payload),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    var sectorCount = (image.Length - Ner5FooterSize) / RawSectorSize;
    for (var lba = 0; lba < sectorCount; lba++) {
      if (lba == 20) continue;
      var off = lba * RawSectorSize;
      Assert.That(image.AsSpan(off, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(off, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly mutated.");
    }

    Assert.That(image.AsSpan(image.Length - Ner5FooterSize, Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize, Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_AppendsPastEofPreservingDataAndFooter() {
    var image = BuildRawMode1Nrg(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x77);

    ((IArchiveModifiable)new NrgFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(NrgInPlaceModifier.FormatSectorEntryName(30), payload),
    ]);

    var grown = ms.ToArray();
    Assert.That(grown.AsSpan(0, 20 * RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(0, 20 * RawSectorSize).ToArray()));

    var userDataAt = 30 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    Assert.That(grown.AsSpan(grown.Length - Ner5FooterSize, Ner5FooterSize).ToArray(),
      Is.EqualTo(original.AsSpan(original.Length - Ner5FooterSize, Ner5FooterSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_ZerosUserData_LeavesEverythingElseIdentical() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    ((IArchiveModifiable)new NrgFormatDescriptor()).Remove(ms, [
      NrgInPlaceModifier.FormatSectorEntryName(20),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(),
      Is.EqualTo(new byte[Iso9660SectorSize]));

    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    var sectorCount = (image.Length - Ner5FooterSize) / RawSectorSize;
    for (var lba = 0; lba < sectorCount; lba++) {
      if (lba == 20) continue;
      var off = lba * RawSectorSize;
      Assert.That(image.AsSpan(off, RawSectorSize).ToArray(),
        Is.EqualTo(original.AsSpan(off, RawSectorSize).ToArray()),
        $"LBA {lba} unexpectedly mutated.");
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Replace_RewritesUserDataPreservingFramingAndNeighbours() {
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var v1 = new byte[Iso9660SectorSize];
    Array.Fill(v1, (byte)0x11);
    var v2 = new byte[Iso9660SectorSize];
    Array.Fill(v2, (byte)0x22);

    var modifier = (IArchiveModifiable)new NrgFormatDescriptor();
    modifier.Add(ms, [ArchiveInputInfo.InMemory(NrgInPlaceModifier.FormatSectorEntryName(21), v1)]);
    modifier.Add(ms, [ArchiveInputInfo.InMemory(NrgInPlaceModifier.FormatSectorEntryName(21), v2)]);

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
    var image = BuildRawMode1Nrg();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(0xAA ^ (i & 0xFF));

    NrgInPlaceModifier.WriteSector(ms, lba: 25, payload);

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
    using var ms = new MemoryStream(BuildRawMode1Nrg(), writable: true);
    Assert.Throws<ArgumentException>(() =>
      NrgInPlaceModifier.WriteSector(ms, 20, new byte[100]));
  }

  [Test, Category("Boundary")]
  public void WriteSector_NegativeLba_Throws() {
    using var ms = new MemoryStream(BuildRawMode1Nrg(), writable: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      NrgInPlaceModifier.WriteSector(ms, -1, new byte[Iso9660SectorSize]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new NrgFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    Assert.That(new NrgFormatDescriptor(), Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void TryParseSectorEntryName_RoundTrips() {
    var name = NrgInPlaceModifier.FormatSectorEntryName(12345);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName(name, out var lba), Is.True);
    Assert.That(lba, Is.EqualTo(12345));
  }

  [Test, Category("Boundary")]
  public void TryParseSectorEntryName_BogusNames_Rejected() {
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("readme.txt", out _), Is.False);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("sector-abc.bin", out _), Is.False);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("sector-.bin", out _), Is.False);
    Assert.That(NrgInPlaceModifier.TryParseSectorEntryName("", out _), Is.False);
  }

  [Test, Category("Boundary")]
  public void Descriptor_Add_NonSectorEntry_IsRefusedAndPreservesImage() {
    // This used to assert the image was untouched, and it was -- because the
    // entry was dropped on the floor. Leaving the image alone is right; doing it
    // without a word is not, and it made the shared "a fresh volume takes one
    // more file" check pass on a volume that had taken nothing.
    var image = BuildRawMode1Nrg();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new NrgFormatDescriptor()).Add(ms, [
        ArchiveInputInfo.InMemory("readme.txt", "hello"u8.ToArray()),
      ]));

    Assert.That(image, Is.EqualTo(original), "a refused add must leave the image as it was");
  }

  [Test, Category("Boundary")]
  public void DetectGeometry_StripsNer5Footer_FromDataAreaLength() {
    var image = BuildRawMode1Nrg(sectorCount: 32);
    using var ms = new MemoryStream(image, writable: true);
    var geom = NrgInPlaceModifier.DetectGeometry(ms);
    Assert.That(geom.SectorSize, Is.EqualTo(RawSectorSize));
    Assert.That(geom.DataOffset, Is.EqualTo(Mode1DataOffset));
    Assert.That(geom.DataAreaLength, Is.EqualTo(32 * RawSectorSize));
  }
}
