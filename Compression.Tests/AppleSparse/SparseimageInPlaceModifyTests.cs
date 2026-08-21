#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.AppleSparse;

namespace Compression.Tests.AppleSparse;

[TestFixture]
public class SparseimageInPlaceModifyTests {

  private const int SectorsPerBand = 8;                                  // 4 KB bands keep test images small
  private const int BandSize = SectorsPerBand * 512;                     // 4096 B
  private const int Header = 4096;

  /// <summary>
  /// Builds a sparseimage with three logical bands. Band 0 = pattern A,
  /// band 1 = all zeros (sparse), band 2 = pattern B. Returns the raw
  /// bytes so the caller can clone them for byte-identity assertions.
  /// </summary>
  private static byte[] BuildThreeBandImage(out byte[] bandA, out byte[] bandB) {
    bandA = new byte[BandSize];
    for (var i = 0; i < bandA.Length; i++) bandA[i] = (byte)((i * 31 + 7) & 0xFF);
    bandB = new byte[BandSize];
    for (var i = 0; i < bandB.Length; i++) bandB[i] = (byte)((i * 53 + 11) & 0xFF);

    var data = new byte[BandSize * 3];
    Array.Copy(bandA, 0, data, 0, BandSize);
    Array.Copy(bandB, 0, data, BandSize * 2, BandSize);

    var w = new SparseimageWriter();
    w.SetSectorsPerBand(SectorsPerBand);
    w.SetDiskData(data);
    return w.Build();
  }

  /// <summary>Builds a four-logical-band image with bands 0, 1 and 3 allocated
  /// (each different pattern); band 2 sparse. Used to lock the "untouched
  /// allocated bands stay byte-identical" invariant for RemoveBand.</summary>
  private static byte[] BuildFourBandImage(out byte[] b0, out byte[] b1, out byte[] b3) {
    b0 = Pattern(0x10, 0x07);
    b1 = Pattern(0x20, 0x13);
    b3 = Pattern(0x40, 0x29);
    var data = new byte[BandSize * 4];
    Array.Copy(b0, 0, data, 0, BandSize);
    Array.Copy(b1, 0, data, BandSize, BandSize);
    // band 2 sparse: leave zero
    Array.Copy(b3, 0, data, BandSize * 3, BandSize);

    var w = new SparseimageWriter();
    w.SetSectorsPerBand(SectorsPerBand);
    w.SetDiskData(data);
    return w.Build();
  }

  private static byte[] Pattern(byte seed, byte step) {
    var b = new byte[BandSize];
    for (var i = 0; i < b.Length; i++) b[i] = (byte)(seed + i * step);
    return b;
  }

  // ── ReadGeometry / header introspection ────────────────────────────

