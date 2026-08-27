using Compression.Registry;
using Compression.Registry.Streaming;
using FileSystem.BcacheFs;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsIntegrationTests {

  private const int SectorSize = 512;

  [Test, Category("Contract")]
  public void Descriptor_AdvertisesActualPublicSurface() {
    var d = new BcacheFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(d, Is.InstanceOf<IArchiveWriteConstraints>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<ILayoutOptimizable>());
      Assert.That(d, Is.InstanceOf<IFilesystemExtentMap>());
      Assert.That(d, Is.InstanceOf<IWipeEmpty>());
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsDirectories), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True,
        "bcachefs has a genuine in-place metadata/data allocator and no longer uses the rebuild fallback");
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
    });
  }

  [Test, Category("HappyPath")]
  public void EmptyVolume_ListsAndExtractsNoSyntheticFiles() {
    var d = new BcacheFsFormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, [], new FormatCreateOptions());

    image.Position = 0;
    Assert.That(d.List(image, null).Select(e => e.Name), Does.Not.Contain("FULL.bcachefs"),
      "an empty valid filesystem is not a carver fragment and must not expose FULL.bcachefs");

    var output = Path.Combine(Path.GetTempPath(), "cwb_bch_empty_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(output);
    try {
      image.Position = 0;
      d.Extract(image, output, null, null);
      // the volume describes itself; what it must not do is hand back a carved blob
      Assert.That(Directory.EnumerateFiles(output).Select(Path.GetFileName),
        Does.Not.Contain("FULL.bcachefs"));
    } finally {
      try { Directory.Delete(output, true); } catch { /* best effort */ }
    }
  }

  [Test, Category("Streaming")]
  public void CreateFromStreams_OpensEachInputOnce_AndOpenEntryIsBounded() {
    var d = new BcacheFsFormatDescriptor();
    var alpha = Bytes(100_123, 17);
    var beta = Bytes(8_777, 23);
    var alphaOpens = 0;
    var betaOpens = 0;

    var inputs = new[] {
      new StreamingArchiveInput("a/alpha.bin", alpha.LongLength, false, () => {
        ++alphaOpens;
        return new MemoryStream(alpha, writable: false);
      }),
      new StreamingArchiveInput("beta.bin", beta.LongLength, false, () => {
        ++betaOpens;
        return new MemoryStream(beta, writable: false);
      }),
    };

    using var image = new MemoryStream();
    ((IArchiveCreatable)d).CreateFromStreams(image, inputs, new FormatCreateOptions());

    Assert.Multiple(() => {
      Assert.That(alphaOpens, Is.EqualTo(1));
      Assert.That(betaOpens, Is.EqualTo(1));
    });

    image.Position = 0;
    using var entry = d.OpenEntry(image, "a/alpha.bin", null);
    Assert.That(entry, Is.InstanceOf<BoundedEntryStream>());
    using var copy = new MemoryStream();
    entry.CopyTo(copy);
    Assert.That(copy.ToArray(), Is.EqualTo(alpha));
    Assert.That(entry.ReadByte(), Is.EqualTo(-1));
  }

  [Test, Category("Contract")]
  public void WriteConstraints_RejectReservedAndOversizedPathComponents() {
    var d = new BcacheFsFormatDescriptor();

    Assert.Multiple(() => {
      Assert.That(d.CanAccept(ArchiveInputInfo.InMemory("ok/nested.bin", [1]), out _), Is.True);
      Assert.That(d.CanAccept(ArchiveInputInfo.InMemory("../escape.bin", [1]), out var reserved), Is.False);
      Assert.That(reserved, Does.Contain("reserved"));
      Assert.That(d.CanAccept(ArchiveInputInfo.InMemory(new string('ä', 128) + ".bin", [1]), out var longName), Is.False,
        "bcachefs limits a component by UTF-8 bytes, not UTF-16 characters");
      Assert.That(longName, Does.Contain("255-byte"));
    });
  }

  [Test, Category("HappyPath")]
  public void AddReplaceRemove_AreInPlace_AndDoNotMoveUnchangedFileData() {
    var d = new BcacheFsFormatDescriptor();
    var keep = Bytes(91_337, 1);
    var edit = Bytes(7_003, 2);
    var replacement = Bytes(130_777, 3);
    var added = Bytes(33_333, 4);

    using var image = new MemoryStream();
    d.Create(image, [
      ArchiveInputInfo.InMemory("keep.bin", keep),
      ArchiveInputInfo.InMemory("edit.bin", edit),
    ], new FormatCreateOptions());

    var before = CaptureEntryAllocation(image, "keep.bin");
    var originalImageLength = image.Length;

    image.Position = 0;
    d.Add(image, [ArchiveInputInfo.InMemory("new/path/added.bin", added)]);
    Assert.That(image.Length, Is.EqualTo(originalImageLength), "in-place add must not rebuild/grow the image");
    AssertEntry(image, "new/path/added.bin", added);
    AssertUnchangedAllocation(image, "keep.bin", before);
    AssertAllocationConsistent(image);

    image.Position = 0;
    d.Add(image, [ArchiveInputInfo.InMemory("edit.bin", replacement)]);
    Assert.That(image.Length, Is.EqualTo(originalImageLength), "in-place replacement must keep device size");
    AssertEntry(image, "edit.bin", replacement);
    AssertUnchangedAllocation(image, "keep.bin", before);
    AssertAllocationConsistent(image);

    image.Position = 0;
    d.Remove(image, ["edit.bin", "new/path/added.bin"]);
    Assert.That(image.Length, Is.EqualTo(originalImageLength));
    AssertEntry(image, "keep.bin", keep);
    AssertUnchangedAllocation(image, "keep.bin", before);
    image.Position = 0;
    Assert.That(d.List(image, null).Select(e => e.Name), Does.Not.Contain("edit.bin"));
    image.Position = 0;
    Assert.That(d.List(image, null).Select(e => e.Name), Does.Not.Contain("new/path/added.bin"));
    AssertAllocationConsistent(image);
  }

  [Test, Category("HappyPath")]
  public void Purge_RemovesAllLiveEntries_AndZerosTheirFormerExtents() {
    var d = new BcacheFsFormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, [
      ArchiveInputInfo.InMemory("one.bin", Bytes(80_001, 11)),
      ArchiveInputInfo.InMemory("sub/two.bin", Bytes(17_901, 12)),
    ], new FormatCreateOptions());

    var liveRanges = CaptureAllDataRanges(image);
    image.Position = 0;
    var described = new[] { "metadata.ini", "superblock.bin" };
    var names = d.List(image, null).Where(e => !e.IsDirectory).Select(e => e.Name)
      .Where(n => !described.Contains(n)).ToArray();
    Assert.That(names, Has.Length.EqualTo(2));

    image.Position = 0;
    d.Remove(image, names);

    image.Position = 0;
    using (var reader = new BcacheFsReader(image)) {
      Assert.Multiple(() => {
        Assert.That(reader.Valid, Is.True);
        Assert.That(reader.Entries, Is.Empty);
        Assert.That(reader.Directories, Is.Empty);
      });
    }

    foreach (var (offset, length) in liveRanges) {
      image.Position = offset;
      var bytes = new byte[checked((int)length)];
      image.ReadExactly(bytes);
      Assert.That(bytes.All(b => b == 0), Is.True,
        $"purge left recoverable bytes in old extent 0x{offset:X}+{length}");
    }
    AssertAllocationConsistent(image);
  }

  [Test, Category("HappyPath")]
  public void OptimizeSurface_IsFixedGeometryAndLabelPatchIsInPlace() {
    var d = new BcacheFsFormatDescriptor();
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("data.bin", Bytes(5_123, 7))], new FormatCreateOptions());

    var originalLength = image.Length;
    image.Position = 0;
    var analysis = d.AnalyzeLayout(image);
    Assert.Multiple(() => {
      Assert.That(analysis.CurrentUnitSize, Is.EqualTo(64 * 1024));
      Assert.That(analysis.OptimalUnitSize, Is.EqualTo(64 * 1024));
      Assert.That(analysis.InPlaceChanges, Does.Contain("VolumeLabel"));
    });

    image.Position = 0;
    d.PatchInPlace(image, new LayoutPatch { VolumeLabel = "rw-test" });
    Assert.That(image.Length, Is.EqualTo(originalLength));
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    Assert.That(reader.Valid, Is.True);
    Assert.That(reader.Label, Is.EqualTo("rw-test"));
  }

  [Test, Category("HappyPath")]
  public void WipeUnusedSpace_WipesTailSlackWithoutChangingPayload() {
    var d = new BcacheFsFormatDescriptor();
    var payload = Bytes(513, 31);
    using var image = new MemoryStream();
    d.Create(image, [ArchiveInputInfo.InMemory("odd.bin", payload)], new FormatCreateOptions());

    image.Position = 0;
    using (var reader = new BcacheFsReader(image)) {
      var file = reader.Entries.Single(e => e.Name == "odd.bin");
      var finalExtent = file.Extents[^1];
      var usedInFinalExtent = payload.LongLength - finalExtent.FileOffset;
      var slackStart = finalExtent.FirstSector * SectorSize + usedInFinalExtent;
      var allocated = (long)finalExtent.Sectors * SectorSize;
      Assert.That(allocated - usedInFinalExtent, Is.GreaterThan(0));

      image.Position = slackStart;
      image.WriteByte(0xA5);
    }

    image.Position = 0;
    var wiped = d.WipeUnusedSpace(image, wipeClusterTips: true, wipeDeletedEntries: true);
    Assert.That(wiped, Is.GreaterThan(0));

    AssertEntry(image, "odd.bin", payload);
  }

  private sealed record AllocationSnapshot(
    IReadOnlyList<(long Sector, int Sectors, long FileOffset)> Extents,
    IReadOnlyList<byte[]> PhysicalBytes);

  private static AllocationSnapshot CaptureEntryAllocation(MemoryStream image, string name) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    var entry = reader.Entries.Single(e => e.Name == name);
    var extents = entry.Extents
      .Select(e => (e.FirstSector, e.Sectors, e.FileOffset))
      .ToList();
    var physical = new List<byte[]>(extents.Count);
    foreach (var extent in extents) {
      var bytes = new byte[extent.Sectors * SectorSize];
      image.Position = extent.FirstSector * SectorSize;
      image.ReadExactly(bytes);
      physical.Add(bytes);
    }
    return new AllocationSnapshot(extents, physical);
  }

  private static void AssertUnchangedAllocation(MemoryStream image, string name, AllocationSnapshot expected) {
    var actual = CaptureEntryAllocation(image, name);
    Assert.That(actual.Extents, Is.EqualTo(expected.Extents),
      "an unrelated file was physically relocated by CRUD");
    Assert.That(actual.PhysicalBytes.Count, Is.EqualTo(expected.PhysicalBytes.Count));
    for (var i = 0; i < actual.PhysicalBytes.Count; ++i)
      Assert.That(actual.PhysicalBytes[i], Is.EqualTo(expected.PhysicalBytes[i]),
        $"unchanged extent #{i} was rewritten");
  }

  private static List<(long Offset, long Length)> CaptureAllDataRanges(MemoryStream image) {
    image.Position = 0;
    using var reader = new BcacheFsReader(image);
    return reader.Entries
      .SelectMany(e => e.Extents)
      .Select(e => (e.FirstSector * (long)SectorSize, e.Sectors * (long)SectorSize))
      .ToList();
  }

  private static void AssertEntry(MemoryStream image, string name, byte[] expected) {
    var d = new BcacheFsFormatDescriptor();
    image.Position = 0;
    Assert.That(d.ExtractEntryToMemory(image, name, null), Is.EqualTo(expected));
  }

  private static void AssertAllocationConsistent(MemoryStream image) {
    image.Position = 0;
    var mover = new BcacheFsBlockMover();
    mover.Init(image);
    Assert.That(mover.DescribeAllocationDiscrepancies(image), Is.Empty);
  }

  private static byte[] Bytes(int length, int seed) {
    var bytes = new byte[length];
    new Random(seed).NextBytes(bytes);
    return bytes;
  }
}
