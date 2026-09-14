using FileSystem.Gpfs;

namespace Compression.Tests.Gpfs;

[TestFixture]
public class GpfsTsdbfsPointerParserTests {
  [Test]
  public void ParsesPublishedDirectPointerAndNullSlots() {
    // IBM support document 7229010: policy-file inode with one direct data
    // pointer followed by null slots. Ellipses are presentation shorthand, not
    // synthesized structure, so only explicitly printed slots are modeled.
    const string text = """
      Inode 41 [41] snap 0 (index 41 in block 0):
        Inode address: 13:133480776 size 4096 nAddrs 330
        indirectionLevel=DIRECT status=RESERVED
        checksum=0x06C87E49 is Valid
        fileSize=224 nFullBlocks=1
        Disk pointers [330]:
          0:  4:18497536    1: (null)         ...       329: (null)
        trailer: is NULL
      """;

    var pointers = GpfsTsdbfsPointerParser.Parse(text);

    Assert.Multiple(() => {
      Assert.That(pointers.DeclaredSlotCount, Is.EqualTo(330));
      Assert.That(pointers.Pointers.Select(static x => x.SlotIndex), Is.EqualTo(new[] { 0, 1, 329 }));
      Assert.That(pointers.Pointers[0].Replicas, Is.EqualTo(new[] { new GpfsDiskAddress(4, 18497536) }));
      Assert.That(pointers.Pointers[1].Replicas, Is.Empty);
      Assert.That(pointers.Pointers[2].Replicas, Is.Empty);
    });
  }

  [Test]
  public void ParsesMultipleReplicasForOneLogicalPointer() {
    const string text = """
      Disk pointers [32]:
        0:  31:2176293761:  30:2176329602: (null)   1: (null)
        31: (null)
      trailer: is NULL
      """;

    var pointers = GpfsTsdbfsPointerParser.Parse(text);

    Assert.Multiple(() => {
      Assert.That(pointers.DeclaredSlotCount, Is.EqualTo(32));
      Assert.That(pointers.Pointers, Has.Count.EqualTo(3));
      Assert.That(pointers.Pointers[0].SlotIndex, Is.Zero);
      Assert.That(pointers.Pointers[0].Replicas, Is.EqualTo(new[] {
        new GpfsDiskAddress(31, 2176293761),
        new GpfsDiskAddress(30, 2176329602),
      }));
    });
  }

  [Test]
  public void RejectsMissingPointerSection()
    => Assert.Throws<InvalidDataException>(() => GpfsTsdbfsPointerParser.Parse("Inode 1"));

  [Test]
  public void RejectsRepeatedSlot() {
    const string text = """
      Disk pointers [4]:
        0:  1:100
        0:  1:200
      trailer: is NULL
      """;

    Assert.Throws<InvalidDataException>(() => GpfsTsdbfsPointerParser.Parse(text));
  }

  [Test]
  public void RejectsSlotOutsideDeclaredRange() {
    const string text = """
      Disk pointers [2]:
        2:  1:100
      trailer: is NULL
      """;

    Assert.Throws<InvalidDataException>(() => GpfsTsdbfsPointerParser.Parse(text));
  }
}