  [Test, Category("HappyPath")]
  public void ReadGeometry_PicksUpHeaderFields() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream(img, writable: true);
    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    Assert.Multiple(() => {
      Assert.That(geom.SectorsPerBand, Is.EqualTo(SectorsPerBand));
      Assert.That(geom.BandSize, Is.EqualTo(BandSize));
      Assert.That(geom.NumBands, Is.EqualTo(3));
      Assert.That(geom.BatOffset, Is.EqualTo(Header));
      Assert.That(geom.FirstBandOffset, Is.EqualTo(((Header + 3L * 4 + 511) & ~511L)));
    });
  }

  [Test, Category("ErrorHandling")]
  public void ReadGeometry_BadMagic_Throws() {
    using var ms = new MemoryStream(new byte[Header]);
    Assert.Throws<InvalidDataException>(() => SparseimageInPlaceModifier.ReadGeometry(ms));
  }

  [Test, Category("ErrorHandling")]
  public void ReadGeometry_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => SparseimageInPlaceModifier.ReadGeometry(ms));
  }

  // ── WriteBand: rewrite an existing allocated band ──────────────────

  [Test, Category("RoundTrip")]
  public void WriteBand_RewriteExisting_HeaderAndOtherBands_ByteIdentical() {
    var img = BuildThreeBandImage(out var bandA, out var bandB);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream(img, writable: true);

    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    var slot0 = SparseimageInPlaceModifier.ReadBatEntry(ms, 0, geom);
    var slot2 = SparseimageInPlaceModifier.ReadBatEntry(ms, 2, geom);
    Assert.That(slot0, Is.Not.Zero);
    Assert.That(slot2, Is.Not.Zero);

    var newBand0 = Pattern(0xAA, 0x03);
    SparseimageInPlaceModifier.WriteBand(ms, 0, newBand0, geom);

    // Stream length unchanged.
    Assert.That(ms.Length, Is.EqualTo(original.Length));

    // Header preamble unchanged.
    Assert.That(img.AsSpan(0, Header).ToArray(), Is.EqualTo(original.AsSpan(0, Header).ToArray()));

    // BAT entries 1 and 2 unchanged.
    Assert.That(img.AsSpan((int)geom.BatOffset + 4, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset + 4, 4).ToArray()));
    Assert.That(img.AsSpan((int)geom.BatOffset + 8, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset + 8, 4).ToArray()));

    // BAT entry 0 unchanged (same physical slot).
    Assert.That(img.AsSpan((int)geom.BatOffset, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset, 4).ToArray()));

    // Band 0's payload is the new pattern at its known offset.
    var band0Phys = geom.FirstBandOffset + (long)(slot0 - 1) * geom.BandSize;
    Assert.That(img.AsSpan((int)band0Phys, BandSize).ToArray(), Is.EqualTo(newBand0));

    // Band 2's payload byte-identical at its original offset.
    var band2Phys = geom.FirstBandOffset + (long)(slot2 - 1) * geom.BandSize;
    Assert.That(img.AsSpan((int)band2Phys, BandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band2Phys, BandSize).ToArray()));
    Assert.That(img.AsSpan((int)band2Phys, BandSize).ToArray(), Is.EqualTo(bandB));

    // Round-trip via reader: band 0 = new pattern, band 1 = zeros, band 2 = old pattern.
    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(newBand0));
    Assert.That(virt.AsSpan(BandSize, BandSize).ToArray(), Is.EqualTo(new byte[BandSize]));
    Assert.That(virt.AsSpan(BandSize * 2, BandSize).ToArray(), Is.EqualTo(bandB));
    Assert.That(bandA, Is.Not.EqualTo(newBand0)); // sanity
  }

  // ── WriteBand: allocate a previously-unallocated band ──────────────

  [Test, Category("RoundTrip")]
  public void WriteBand_AllocateNewBand_HeaderAndOtherBands_ByteIdentical() {
    var img = BuildThreeBandImage(out var bandA, out var bandB);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    var slot0 = SparseimageInPlaceModifier.ReadBatEntry(ms, 0, geom);
    var slot1Before = SparseimageInPlaceModifier.ReadBatEntry(ms, 1, geom);
    var slot2 = SparseimageInPlaceModifier.ReadBatEntry(ms, 2, geom);
    Assert.That(slot1Before, Is.Zero, "band 1 should start unallocated");

    var newBand1 = Pattern(0x55, 0x09);
    SparseimageInPlaceModifier.WriteBand(ms, 1, newBand1, geom);

    var result = ms.ToArray();

    // Stream length grew by exactly one band size.
    Assert.That(result.Length, Is.EqualTo(original.Length + BandSize));

    // Header preamble unchanged.
    Assert.That(result.AsSpan(0, Header).ToArray(),
      Is.EqualTo(original.AsSpan(0, Header).ToArray()));

    // BAT entries 0 and 2 unchanged.
    Assert.That(result.AsSpan((int)geom.BatOffset, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset, 4).ToArray()));
    Assert.That(result.AsSpan((int)geom.BatOffset + 8, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset + 8, 4).ToArray()));

    // BAT entry 1 now points at the new slot (= existing slots + 1).
    var slot1After = BinaryPrimitives.ReadUInt32BigEndian(result.AsSpan((int)geom.BatOffset + 4, 4));
    Assert.That(slot1After, Is.GreaterThan(Math.Max(slot0, slot2)));

    // Existing bands' payloads byte-identical at their original offsets.
    var band0Phys = geom.FirstBandOffset + (long)(slot0 - 1) * geom.BandSize;
    var band2Phys = geom.FirstBandOffset + (long)(slot2 - 1) * geom.BandSize;
    Assert.That(result.AsSpan((int)band0Phys, BandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band0Phys, BandSize).ToArray()));
    Assert.That(result.AsSpan((int)band2Phys, BandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band2Phys, BandSize).ToArray()));

    // The new physical slot lives at its known offset and carries the new payload.
    var band1Phys = geom.FirstBandOffset + (long)(slot1After - 1) * geom.BandSize;
    Assert.That(result.AsSpan((int)band1Phys, BandSize).ToArray(), Is.EqualTo(newBand1));

    // Round-trip via reader: band 0 = pattern A, band 1 = new pattern, band 2 = pattern B.
    using var ms2 = new MemoryStream(result);
    using var reader = new SparseimageReader(ms2, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(bandA));
    Assert.That(virt.AsSpan(BandSize, BandSize).ToArray(), Is.EqualTo(newBand1));
    Assert.That(virt.AsSpan(BandSize * 2, BandSize).ToArray(), Is.EqualTo(bandB));
  }

  [Test, Category("RoundTrip")]
  public void WriteBand_PastEofAppend_HeaderPreserved_NoStreamShift() {
    // After alloc-new-band call, even with a MemoryStream that's already sized
    // exactly to the file length, the header bytes preceding the BAT must not
    // be touched. Locks the "past-EOF append" invariant from the brief.
    var img = BuildThreeBandImage(out _, out _);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    SparseimageInPlaceModifier.WriteBand(ms, 1, Pattern(0x88, 0x11));

    var result = ms.ToArray();
    // Bytes 0..Header byte-identical with original (header preamble).
    Assert.That(result.AsSpan(0, Header).ToArray(),
      Is.EqualTo(original.AsSpan(0, Header).ToArray()));
    // The BAT region directly after the header had only entry 1 modified.
    Assert.That(result.AsSpan(Header, 4).ToArray(),
      Is.EqualTo(original.AsSpan(Header, 4).ToArray()));      // entry 0
    Assert.That(result.AsSpan(Header + 8, 4).ToArray(),
      Is.EqualTo(original.AsSpan(Header + 8, 4).ToArray()));  // entry 2
  }

  // ── RemoveBand: zero data + clear BAT entry ────────────────────────

  [Test, Category("RoundTrip")]
  public void RemoveBand_AllocatedBand_ZerosDataAndClearsBatEntry() {
    var img = BuildFourBandImage(out var b0, out var b1, out var b3);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream(img, writable: true);

    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    var slot1 = SparseimageInPlaceModifier.ReadBatEntry(ms, 1, geom);
    var slot0 = SparseimageInPlaceModifier.ReadBatEntry(ms, 0, geom);
    var slot3 = SparseimageInPlaceModifier.ReadBatEntry(ms, 3, geom);
    Assert.That(slot1, Is.Not.Zero);

    var removed = SparseimageInPlaceModifier.RemoveBand(ms, 1, geom);
    Assert.That(removed, Is.True);

    // Stream length unchanged.
    Assert.That(ms.Length, Is.EqualTo(original.Length));

    // Header preamble unchanged.
    Assert.That(img.AsSpan(0, Header).ToArray(),
      Is.EqualTo(original.AsSpan(0, Header).ToArray()));

    // BAT entries 0, 2 and 3 unchanged.
    Assert.That(img.AsSpan((int)geom.BatOffset, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset, 4).ToArray()));
    Assert.That(img.AsSpan((int)geom.BatOffset + 8, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset + 8, 4).ToArray()));
    Assert.That(img.AsSpan((int)geom.BatOffset + 12, 4).ToArray(),
      Is.EqualTo(original.AsSpan((int)geom.BatOffset + 12, 4).ToArray()));

    // BAT entry 1 is now 0 (unallocated).
    Assert.That(SparseimageInPlaceModifier.ReadBatEntry(ms, 1, geom), Is.Zero);

    // The removed band's physical slot is zero-filled.
    var band1Phys = geom.FirstBandOffset + (long)(slot1 - 1) * geom.BandSize;
    var zeros = new byte[BandSize];
    Assert.That(img.AsSpan((int)band1Phys, BandSize).ToArray(), Is.EqualTo(zeros));

    // Other allocated bands' payloads byte-identical at their original offsets.
    var band0Phys = geom.FirstBandOffset + (long)(slot0 - 1) * geom.BandSize;
    var band3Phys = geom.FirstBandOffset + (long)(slot3 - 1) * geom.BandSize;
    Assert.That(img.AsSpan((int)band0Phys, BandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band0Phys, BandSize).ToArray()));
    Assert.That(img.AsSpan((int)band3Phys, BandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band3Phys, BandSize).ToArray()));

    // Round-trip via reader: band 1 now reads as zeros.
    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(b0));
    Assert.That(virt.AsSpan(BandSize, BandSize).ToArray(), Is.EqualTo(zeros));
    Assert.That(virt.AsSpan(BandSize * 3, BandSize).ToArray(), Is.EqualTo(b3));
  }

  [Test, Category("HappyPath")]
  public void RemoveBand_UnallocatedBand_NoOp() {
    var img = BuildThreeBandImage(out _, out _);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream(img, writable: true);

    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    Assert.That(SparseimageInPlaceModifier.ReadBatEntry(ms, 1, geom), Is.Zero);

    var removed = SparseimageInPlaceModifier.RemoveBand(ms, 1, geom);
    Assert.That(removed, Is.False);

    // Entire image byte-identical.
    Assert.That(img, Is.EqualTo(original));
  }

  [Test, Category("HappyPath")]
  public void RemoveBand_IndexPastNumBands_ReturnsFalse_NoMutation() {
    var img = BuildThreeBandImage(out _, out _);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream(img, writable: true);
    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    var removed = SparseimageInPlaceModifier.RemoveBand(ms, 99, geom);
    Assert.That(removed, Is.False);
    Assert.That(img, Is.EqualTo(original));
  }

  // ── WriteBand boundary / error cases ──────────────────────────────

  [Test, Category("ErrorHandling")]
  public void WriteBand_WrongPayloadSize_Throws() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream(img, writable: true);
    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    Assert.Throws<ArgumentException>(() =>
      SparseimageInPlaceModifier.WriteBand(ms, 0, new byte[BandSize - 1], geom));
    Assert.Throws<ArgumentException>(() =>
      SparseimageInPlaceModifier.WriteBand(ms, 0, new byte[BandSize + 1], geom));
  }

  [Test, Category("ErrorHandling")]
  public void WriteBand_NegativeIndex_Throws() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream(img, writable: true);
    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      SparseimageInPlaceModifier.WriteBand(ms, -1, new byte[BandSize], geom));
  }

  [Test, Category("ErrorHandling")]
  public void WriteBand_IndexPastNumBands_Throws() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream(img, writable: true);
    var geom = SparseimageInPlaceModifier.ReadGeometry(ms);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      SparseimageInPlaceModifier.WriteBand(ms, 99, new byte[BandSize], geom));
  }

  // ── Synthetic entry name parsing ──────────────────────────────────

  [Test, Category("HappyPath")]
  public void TryParseBandEntryName_RoundTripsLogicalIndex() {
    Assert.Multiple(() => {
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("band-000042.bin", out var i), Is.True);
      Assert.That(i, Is.EqualTo(42));
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("band-7.bin", out i), Is.True);
      Assert.That(i, Is.EqualTo(7));
      Assert.That(SparseimageInPlaceModifier.FormatBandEntryName(5), Is.EqualTo("band-000005.bin"));
    });
  }

  [Test, Category("ErrorHandling")]
  public void TryParseBandEntryName_RejectsBadNames() {
    Assert.Multiple(() => {
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("sector-1.bin", out _), Is.False);
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("band-abc.bin", out _), Is.False);
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("band-1.txt", out _), Is.False);
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("", out _), Is.False);
      Assert.That(SparseimageInPlaceModifier.TryParseBandEntryName("band--5.bin", out _), Is.False);
    });
  }

  // ── Descriptor surface ────────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var desc = new SparseimageFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(desc is IArchiveModifiable, Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RewritesBand_OthersByteIdentical() {
    var img = BuildThreeBandImage(out var bandA, out var bandB);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    var desc = new SparseimageFormatDescriptor();
    var newBand0 = Pattern(0xC3, 0x05);
    desc.Add(ms, [
      ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(0), newBand0),
    ]);

    var result = ms.ToArray();

    // Stream length unchanged (band 0 was already allocated).
    Assert.That(result.Length, Is.EqualTo(original.Length));

    // Round-trip via reader.
    using var ms2 = new MemoryStream(result);
    using var reader = new SparseimageReader(ms2, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(newBand0));
    Assert.That(virt.AsSpan(BandSize * 2, BandSize).ToArray(), Is.EqualTo(bandB));
    Assert.That(bandA, Is.Not.EqualTo(newBand0));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_AllocatesNewBand_GrowsByOneBand() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;
    var originalLen = ms.Length;

    var desc = new SparseimageFormatDescriptor();
    var newBand1 = Pattern(0x77, 0x0D);
    desc.Add(ms, [
      ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(1), newBand1),
    ]);

    Assert.That(ms.Length, Is.EqualTo(originalLen + BandSize));

    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(BandSize, BandSize).ToArray(), Is.EqualTo(newBand1));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_ZeroesBand() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    var desc = new SparseimageFormatDescriptor();
    desc.Remove(ms, [SparseimageInPlaceModifier.FormatBandEntryName(0)]);

    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    var zeros = new byte[BandSize];
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(zeros));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_NonBandEntries_IsRefused() {
    // The image being unchanged was the whole assertion, and it held because the
    // entry was quietly discarded. Leaving the image alone is correct; doing it
    // in silence let a caller believe a band had been written.
    var img = BuildThreeBandImage(out var bandA, out _);
    var original = (byte[])img.Clone();
    using var ms = new MemoryStream(img, writable: true);

    var desc = new SparseimageFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => desc.Add(ms, [
      ArchiveInputInfo.InMemory("HELLO.TXT", "world"u8.ToArray()),
    ]), "a name that names no band should be refused");

    Assert.Throws<ArgumentException>(() => desc.Add(ms, [
      ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(0), new byte[BandSize - 1]),
    ]), "a band that is not a whole band should be refused");

    Assert.That(img, Is.EqualTo(original), "a refused add must leave the image as it was");

    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(bandA));
  }

  // ── Mutate-then-extract end-to-end ────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_RoundTrip_AddRemoveAddReplace() {
    var img = BuildThreeBandImage(out _, out _);
    using var ms = new MemoryStream();
    ms.Write(img);
    ms.Position = 0;

    var desc = new SparseimageFormatDescriptor();
    var newBand0 = Pattern(0x10, 0x1F);
    var newBand1 = Pattern(0x20, 0x0F);
    var newBand2 = Pattern(0x30, 0x07);

    // 1. Allocate band 1.
    desc.Add(ms, [ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(1), newBand1)]);
    // 2. Replace band 0.
    desc.Add(ms, [ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(0), newBand0)]);
    // 3. Remove band 2.
    desc.Remove(ms, [SparseimageInPlaceModifier.FormatBandEntryName(2)]);
    // 4. Allocate band 2 again with new pattern.
    desc.Add(ms, [ArchiveInputInfo.InMemory(SparseimageInPlaceModifier.FormatBandEntryName(2), newBand2)]);

    ms.Position = 0;
    using var reader = new SparseimageReader(ms, leaveOpen: true);
    var virt = reader.ExtractDisk();
    Assert.That(virt.AsSpan(0, BandSize).ToArray(), Is.EqualTo(newBand0));
    Assert.That(virt.AsSpan(BandSize, BandSize).ToArray(), Is.EqualTo(newBand1));
    Assert.That(virt.AsSpan(BandSize * 2, BandSize).ToArray(), Is.EqualTo(newBand2));
  }
}
