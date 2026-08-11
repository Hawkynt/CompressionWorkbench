#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.AppleSparse;

namespace Compression.Tests.AppleSparse;

[TestFixture]
public class AppleSparseInPlaceModifyTests {

  // Tiny band geometry for tests: 4 sectors/band = 2 048 B per band.
  // 256 logical bands fit in the 4 KB header's band-table area.
  private const int TestSectorsPerBand = 4;
  private const int TestBandSize = TestSectorsPerBand * AppleSparseReader.SectorBytes;
  private const int HeaderSize = AppleSparseReader.HeaderSize;
  private const int HeaderPreambleSize = AppleSparseReader.HeaderPreambleSize;

  /// <summary>
  /// Builds an in-memory sparseimage with three pre-allocated bands at
  /// logical indices 0, 1, 2, each pre-seeded with a recognisable pattern
  /// so the test can detect any unintended writes.
  /// </summary>
  private static MemoryStream BuildSeededImage(out byte[] originalBytes) {
    var ms = new MemoryStream();
    var header = AppleSparseInPlaceModifier.BuildEmptyContainer(TestSectorsPerBand, 256);
    ms.Write(header);

    // Allocate three bands at logicals 0, 1, 2 with distinct payload patterns.
    var band0 = new byte[TestBandSize];
    Array.Fill(band0, (byte)0xA0);
    var band1 = new byte[TestBandSize];
    Array.Fill(band1, (byte)0xB1);
    var band2 = new byte[TestBandSize];
    Array.Fill(band2, (byte)0xC2);

    AppleSparseInPlaceModifier.WriteBand(ms, 0, band0);
    AppleSparseInPlaceModifier.WriteBand(ms, 1, band1);
    AppleSparseInPlaceModifier.WriteBand(ms, 2, band2);

    originalBytes = ms.ToArray();
    ms.Position = 0;
    return ms;
  }

