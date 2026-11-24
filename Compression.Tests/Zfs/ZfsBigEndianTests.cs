using FileSystem.Zfs;
using static Compression.Tests.Zfs.ZfsReferencePools;

namespace Compression.Tests.Zfs;

/// <summary>
/// Pools a big-endian host wrote: OpenZFS on Ubuntu 24.04 s390x under QEMU made them, filled them
/// and listed them (<c>ReferenceVectors/README.md</c>, <c>generate-be.sh</c>). Every block they hold
/// was written in big-endian order, so every structure the reader meets is byte-swapped first.
/// </summary>
[TestFixture]
public class ZfsBigEndianTests {

  [TestCase("bebe", 5000UL)]
  [TestCase("beold", 28UL)]
  [Category("HappyPath"), Category("Oracle")]
  public void GivenAPoolABigEndianHostWrote_WhenRead_ThenEveryFileIsWhatThatHostListed(string pool, ulong version) {
    using var reader = new ZfsReader(Open(pool));
    Assert.Multiple(() => {
      Assert.That(reader.PoolName, Is.EqualTo(pool));
      Assert.That(reader.PoolVersion, Is.EqualTo(version));
      Assert.That(reader.Warnings, Is.Empty);
    });
    AssertMatchesManifest(reader, pool);
    Assert.That(reader.Statistics.ByteSwappedBlocks, Is.GreaterThan(0), "the blocks were swapped from big-endian");
  }

  [Test, Category("Spec"), Category("Oracle")]
  public void GivenTheFeaturePool_WhenRead_ThenItsChildDatasetAndSnapshotAreListed() {
    using var reader = new ZfsReader(Open("bebe"));
    var names = reader.Datasets.Where(static d => d.Refusal == null).Select(static d => d.Name).ToHashSet();
    Assert.That(names, Is.SupersetOf(new[] { "bebe", "bebe/sub", "bebe@first" }));
  }

  [TestCase("bebe")]
  [TestCase("beold")]
  [Category("Exception")]
  public void GivenAPoolABigEndianHostWrote_WhenEdited_ThenTheEditIsRefusedAndThePoolUntouched(string pool) {
    using var image = Copy(pool);
    var before = image.ToArray();
    var descriptor = new ZfsFormatDescriptor();
    Assert.Throws<NotSupportedException>(() => descriptor.Add(image, [Compression.Registry.ArchiveInputInfo.InMemory("added.txt", [1, 2, 3])]));
    Assert.That(image.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("Exception")]
  public void GivenADamagedBigEndianBlock_WhenItsFileIsRead_ThenTheChecksumRefusesIt() {
    using var reader = new ZfsReader(Open("bebe"));
    var big = reader.Entries.Single(static e => e.Name == "docs/random.bin");
    Assume.That(big.Dnode!.Levels, Is.EqualTo(1), "one data block, stored once");
    var bp = big.Dnode!.BlockPointers[0];
    Assume.That(bp.IsLittleEndian, Is.False);
    using var damaged = Copy("bebe");
    var at = ZfsConstants.DataStart + bp.Dvas[0].Offset + 10;
    damaged.Position = at;
    var b = damaged.ReadByte();
    damaged.Position = at;
    damaged.WriteByte((byte)(b ^ 0x55));
    using var broken = new ZfsReader(damaged);
    var entry = broken.Entries.Single(static e => e.Name == "docs/random.bin");
    Assert.Throws<InvalidDataException>(() => broken.Extract(entry));
  }
}
