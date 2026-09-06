#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileSystem.Udf;

namespace Compression.Tests.Udf;

/// <summary>
/// Reads the bytes our writer produces and checks the few structural rules that
/// other implementations depend on. These need no tool installed, so they hold
/// the line on a host where the native checks stand down.
/// </summary>
[TestFixture]
public sealed class UdfOnDiskStructureTests {

  private const int BlockSize = 2048;
  private const int PartitionStart = 257;

  private static byte[] Build(params (string Name, byte[] Data)[] files) {
    var writer = new UdfWriter();
    foreach (var (name, data) in files)
      writer.AddFile(name, data);
    using var image = new MemoryStream();
    writer.WriteTo(image);
    return image.ToArray();
  }

  private static ReadOnlySpan<byte> Block(byte[] image, int block)
    => image.AsSpan(block * BlockSize, BlockSize);

  /// <summary>The root directory's File Entry, at partition block 1.</summary>
  private static ReadOnlySpan<byte> RootFileEntry(byte[] image) => Block(image, PartitionStart + 1);

  /// <summary>
  /// A name whose characters all fit in a byte is recorded with compression 8
  /// and one byte per character. Recording UTF-8 under that identifier makes
  /// every accented name unreadable to anything but the writer that produced
  /// it — OSTA UDF §2.1.1.
  /// </summary>
  [Test]
  public void ALatin1NameIsRecordedOneBytePerCharacter() {
    var image = Build(("café.bin", "x"u8.ToArray()));
    var fids = DirectoryBytes(image, RootFileEntry(image));

    // The parent record comes first and carries no identifier.
    var identifier = FirstNamedIdentifier(fids);
    Assert.Multiple(() => {
      Assert.That(identifier[0], Is.EqualTo(8), "characters below U+0100 take the single-byte compression");
      Assert.That(identifier.Length, Is.EqualTo(1 + "café.bin".Length));
      Assert.That(identifier[^5], Is.EqualTo(0xE9), "é is one byte, its own code point, not two UTF-8 bytes");
    });
  }

  /// <summary>A name that needs more than a byte per character takes the wide compression.</summary>
  [Test]
  public void AWideNameIsRecordedTwoBytesPerCharacterBigEndian() {
    var image = Build(("日本.bin", "x"u8.ToArray()));
    var identifier = FirstNamedIdentifier(DirectoryBytes(image, RootFileEntry(image)));

    Assert.Multiple(() => {
      Assert.That(identifier[0], Is.EqualTo(16));
      Assert.That(identifier.Length, Is.EqualTo(1 + 2 * "日本.bin".Length));
      Assert.That(identifier[1], Is.EqualTo(0x65), "big-endian: the high byte of U+65E5 comes first");
      Assert.That(identifier[2], Is.EqualTo(0xE5));
    });
  }

  /// <summary>
  /// A directory is a dense run of File Identifier Descriptors. ECMA-167
  /// §4/14.4 lets one span a logical block boundary and the kernel's udf driver
  /// relies on that; padding the boundary instead leaves a zero tag where the
  /// next record should be, and the driver stops there.
  /// </summary>
  [Test]
  public void DirectoryRecordsRunBackToBackAcrossBlockBoundaries() {
    // Enough entries that the directory needs several blocks, so at least one
    // record has to straddle a boundary.
    var files = Enumerable.Range(0, 200)
      .Select(i => ($"entry_{i:D4}.txt", "x"u8.ToArray()))
      .ToArray();
    var image = Build(files);
    var fids = DirectoryBytes(image, RootFileEntry(image));

    var straddling = 0;
    var position = 0;
    var records = 0;
    while (position + 38 <= fids.Length) {
      var tag = BinaryPrimitives.ReadUInt16LittleEndian(fids.AsSpan(position));
      Assert.That(tag, Is.EqualTo(257),
        $"byte {position} of the directory is not the start of a File Identifier Descriptor");
      var implementationUse = BinaryPrimitives.ReadUInt16LittleEndian(fids.AsSpan(position + 36));
      var length = (38 + implementationUse + fids[position + 19] + 3) & ~3;

      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(fids.AsSpan(position + 16)), Is.EqualTo(1),
        "OSTA UDF §2.3.4.1: the file version number of every record is one");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(fids.AsSpan(position + 12)),
        Is.EqualTo((uint)(RootDirectoryFirstBlock(image) + position / BlockSize)),
        "a record's tag names the block it starts in");

