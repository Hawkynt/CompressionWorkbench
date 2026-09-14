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
  public void ParsesAdjacentPointerSlotsWithoutMergingAddresses() {
    // Public tsdbfs transcripts are often rendered with collapsed whitespace.
    // This fixture preserves the intended column boundaries explicitly: these
    // are slot 0 and slot 1, not two replicas belonging to slot 0.
    const string text = """
      Disk pointers [32]:
        0:  31:217629376   1:  30:217632960   2: (null)
        31: (null)
      trailer: is NULL
      """;

    var pointers = GpfsTsdbfsPointerParser.Parse(text);

    Assert.Multiple(() => {
      Assert.That(pointers.DeclaredSlotCount, Is.EqualTo(32));
      Assert.That(pointers.Pointers, Has.Count.EqualTo(4));
      Assert.That(pointers.Pointers[0], Is.EqualTo(new GpfsDiskPointerOracle(0, new[] { new GpfsDiskAddress(31, 217629376) })));
      Assert.That(pointers.Pointers[1], Is.EqualTo(new GpfsDiskPointerOracle(1, new[] { new GpfsDiskAddress(30, 217632960) })));
      Assert.That(pointers.Pointers[2].Replicas, Is.Empty);
      Assert.That(pointers.Pointers[3].SlotIndex, Is.EqualTo(31));
    });
  }

  [Test]
  public void ParsesMultipleAddressesWhenOneExplicitSlotContainsThem() {
    const string text = """
      Disk pointers [4]:
        0:  7:100 9:200   1: (null)
      trailer: is NULL
      """;

    var pointers = GpfsTsdbfsPointerParser.Parse(text);

    Assert.That(pointers.Pointers[0].Replicas, Is.EqualTo(new[] {
      new GpfsDiskAddress(7, 100),
      new GpfsDiskAddress(9, 200),
    }));
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
