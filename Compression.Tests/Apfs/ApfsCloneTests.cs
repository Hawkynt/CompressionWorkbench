using System.IO.Compression;
using System.Security.Cryptography;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// Clones (<c>cp --reflink</c>) and a snapshot written by the linux-apfs-rw kernel driver on a
/// container mkapfs made, checked by apfsck and compared with what apfs-fuse read
/// (<c>ReferenceVectors/apfs-clones.*</c>, made by <c>generate-clones.sh</c>).
/// </summary>
/// <remarks>
/// A clone shares its source's extents until either is written; afterwards each must read
/// its own bytes. The volume holds two clones taken before a snapshot, then a write to the
/// source and to one clone and an append to the other, so the live tree has diverged copies
/// over shared blocks and the snapshot still sees them identical.
/// </remarks>
[TestFixture]
public sealed class ApfsCloneTests {

  private static ApfsReader Open() {
    using var resource = typeof(ApfsCloneTests).Assembly.GetManifestResourceStream("ApfsVectors.apfs-clones.img.gz")!;
    using var gzip = new GZipStream(resource, CompressionMode.Decompress);
    var image = new MemoryStream();
    gzip.CopyTo(image);
    image.Position = 0;
    return new ApfsReader(image);
  }

  [Test, Category("HappyPath")]
  public void GivenClonesAndASnapshot_WhenRead_ThenEveryFileMatchesApfsFuse() {
    using var reader = Open();
    var entries = reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, StringComparer.Ordinal);
    var expected = DissectApfsSamples.Manifest("apfs-clones.manifest").Where(m => m.Kind is 'f' or 'l').ToList();
    Assert.That(entries.Keys, Is.EquivalentTo(expected.Select(m => m.Path)));
    Assert.Multiple(() => {
      foreach (var (kind, value, path) in expected) {
        var entry = entries[path];
        if (kind == 'f')
          Assert.That(Convert.ToHexStringLower(SHA256.HashData(reader.Extract(entry))), Is.EqualTo(value), path);
        else
          Assert.That(entry.LinkTarget, Is.EqualTo(value), path);
      }
    });
  }

  [Test, Category("HappyPath")]
  public void GivenAClone_WhenItsExtentsAreCompared_ThenItSharesBlocksWithItsSource() {
    // Proves the fixture holds a real clone, not a copy: the diverged clone still points at
    // blocks its source points at.
    using var reader = Open();
    var source = reader.Entries.Single(e => e.Name == "big.bin");
    var clone = reader.Entries.Single(e => e.Name == "dir/big-clone.bin");
    static HashSet<ulong> Blocks(ApfsEntry e, uint blockSize) =>
      e.Extents.Where(x => x.PhysBlock != 0)
        .SelectMany(x => Enumerable.Range(0, (int)((x.Length + blockSize - 1) / blockSize)).Select(i => x.PhysBlock + (ulong)i))
        .ToHashSet();
    var shared = Blocks(source, reader.BlockSize).Intersect(Blocks(clone, reader.BlockSize)).Count();
    Assert.That(shared, Is.GreaterThan(0));
    Assert.That(reader.Extract(source), Is.Not.EqualTo(reader.Extract(clone)), "the two have diverged since");
  }

  [Test, Category("HappyPath")]
  public void GivenTheSnapshotTakenBeforeTheWrites_WhenRead_ThenSourceAndCloneAreStillIdentical() {
    using var reader = Open();
    Assert.That(reader.Snapshots.Select(s => s.Name), Is.EqualTo(new[] { "before" }));
    var source = reader.Entries.Single(e => e.Name == ".snapshots/before/big.bin");
    var clone = reader.Entries.Single(e => e.Name == ".snapshots/before/dir/big-clone.bin");
    Assert.That(reader.Extract(clone), Is.EqualTo(reader.Extract(source)));
  }
}
