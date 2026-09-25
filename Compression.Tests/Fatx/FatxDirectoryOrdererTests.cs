using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Fatx;

namespace Compression.Tests.Fatx;

[TestFixture]
public class FatxDirectoryOrdererTests {
  [Test, Category("RoundTrip")]
  public void SortDirectoryEntries_SortsEveryDirectoryWithoutMovingAllocations() {
    var descriptor = new FatxFormatDescriptor();
    using var image = new MemoryStream();

    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("zeta.txt", "zeta"u8.ToArray()),
      ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray()),
      ArchiveInputInfo.InMemory("middle.txt", "middle"u8.ToArray()),
      ArchiveInputInfo.InMemory("z-dir/z-last.bin", "z-child"u8.ToArray()),
      ArchiveInputInfo.InMemory("z-dir/a-first.bin", "a-child"u8.ToArray()),
      ArchiveInputInfo.InMemory("a-dir/middle.bin", "nested"u8.ToArray()),
    ], new FormatCreateOptions());

    var before = Snapshot(image);
    image.Position = 0;

    ((IFilesystemDirectoryOrderer)descriptor).SortDirectoryEntries(image);

    var after = Snapshot(image);

    Assert.Multiple(() => {
      Assert.That(
        after.Entries.Where(entry => !entry.Name.Contains('/')).Select(entry => entry.Name),
        Is.EqualTo(new[] { "a-dir", "alpha.txt", "middle.txt", "z-dir", "zeta.txt" }),
        "root directory records must be sorted case-insensitively by name");

      Assert.That(
        after.Entries
          .Where(entry => entry.Name.StartsWith("z-dir/", StringComparison.Ordinal))
          .Select(entry => entry.Name),
        Is.EqualTo(new[] { "z-dir/a-first.bin", "z-dir/z-last.bin" }),
        "nested directory records must be sorted too");

      Assert.That(
        after.Entries.ToDictionary(entry => entry.Name, entry => entry.FirstCluster),
        Is.EqualTo(before.Entries.ToDictionary(entry => entry.Name, entry => entry.FirstCluster)),
        "sorting directory records must not move allocation chains");

      Assert.That(after.Payloads.Keys, Is.EquivalentTo(before.Payloads.Keys));
    });

    foreach (var (name, payload) in before.Payloads)
      Assert.That(after.Payloads[name], Is.EqualTo(payload), $"{name} payload changed while sorting directory records");
  }

  [Test, Category("Regression")]
  public void SortDirectoryEntries_PreservesTombstonesAndDoesNotResurrectDeletedFiles() {
    var descriptor = new FatxFormatDescriptor();
    using var image = new MemoryStream();

    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("zeta.txt", "zeta"u8.ToArray()),
      ArchiveInputInfo.InMemory("deleted.txt", "deleted"u8.ToArray()),
      ArchiveInputInfo.InMemory("alpha.txt", "alpha"u8.ToArray()),
    ], new FormatCreateOptions());

    image.Position = 0;
    descriptor.Remove(image, ["deleted.txt"]);

    var before = Snapshot(image);
    image.Position = 0;
    ((IFilesystemDirectoryOrderer)descriptor).SortDirectoryEntries(image);
    var after = Snapshot(image);

    Assert.Multiple(() => {
      Assert.That(after.Entries.Select(entry => entry.Name),
        Is.EqualTo(new[] { "alpha.txt", "zeta.txt" }));
      Assert.That(after.Entries.Select(entry => entry.Name), Does.Not.Contain("deleted.txt"));
      Assert.That(after.Payloads["alpha.txt"], Is.EqualTo(before.Payloads["alpha.txt"]));
      Assert.That(after.Payloads["zeta.txt"], Is.EqualTo(before.Payloads["zeta.txt"]));
    });

    // A preserved reusable slot must still exist after sorting. If sorting had
    // consumed/resurrected the tombstone, this add would need a different slot
    // or could fail on a full directory.
    image.Position = 0;
    descriptor.Add(image, [ArchiveInputInfo.InMemory("bravo.txt", "bravo"u8.ToArray())]);
    image.Position = 0;
    Assert.That(descriptor.List(image, null).Select(entry => entry.Name),
      Does.Contain("bravo.txt"));
  }

  [Test, Category("Regression")]
  public void SortDirectoryEntries_MalformedChildLeavesWholeImageUntouched() {
    var descriptor = new FatxFormatDescriptor();
    using var image = new MemoryStream();

    descriptor.Create(image, [
      ArchiveInputInfo.InMemory("zeta.txt", "zeta"u8.ToArray()),
      ArchiveInputInfo.InMemory("a-dir/child.bin", "child"u8.ToArray()),
    ], new FormatCreateOptions());

    image.Position = 0;
    uint childDirectoryCluster;
    using (var reader = new FatxReader(image))
      childDirectoryCluster = reader.Entries.Single(entry => entry.Name == "a-dir").FirstCluster;

    var bytes = image.ToArray();
    var clusterSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x08)) * 512;
    var childOffset = ComputeDataRegionStart(bytes)
      + (long)(childDirectoryCluster - 1) * clusterSize;
    bytes[checked((int)childOffset)] = 43; // impossible FATX name length (> 42)

    image.SetLength(0);
    image.Write(bytes);
    var before = image.ToArray();

    image.Position = 0;
    Assert.That(
      () => ((IFilesystemDirectoryOrderer)descriptor).SortDirectoryEntries(image),
      Throws.TypeOf<InvalidDataException>());

    Assert.That(image.ToArray(), Is.EqualTo(before),
      "planning must reject malformed descendants before rewriting any parent directory");
  }

  private static long ComputeDataRegionStart(byte[] image) {
    var sectorsPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x08));
    var clusterSize = checked((int)sectorsPerCluster * 512);
    var postSuperblockBytes = (long)image.Length - 0x1000;
    var clusterCount = Math.Max(1L, postSuperblockBytes / clusterSize);
    var entryBytes = clusterCount < 0xFFF4 ? 2 : 4;
    var fatRaw = (clusterCount + 2) * entryBytes;
    var fatRounded = (fatRaw + 0xFFF) & ~0xFFFL;
    return 0x1000 + fatRounded;
  }

  private static SnapshotResult Snapshot(MemoryStream image) {
    image.Position = 0;
    using var reader = new FatxReader(image);
    var entries = reader.Entries
      .Select(entry => new EntrySnapshot(entry.Name, entry.FirstCluster, entry.IsDirectory))
      .ToArray();

    var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries.Where(entry => !entry.IsDirectory))
      payloads[entry.Name] = reader.Extract(entry);

    return new SnapshotResult(entries, payloads);
  }

  private sealed record EntrySnapshot(string Name, uint FirstCluster, bool IsDirectory);
  private sealed record SnapshotResult(
    IReadOnlyList<EntrySnapshot> Entries,
    IReadOnlyDictionary<string, byte[]> Payloads);
}