  // ── Reader / container plumbing ─────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Reader_ParsesAllAllocatedBands() {
    using var ms = BuildSeededImage(out _);
    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    Assert.That(container!.Bands, Has.Count.EqualTo(3));
    Assert.That(container.Bands.Select(b => b.LogicalBandIndex), Is.EqualTo(new[] { 0, 1, 2 }));
    Assert.That(container.SectorsPerBand, Is.EqualTo(TestSectorsPerBand));
    Assert.That(container.BandSize, Is.EqualTo(TestBandSize));
    Assert.That(container.AllocatedCount, Is.EqualTo(3));
  }

  // ── WriteBand — rewrite existing band in place ──────────────────────

  [Test, Category("RoundTrip")]
  public void WriteBand_RewriteExisting_TouchesOnlyThatBand() {
    using var ms = BuildSeededImage(out var original);
    var raw = ms.GetBuffer();

    var newPayload = new byte[TestBandSize];
    for (var i = 0; i < newPayload.Length; i++) newPayload[i] = (byte)((i + 1) & 0xFF);

    AppleSparseInPlaceModifier.WriteBand(ms, 1, newPayload);

    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    var band1 = container!.Bands.Single(b => b.LogicalBandIndex == 1);
    var actual = AppleSparseReader.ReadBand(ms, band1);
    Assert.That(actual, Is.EqualTo(newPayload));

    // Header preamble (32 B) untouched.
    Assert.That(raw.AsSpan(0, HeaderPreambleSize).ToArray(),
      Is.EqualTo(original.AsSpan(0, HeaderPreambleSize).ToArray()));

    // Band-table slots for logicals 0 and 2 untouched.
    var slot0Offset = HeaderPreambleSize + 0 * 4;
    var slot2Offset = HeaderPreambleSize + 2 * 4;
    Assert.That(raw.AsSpan(slot0Offset, 4).ToArray(), Is.EqualTo(original.AsSpan(slot0Offset, 4).ToArray()));
    Assert.That(raw.AsSpan(slot2Offset, 4).ToArray(), Is.EqualTo(original.AsSpan(slot2Offset, 4).ToArray()));

    // Band 0 + band 2 payload regions untouched.
    var band0Offset = HeaderSize + 0 * TestBandSize;
    var band2Offset = HeaderSize + 2 * TestBandSize;
    Assert.That(raw.AsSpan((int)band0Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band0Offset, TestBandSize).ToArray()));
    Assert.That(raw.AsSpan((int)band2Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan((int)band2Offset, TestBandSize).ToArray()));
  }

  // ── WriteBand — allocate fresh logical band (Add) ──────────────────

  [Test, Category("RoundTrip")]
  public void WriteBand_AllocateNewLogicalBand_BumpsHeaderAndAppendsAtEof() {
    using var ms = BuildSeededImage(out var original);

    var newPayload = new byte[TestBandSize];
    Array.Fill(newPayload, (byte)0xD3);

    var imageLengthBeforeAdd = ms.Length;
    Assert.That(imageLengthBeforeAdd, Is.EqualTo(HeaderSize + 3 * TestBandSize));

    AppleSparseInPlaceModifier.WriteBand(ms, 5, newPayload);

    Assert.That(ms.Length, Is.EqualTo(imageLengthBeforeAdd + TestBandSize));

    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    Assert.That(container!.AllocatedCount, Is.EqualTo(4));
    Assert.That(container.NextPhysicalSlot, Is.EqualTo(5));
    var band5 = container.Bands.Single(b => b.LogicalBandIndex == 5);
    Assert.That(band5.PhysicalSlotIndex, Is.EqualTo(4));
    Assert.That(AppleSparseReader.ReadBand(ms, band5), Is.EqualTo(newPayload));

    // The original three bands stay byte-identical at their original offsets.
    var raw = ms.GetBuffer();
    Assert.That(raw.AsSpan(HeaderSize, 3 * TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(HeaderSize, 3 * TestBandSize).ToArray()));

    // Band-table slots 0..2 untouched.
    for (var logical = 0; logical < 3; logical++) {
      var off = HeaderPreambleSize + logical * 4;
      Assert.That(raw.AsSpan(off, 4).ToArray(),
        Is.EqualTo(original.AsSpan(off, 4).ToArray()),
        $"slot {logical} unexpectedly mutated.");
    }
  }

  // ── RemoveBand ──────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void RemoveBand_ZerosPayload_PreservesAllOtherBytes() {
    using var ms = BuildSeededImage(out var original);
    var raw = ms.GetBuffer();

    Assert.That(AppleSparseInPlaceModifier.RemoveBand(ms, 1), Is.True);

    // Logical band 1 was at physical slot 2 → byte offset HeaderSize + 1*TestBandSize.
    var bandOffset = HeaderSize + 1 * TestBandSize;
    Assert.That(raw.AsSpan(bandOffset, TestBandSize).ToArray(),
      Is.EqualTo(new byte[TestBandSize]));

    // Band-table slot for logical 1 is cleared.
    var slot1Offset = HeaderPreambleSize + 1 * 4;
    Assert.That(raw.AsSpan(slot1Offset, 4).ToArray(), Is.EqualTo(new byte[4]));

    // Band-table slots for logicals 0 and 2 untouched.
    var slot0Offset = HeaderPreambleSize + 0 * 4;
    var slot2Offset = HeaderPreambleSize + 2 * 4;
    Assert.That(raw.AsSpan(slot0Offset, 4).ToArray(), Is.EqualTo(original.AsSpan(slot0Offset, 4).ToArray()));
    Assert.That(raw.AsSpan(slot2Offset, 4).ToArray(), Is.EqualTo(original.AsSpan(slot2Offset, 4).ToArray()));

    // Other bands' payload regions untouched.
    var band0Offset = HeaderSize + 0 * TestBandSize;
    var band2Offset = HeaderSize + 2 * TestBandSize;
    Assert.That(raw.AsSpan(band0Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band0Offset, TestBandSize).ToArray()));
    Assert.That(raw.AsSpan(band2Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band2Offset, TestBandSize).ToArray()));

    // allocated_count decremented; next_physical_slot stays put (hole retained).
    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    Assert.That(container!.AllocatedCount, Is.EqualTo(2));
    Assert.That(container.Bands, Has.Count.EqualTo(2));
    Assert.That(container.Bands.Any(b => b.LogicalBandIndex == 1), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void RemoveBand_Unallocated_ReturnsFalse() {
    using var ms = BuildSeededImage(out _);
    Assert.That(AppleSparseInPlaceModifier.RemoveBand(ms, 99), Is.False);
  }

  // ── Descriptor-level Add / Remove / Replace ────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_RewritesNamedBand() {
    using var ms = BuildSeededImage(out var original);
    var raw = ms.GetBuffer();

    var payload = new byte[TestBandSize];
    Array.Fill(payload, (byte)0xE4);

    ((IArchiveModifiable)new AppleSparseFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(AppleSparseInPlaceModifier.FormatBandEntryName(2), payload),
    ]);

    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    var band2 = container!.Bands.Single(b => b.LogicalBandIndex == 2);
    Assert.That(AppleSparseReader.ReadBand(ms, band2), Is.EqualTo(payload));

    // Bands 0 + 1 untouched.
    var band0Offset = HeaderSize + 0 * TestBandSize;
    var band1Offset = HeaderSize + 1 * TestBandSize;
    Assert.That(raw.AsSpan(band0Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band0Offset, TestBandSize).ToArray()));
    Assert.That(raw.AsSpan(band1Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band1Offset, TestBandSize).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Add_AllocatesNewLogicalBandPreservingExisting() {
    using var ms = BuildSeededImage(out var original);

    var payload = new byte[TestBandSize];
    Array.Fill(payload, (byte)0xF5);

    ((IArchiveModifiable)new AppleSparseFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory(AppleSparseInPlaceModifier.FormatBandEntryName(10), payload),
    ]);

    var raw = ms.GetBuffer();

    // First 3 bands still byte-identical at their original positions.
    Assert.That(raw.AsSpan(HeaderSize, 3 * TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(HeaderSize, 3 * TestBandSize).ToArray()));

    // New band readable via the descriptor pipeline.
    var container = AppleSparseReader.TryRead(ms);
    Assert.That(container, Is.Not.Null);
    var band10 = container!.Bands.Single(b => b.LogicalBandIndex == 10);
    Assert.That(AppleSparseReader.ReadBand(ms, band10), Is.EqualTo(payload));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Remove_ZerosBand_LeavesEverythingElseIdentical() {
    using var ms = BuildSeededImage(out var original);

    ((IArchiveModifiable)new AppleSparseFormatDescriptor()).Remove(ms, [
      AppleSparseInPlaceModifier.FormatBandEntryName(0),
    ]);

    var raw = ms.GetBuffer();

    // Band 0 zeroed.
    Assert.That(raw.AsSpan(HeaderSize, TestBandSize).ToArray(),
      Is.EqualTo(new byte[TestBandSize]));

    // Bands 1 + 2 byte-identical.
    var band1Offset = HeaderSize + 1 * TestBandSize;
    var band2Offset = HeaderSize + 2 * TestBandSize;
    Assert.That(raw.AsSpan(band1Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band1Offset, TestBandSize).ToArray()));
    Assert.That(raw.AsSpan(band2Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band2Offset, TestBandSize).ToArray()));

    // Band-table slot for 0 cleared; slots for 1 + 2 untouched.
    var slot0 = HeaderPreambleSize + 0 * 4;
    var slot1 = HeaderPreambleSize + 1 * 4;
    var slot2 = HeaderPreambleSize + 2 * 4;
    Assert.That(raw.AsSpan(slot0, 4).ToArray(), Is.EqualTo(new byte[4]));
    Assert.That(raw.AsSpan(slot1, 4).ToArray(), Is.EqualTo(original.AsSpan(slot1, 4).ToArray()));
    Assert.That(raw.AsSpan(slot2, 4).ToArray(), Is.EqualTo(original.AsSpan(slot2, 4).ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Replace_RewritesPayloadPreservingNeighbours() {
    using var ms = BuildSeededImage(out var original);

    var v1 = new byte[TestBandSize];
    Array.Fill(v1, (byte)0x11);
    var v2 = new byte[TestBandSize];
    Array.Fill(v2, (byte)0x22);

    var modifier = (IArchiveModifiable)new AppleSparseFormatDescriptor();
    modifier.Add(ms, [ArchiveInputInfo.InMemory(AppleSparseInPlaceModifier.FormatBandEntryName(1), v1)]);
    modifier.Add(ms, [ArchiveInputInfo.InMemory(AppleSparseInPlaceModifier.FormatBandEntryName(1), v2)]);

    var raw = ms.GetBuffer();
    var band1Offset = HeaderSize + 1 * TestBandSize;
    Assert.That(raw.AsSpan(band1Offset, TestBandSize).ToArray(), Is.EqualTo(v2));

    // Bands 0 + 2 unchanged.
    var band0Offset = HeaderSize + 0 * TestBandSize;
    var band2Offset = HeaderSize + 2 * TestBandSize;
    Assert.That(raw.AsSpan(band0Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band0Offset, TestBandSize).ToArray()));
    Assert.That(raw.AsSpan(band2Offset, TestBandSize).ToArray(),
      Is.EqualTo(original.AsSpan(band2Offset, TestBandSize).ToArray()));
  }

  // ── List + Extract round-trip ──────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Descriptor_List_SurfacesEachAllocatedBand() {
    using var ms = BuildSeededImage(out _);
    var entries = new AppleSparseFormatDescriptor().List(ms, password: null);
    Assert.That(entries.Select(e => e.Name).OrderBy(n => n).ToArray(),
      Is.EqualTo(new[] {
        AppleSparseInPlaceModifier.FormatBandEntryName(0),
        AppleSparseInPlaceModifier.FormatBandEntryName(1),
        AppleSparseInPlaceModifier.FormatBandEntryName(2),
      }));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Extract_WritesAllBandsToDisk() {
    using var ms = BuildSeededImage(out _);
    var tmp = Path.Combine(Path.GetTempPath(), "AppleSparseInPlace_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    try {
      new AppleSparseFormatDescriptor().Extract(ms, tmp, password: null, files: null);
      var files = Directory.GetFiles(tmp).Select(Path.GetFileName).OrderBy(n => n).ToArray();
      Assert.That(files, Is.EqualTo(new[] {
        AppleSparseInPlaceModifier.FormatBandEntryName(0),
        AppleSparseInPlaceModifier.FormatBandEntryName(1),
        AppleSparseInPlaceModifier.FormatBandEntryName(2),
      }));

      var band1Bytes = File.ReadAllBytes(Path.Combine(tmp, AppleSparseInPlaceModifier.FormatBandEntryName(1)));
      Assert.That(band1Bytes, Has.Length.EqualTo(TestBandSize));
      Assert.That(band1Bytes[0], Is.EqualTo(0xB1));
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  // ── Boundary / contract ────────────────────────────────────────────

  [Test, Category("Boundary")]
  public void WriteBand_WrongPayloadSize_Throws() {
    using var ms = BuildSeededImage(out _);
    Assert.Throws<ArgumentException>(() =>
      AppleSparseInPlaceModifier.WriteBand(ms, 0, new byte[1]));
  }

  [Test, Category("Boundary")]
  public void WriteBand_NegativeLogical_Throws() {
    using var ms = BuildSeededImage(out _);
    Assert.Throws<ArgumentOutOfRangeException>(() =>
      AppleSparseInPlaceModifier.WriteBand(ms, -1, new byte[TestBandSize]));
  }

  [Test, Category("Boundary")]
  public void WriteBand_NonSparseimage_Throws() {
    using var ms = new MemoryStream(new byte[HeaderSize]);
    Assert.Throws<ArgumentException>(() =>
      AppleSparseInPlaceModifier.WriteBand(ms, 0, new byte[TestBandSize]));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify() {
    var d = new AppleSparseFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_ImplementsIArchiveModifiable() {
    Assert.That(new AppleSparseFormatDescriptor(), Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("HappyPath")]
  public void TryParseBandEntryName_RoundTrips() {
    var name = AppleSparseInPlaceModifier.FormatBandEntryName(7);
    Assert.That(AppleSparseInPlaceModifier.TryParseBandEntryName(name, out var idx), Is.True);
    Assert.That(idx, Is.EqualTo(7));
  }

  [Test, Category("Boundary")]
  public void TryParseBandEntryName_BogusNames_Rejected() {
    Assert.That(AppleSparseInPlaceModifier.TryParseBandEntryName("readme.txt", out _), Is.False);
    Assert.That(AppleSparseInPlaceModifier.TryParseBandEntryName("band-xyz.bin", out _), Is.False);
    Assert.That(AppleSparseInPlaceModifier.TryParseBandEntryName("", out _), Is.False);
  }

  [Test, Category("Boundary")]
  public void Descriptor_Add_NonBandEntry_NoOpAndPreservesImage() {
    using var ms = BuildSeededImage(out var original);
    ((IArchiveModifiable)new AppleSparseFormatDescriptor()).Add(ms, [
      ArchiveInputInfo.InMemory("readme.txt", "hello"u8.ToArray()),
    ]);
    Assert.That(ms.ToArray(), Is.EqualTo(original));
  }

  [Test, Category("RoundTrip")]
  public void Create_RoundTripsThroughReader() {
    var descriptor = new AppleSparseFormatDescriptor();
    using var output = new MemoryStream();
    // Default Create geometry is 1 MiB band size; supply that exact size.
    var defaultBandSize = 2048 * AppleSparseReader.SectorBytes;
    var payload = new byte[defaultBandSize];
    Array.Fill(payload, (byte)0x42);

    descriptor.Create(output, [
      ArchiveInputInfo.InMemory(AppleSparseInPlaceModifier.FormatBandEntryName(3), payload),
    ], new FormatCreateOptions());

    output.Position = 0;
    var entries = descriptor.List(output, password: null);
    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo(AppleSparseInPlaceModifier.FormatBandEntryName(3)));
  }
}