      if (position / BlockSize != (position + length - 1) / BlockSize)
        ++straddling;
      position += length;
      ++records;
    }

    Assert.Multiple(() => {
      Assert.That(records, Is.EqualTo(files.Length + 1), "one record per entry, plus the parent");
      Assert.That(position, Is.EqualTo(fids.Length), "the records fill the directory exactly");
      Assert.That(straddling, Is.GreaterThan(0),
        "with this many entries at least one record has to cross a block boundary");
    });
  }

  /// <summary>
  /// A zero-length file is given no extent. An allocation descriptor naming a
  /// block it does not own is a chain longer than the size says, which is what
  /// every filesystem's own checker calls corruption.
  /// </summary>
  [Test]
  public void AnEmptyFileIsGivenNoExtent() {
    var image = Build(("empty.bin", []), ("full.bin", new byte[3000]));

    var empty = FileEntryFor(image, "empty.bin");
    var full = FileEntryFor(image, "full.bin");

    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(empty + 56)), Is.Zero,
        "information length");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(empty + 172)), Is.Zero,
        "an empty file records no allocation descriptors at all");
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(empty + 64)), Is.Zero,
        "and no blocks recorded");

      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(full + 172)), Is.EqualTo(8u),
        "a file with content records one descriptor");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(full + 176)) & 0x3FFFFFFF,
        Is.EqualTo(3000u), "whose length is the file's, not the block it was rounded up to");
    });
  }

  /// <summary>
  /// ECMA-167 §3/8.4 wants an anchor at logical block 256 and at the volume's
  /// last block, and each says which block it is. A volume with only the first
  /// has one copy of the only descriptor found by address rather than by being
  /// pointed at.
  /// </summary>
  [Test]
  public void BothAnchorsAreRecordedAndNameTheirOwnBlock() {
    var image = Build(("a.bin", "a"u8.ToArray()));
    var lastBlock = image.Length / BlockSize - 1;

    Assert.Multiple(() => {
      foreach (var block in new[] { 256, lastBlock }) {
        var tag = Block(image, block);
        Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(tag), Is.EqualTo(2),
          $"no anchor at block {block}");
        Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(tag[12..]), Is.EqualTo((uint)block),
          $"the anchor at block {block} names a different block");
        byte sum = 0;
        for (var i = 0; i < 16; ++i)
          if (i != 4) sum = (byte)(sum + tag[i]);
        Assert.That(sum, Is.EqualTo(tag[4]), $"the anchor at block {block} has a broken tag checksum");
      }
    });
  }

  /// <summary>
  /// Both volume descriptor sequences the anchor names are recorded, each
  /// carrying the descriptors a UDF volume is required to have.
  /// </summary>
  [Test]
  public void BothVolumeDescriptorSequencesAreRecorded() {
    var image = Build(("a.bin", "a"u8.ToArray()));
    var anchor = Block(image, 256);
    var main = (int)BinaryPrimitives.ReadUInt32LittleEndian(anchor[20..]);
    var reserve = (int)BinaryPrimitives.ReadUInt32LittleEndian(anchor[28..]);

    Assert.That(reserve, Is.Not.Zero, "the anchor records no reserve sequence");
    Assert.Multiple(() => {
      foreach (var start in new[] { main, reserve }) {
        var tags = new List<ushort>();
        for (var i = 0; i < 16; ++i) {
          var tag = BinaryPrimitives.ReadUInt16LittleEndian(Block(image, start + i));
          if (tag == 0) break;
          tags.Add(tag);
          if (tag == 8) break;
        }

        // Primary volume, logical volume, partition, implementation use,
        // unallocated space, terminator.
        Assert.That(tags, Is.EquivalentTo(new ushort[] { 1, 6, 5, 4, 7, 8 }),
          $"the sequence at block {start} is missing descriptors");
      }
    });
  }

  // ── walking helpers ───────────────────────────────────────────────────────

  private static int RootDirectoryFirstBlock(byte[] image)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(RootFileEntry(image)[180..]);

  /// <summary>Concatenates the blocks a directory File Entry's descriptors name.</summary>
  private static byte[] DirectoryBytes(byte[] image, ReadOnlySpan<byte> fileEntry) {
    var informationLength = (int)BinaryPrimitives.ReadUInt64LittleEndian(fileEntry[56..]);
    var lengthOfDescriptors = (int)BinaryPrimitives.ReadUInt32LittleEndian(fileEntry[172..]);

    var bytes = new List<byte>();
    for (var at = 176; at + 8 <= 176 + lengthOfDescriptors; at += 8) {
      var length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(fileEntry[at..]) & 0x3FFFFFFF);
      var block = (int)BinaryPrimitives.ReadUInt32LittleEndian(fileEntry[(at + 4)..]);
      for (var i = 0; i < length; ++i)
        bytes.Add(image[(PartitionStart + block) * BlockSize + i]);
    }

    return [.. bytes.Take(informationLength)];
  }

  /// <summary>The identifier bytes of the first record that names something.</summary>
  private static byte[] FirstNamedIdentifier(byte[] directory) {
    var position = 0;
    while (position + 38 <= directory.Length) {
      var implementationUse = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 36));
      var identifierLength = directory[position + 19];
      if (identifierLength > 0)
        return directory[(position + 38 + implementationUse)..(position + 38 + implementationUse + identifierLength)];
      position += (38 + implementationUse + identifierLength + 3) & ~3;
    }

    throw new InvalidOperationException("the directory holds no named record");
  }

  /// <summary>Byte offset of the File Entry the root directory names <paramref name="name" />.</summary>
  private static int FileEntryFor(byte[] image, string name) {
    var directory = DirectoryBytes(image, RootFileEntry(image));
    var position = 0;
    while (position + 38 <= directory.Length) {
      var implementationUse = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 36));
      var identifierLength = directory[position + 19];
      if (identifierLength > 1) {
        var text = Encoding.Latin1.GetString(
          directory, position + 39 + implementationUse, identifierLength - 1);
        if (text == name) {
          var block = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position + 24));
          return (int)((PartitionStart + block) * BlockSize);
        }
      }

      position += (38 + implementationUse + identifierLength + 3) & ~3;
    }

    throw new InvalidOperationException($"the root directory holds no entry named {name}");
  }
}
