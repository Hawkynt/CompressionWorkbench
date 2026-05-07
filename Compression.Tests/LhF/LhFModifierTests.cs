#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.LhF;

namespace Compression.Tests.LhF;

[TestFixture]
public class LhFModifierTests {

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    var ms = BuildSeedLhF();
    var data = MakeTrackData(seed: 11);
    LhFModifier.AddFile(ms, "track_005.raw", data);
    ms.Position = 0;
    var reader = new LhFReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "track_005.raw");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_PreservesExistingEntries() {
    var seed = MakeTrackData(seed: 1);
    var ms = BuildSeedLhF(seed);
    var added = MakeTrackData(seed: 2);
    LhFModifier.AddFile(ms, "track_009.raw", added);

    ms.Position = 0;
    var reader = new LhFReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
    var byName = reader.Entries.ToDictionary(e => e.Name, e => reader.Extract(e));
    Assert.That(byName["track_000.raw"], Is.EqualTo(seed));
    Assert.That(byName["track_009.raw"], Is.EqualTo(added));
  }

  [Test, Category("RoundTrip")]
  public void AddFile_LargeCompressible_StoresSmaller() {
    var ms = BuildSeedLhF();
    // Highly compressible (all zeros) — should be encoded smaller than raw.
    var data = new byte[LhFWriter.TrackSize];
    LhFModifier.AddFile(ms, "track_007.raw", data);

    ms.Position = 0;
    var reader = new LhFReader(ms);
    var entry = reader.Entries.Single(e => e.Name == "track_007.raw");
    Assert.That(reader.Extract(entry), Is.EqualTo(data));
    Assert.That(entry.CompressedSize, Is.LessThan(entry.Size),
      "expected zeros to compress smaller than the raw track");
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    var ms = BuildSeedLhF();
    LhFModifier.AddFile(ms, "track_002.raw", MakeTrackData(seed: 3));
    LhFModifier.AddFile(ms, "track_003.raw", MakeTrackData(seed: 4));
    Assert.That(LhFModifier.RemoveFile(ms, "track_002.raw"), Is.True);

    ms.Position = 0;
    var reader = new LhFReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "track_002.raw"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "track_003.raw"), Is.True);
    Assert.That(reader.Entries.Any(e => e.Name == "track_000.raw"), Is.True);
    Assert.That(reader.Entries, Has.Count.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    var ms = BuildSeedLhF();
    Assert.That(LhFModifier.RemoveFile(ms, "track_999.raw"), Is.False);

    // The seed track must still be intact.
    ms.Position = 0;
    var reader = new LhFReader(ms);
    Assert.That(reader.Entries, Has.Count.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void Replace_ViaRemoveAdd_SwapsContent() {
    var ms = BuildSeedLhF();
    var v1 = MakeTrackData(seed: 100);
    var v2 = MakeTrackData(seed: 200);
    LhFModifier.AddFile(ms, "track_004.raw", v1);
    LhFModifier.RemoveFile(ms, "track_004.raw");
    LhFModifier.AddFile(ms, "track_004.raw", v2);

    ms.Position = 0;
    var reader = new LhFReader(ms);
    var matching = reader.Entries.Where(e => e.Name == "track_004.raw").ToList();
    Assert.That(matching, Has.Count.EqualTo(1));
    Assert.That(reader.Extract(matching[0]), Is.EqualTo(v2));
  }

  [Test, Category("RoundTrip")]
  public void TrackCountField_IsBumpedOnAdd() {
    var ms = BuildSeedLhF();
    LhFModifier.AddFile(ms, "track_011.raw", MakeTrackData(seed: 5));
    LhFModifier.AddFile(ms, "track_012.raw", MakeTrackData(seed: 6));

    var raw = ms.ToArray();
    var count = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2));
    Assert.That(count, Is.EqualTo(3), "header trackCount should reflect appended tracks");
  }

  [Test, Category("RoundTrip")]
  public void TrackCountField_IsDecrementedOnRemove() {
    var ms = BuildSeedLhF();
    LhFModifier.AddFile(ms, "track_011.raw", MakeTrackData(seed: 5));
    LhFModifier.AddFile(ms, "track_012.raw", MakeTrackData(seed: 6));
    LhFModifier.RemoveFile(ms, "track_011.raw");

    var raw = ms.ToArray();
    var count = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(4, 2));
    Assert.That(count, Is.EqualTo(2));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    var ms = BuildSeedLhF();
    var tmp = Path.GetTempFileName();
    try {
      var data = MakeTrackData(seed: 42);
      File.WriteAllBytes(tmp, data);
      ((IArchiveModifiable)new LhFFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "track_006.raw", false)]);

      ms.Position = 0;
      var reader = new LhFReader(ms);
      var entry = reader.Entries.Single(e => e.Name == "track_006.raw");
      Assert.That(reader.Extract(entry), Is.EqualTo(data));
    } finally { File.Delete(tmp); }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_RemoveViaInterface() {
    var ms = BuildSeedLhF();
    LhFModifier.AddFile(ms, "track_002.raw", MakeTrackData(seed: 7));

    ((IArchiveModifiable)new LhFFormatDescriptor()).Remove(ms, ["track_002.raw"]);

    ms.Position = 0;
    var reader = new LhFReader(ms);
    Assert.That(reader.Entries.Any(e => e.Name == "track_002.raw"), Is.False);
    Assert.That(reader.Entries.Any(e => e.Name == "track_000.raw"), Is.True);
  }

  // ── Helpers ───────────────────────────────────────────────────────────

  /// <summary>
  /// Builds a freshly-seeded LhF MemoryStream with a single track (track_000.raw).
  /// </summary>
  private static MemoryStream BuildSeedLhF() => BuildSeedLhF(MakeTrackData(seed: 0));

  private static MemoryStream BuildSeedLhF(byte[] track0) {
    var ms = new MemoryStream();
    var w = new LhFWriter();
    w.AddTrack(0, track0);
    w.WriteTo(ms);
    var copy = new MemoryStream();
    ms.Position = 0;
    ms.CopyTo(copy);
    copy.Position = 0;
    return copy;
  }

  /// <summary>
  /// Random-but-deterministic track payload at TrackSize so the writer's
  /// stored-passthrough path kicks in (random data won't compress).
  /// </summary>
  private static byte[] MakeTrackData(int seed) {
    var data = new byte[LhFWriter.TrackSize];
    new Random(seed).NextBytes(data);
    return data;
  }
}
