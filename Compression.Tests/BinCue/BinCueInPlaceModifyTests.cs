#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.BinCue;

namespace Compression.Tests.BinCue;

[TestFixture]
public class BinCueInPlaceModifyTests {

  private const int Iso9660SectorSize = 2048;
  private const int RawSectorSize = 2352;
  private const int Mode1DataOffset = 16;

  // ── geometry fixtures ───────────────────────────────────────────────

  /// <summary>
  /// Builds a raw 2 352-byte/sector Mode 1 image with the minimum framing
  /// the modifier and reader need to detect geometry: PVD at LBA 16 with
  /// the <c>CD001</c> signature. Total: <paramref name="sectorCount"/>
  /// raw sectors, each pre-seeded with a deterministic pattern so we can
  /// detect byte-identical preservation.
  /// </summary>
  private static byte[] BuildRawMode1Image(int sectorCount = 32) {
    if (sectorCount <= 16) sectorCount = 20;
    var buf = new byte[sectorCount * RawSectorSize];

    // Pre-seed every byte with a deterministic value so accidental zeroing
    // shows up in byte-identity assertions.
    for (var i = 0; i < buf.Length; i++)
      buf[i] = (byte)((i * 31 + 7) & 0xFF);

    // Lay sync + Mode 1 type byte on every sector so framing detection works.
    Span<byte> sync = stackalloc byte[12] {
      0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00,
    };
    for (var s = 0; s < sectorCount; s++) {
      sync.CopyTo(buf.AsSpan(s * RawSectorSize, 12));
      buf[s * RawSectorSize + 12] = 0;                  // address minute
      buf[s * RawSectorSize + 13] = 0;                  // address second
      buf[s * RawSectorSize + 14] = (byte)s;            // address frame
      buf[s * RawSectorSize + 15] = 0x01;               // mode 1
    }

    // PVD payload at LBA 16: type=1, "CD001", version=1.
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
  /// Builds a cooked flat 2 048-byte/sector image with the PVD at LBA 16.
  /// Used to lock the geometry-detection path that doesn't require sync.
  /// </summary>
  private static byte[] BuildCookedImage(int sectorCount = 32) {
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
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i + 1) & 0xFF);

    BinCueInPlaceModifier.WriteSector(ms, lba: 20, payload);

    // The user-data window of LBA 20 holds our payload.
    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    // The 16-byte sync+address+mode header of LBA 20 is untouched.
    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    // The EDC/ECC tail (288 bytes after the 2 048-byte user data) is untouched.
    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void WriteSector_Raw_OtherSectors_ByteIdentical() {
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0xA5);

    BinCueInPlaceModifier.WriteSector(ms, lba: 20, payload);

    // All sectors except LBA 20 must be byte-identical at their original offsets.
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
    var image = BuildCookedImage();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

    BinCueInPlaceModifier.WriteSector(ms, lba: 22, payload);

    var sectorAt = 22 * Iso9660SectorSize;
    Assert.That(image.AsSpan(sectorAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    // Other sectors untouched.
    Assert.That(image.AsSpan(0, sectorAt).ToArray(),
      Is.EqualTo(original.AsSpan(0, sectorAt).ToArray()));
    Assert.That(image.AsSpan(sectorAt + Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(sectorAt + Iso9660SectorSize).ToArray()));
  }

  // ── WriteSector — append path (Add new sector past EOF) ─────────────

  [Test, Category("RoundTrip")]
  public void WriteSector_PastEof_AppendsWithCorrectFraming() {
    var image = BuildRawMode1Image(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x5A);

    // Append at LBA 25 — there's no sector 20..24 yet.
    BinCueInPlaceModifier.WriteSector(ms, lba: 25, payload);

    var grown = ms.ToArray();
    Assert.That(grown.Length, Is.EqualTo(26 * RawSectorSize));

    // Original LBA 0..19 sectors stay byte-identical.
    Assert.That(grown.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original));

    // LBA 25 user-data window holds our payload.
    var userDataAt = 25 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    // Padding sectors 20..24 carry sync + Mode 1 byte; their user-data window is zero.
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
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.That(BinCueInPlaceModifier.ZeroSector(ms, lba: 19), Is.True);

    var userDataAt = 19 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(new byte[Iso9660SectorSize]));

    // Sync + header preserved.
    Assert.That(image.AsSpan(19 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(19 * RawSectorSize, 16).ToArray()));

    // EDC/ECC tail preserved.
    Assert.That(image.AsSpan(userDataAt + Iso9660SectorSize,
                              RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(userDataAt + Iso9660SectorSize,
                                  RawSectorSize - Mode1DataOffset - Iso9660SectorSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void ZeroSector_PastEof_ReturnsFalse() {
    var image = BuildRawMode1Image();
    using var ms = new MemoryStream(image, writable: true);
    Assert.That(BinCueInPlaceModifier.ZeroSector(ms, lba: 9999), Is.False);
  }

  // ── Descriptor-level Add/Remove ────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RewritesNamedSector() {
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var payload = new byte[Iso9660SectorSize];
    for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

    ((IArchiveModifiable)new BinCueFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(BinCueInPlaceModifier.FormatSectorEntryName(20), payload),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));

    // Every other sector stays byte-identical.
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
    var image = BuildRawMode1Image(sectorCount: 20);
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream();
    ms.Write(image);

    var payload = new byte[Iso9660SectorSize];
    Array.Fill(payload, (byte)0x77);

    ((IArchiveModifiable)new BinCueFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(BinCueInPlaceModifier.FormatSectorEntryName(30), payload),
    ]);

    var grown = ms.ToArray();
    Assert.That(grown.AsSpan(0, original.Length).ToArray(), Is.EqualTo(original));

    var userDataAt = 30 * RawSectorSize + Mode1DataOffset;
    Assert.That(grown.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_ZerosUserData_LeavesEverythingElseIdentical() {
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    ((IArchiveModifiable)new BinCueFormatDescriptor()).Remove(ms, [
      BinCueInPlaceModifier.FormatSectorEntryName(20),
    ]);

    var userDataAt = 20 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(),
      Is.EqualTo(new byte[Iso9660SectorSize]));

    // Header preserved on the zeroed sector.
    Assert.That(image.AsSpan(20 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, 16).ToArray()));

    // Every other sector byte-identical.
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
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    var v1 = new byte[Iso9660SectorSize];
    Array.Fill(v1, (byte)0x11);
    var v2 = new byte[Iso9660SectorSize];
    Array.Fill(v2, (byte)0x22);

    var modifier = (IArchiveModifiable)new BinCueFormatDescriptor();
    modifier.Add(ms, [ArchiveInputInfo.InMemory(BinCueInPlaceModifier.FormatSectorEntryName(21), v1)]);
    modifier.Add(ms, [ArchiveInputInfo.InMemory(BinCueInPlaceModifier.FormatSectorEntryName(21), v2)]);

    var userDataAt = 21 * RawSectorSize + Mode1DataOffset;
    Assert.That(image.AsSpan(userDataAt, Iso9660SectorSize).ToArray(), Is.EqualTo(v2));

    // Framing of LBA 21 still pristine.
    Assert.That(image.AsSpan(21 * RawSectorSize, 16).ToArray(),
      Is.EqualTo(original.AsSpan(21 * RawSectorSize, 16).ToArray()));

    // Neighbours byte-identical.
    Assert.That(image.AsSpan(20 * RawSectorSize, RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(20 * RawSectorSize, RawSectorSize).ToArray()));
    Assert.That(image.AsSpan(22 * RawSectorSize, RawSectorSize).ToArray(),
      Is.EqualTo(original.AsSpan(22 * RawSectorSize, RawSectorSize).ToArray()));
  }

  // ── Boundary / contract ────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void WriteSector_WrongUserDataLength_Throws() {
    using var ms = new MemoryStream(BuildRawMode1Image(), writable: true);
    Assert.Throws<ArgumentException>(() =>
      BinCueInPlaceModifier.WriteSector(ms, 20, new byte[100]));
  }

  [Test, Category("Boundary")]
  public void WriteSector_NegativeLba_Throws() {
    using var ms = new MemoryStream(BuildRawMode1Image(), writable: true);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      BinCueInPlaceModifier.WriteSector(ms, -1, new byte[Iso9660SectorSize]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new BinCueFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    Assert.That(new BinCueFormatDescriptor(), Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void TryParseSectorEntryName_RoundTrips() {
    var name = BinCueInPlaceModifier.FormatSectorEntryName(12345);
    Assert.That(BinCueInPlaceModifier.TryParseSectorEntryName(name, out var lba), Is.True);
    Assert.That(lba, Is.EqualTo(12345));
  }

  [Test, Category("Boundary")]
  public void TryParseSectorEntryName_BogusNames_Rejected() {
    Assert.That(BinCueInPlaceModifier.TryParseSectorEntryName("readme.txt", out _), Is.False);
    Assert.That(BinCueInPlaceModifier.TryParseSectorEntryName("sector-abc.bin", out _), Is.False);
    Assert.That(BinCueInPlaceModifier.TryParseSectorEntryName("sector-.bin", out _), Is.False);
    Assert.That(BinCueInPlaceModifier.TryParseSectorEntryName("", out _), Is.False);
  }

  [Test, Category("Boundary")]
  public void Descriptor_Add_NonSectorEntry_IsRefusedAndPreservesImage() {
    // This used to assert the image was untouched, and it was -- because the
    // entry was dropped on the floor. Leaving the image alone is right; doing it
    // without a word is not, and it made the shared "a fresh volume takes one
    // more file" check pass on a volume that had taken nothing.
    var image = BuildRawMode1Image();
    var original = (byte[])image.Clone();
    using var ms = new MemoryStream(image, writable: true);

    Assert.Throws<NotSupportedException>(() =>
      ((IArchiveModifiable)new BinCueFormatDescriptor()).Add(ms, [
        ArchiveInputInfo.InMemory("readme.txt", "hello"u8.ToArray()),
      ]));

    Assert.That(image, Is.EqualTo(original), "a refused add must leave the image as it was");
  }
}
