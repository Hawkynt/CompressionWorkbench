#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.RomFs;

namespace Compression.Tests.RomFs;

/// <summary>
/// The shape Linux insists on before it will mount a ROMFS image at all.
/// </summary>
/// <remarks>
/// Every one of these was wrong at once, and together they meant no image this
/// writer produced could be mounted: the superblock checksum summed the
/// superblock instead of the first 512 bytes ("bad initial checksum"), no
/// directory chain carried its own "." and ".." so the record the kernel takes
/// as the root inode was an ordinary file, directory records lacked the
/// executable bit so nothing inside could be traversed, and the image ended
/// part way through a block, which a loop device rounds off along with whatever
/// record was written last.
/// </remarks>
[TestFixture]
public class RomFsKernelShapeTests {

  /// <summary>Header words the checksum covers, as Linux reads them.</summary>
  private const int ChecksumSpan = 512;

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    using var output = new MemoryStream();
    var writer = new RomFsWriter(output, leaveOpen: true);
    foreach (var (name, data) in files) writer.AddFile(name, data);
    writer.Finish();
    return output.ToArray();
  }

  private static byte[] Payload(int seed, int length) {
    var data = new byte[length];
    for (var i = 0; i < length; ++i) data[i] = (byte)((i * 13 + seed * 29) % 251);
    return data;
  }

  /// <summary>The sum Linux takes over the head of the image; it must be zero.</summary>
  private static uint InitialChecksum(byte[] image) {
    var covered = Math.Min(ChecksumSpan, image.Length) & ~3;
    uint sum = 0;
    for (var i = 0; i < covered; i += 4)
      sum += BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(i));
    return sum;
  }

  /// <summary>Offset of the first record, which the kernel takes as the root inode.</summary>
  private static long RootRecord(byte[] image) {
    var end = 16;
    while (end < image.Length && image[end] != 0) ++end;
    return 16 + ((end - 16 + 1 + 15) & ~15);
  }

  private static (int Type, long Next, long Spec, string Name) ReadRecord(byte[] image, long at) {
    var nextAndType = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)at));
    var spec = BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan((int)at + 4));
    var end = (int)at + 16;
    while (end < image.Length && image[end] != 0) ++end;
    var name = Encoding.ASCII.GetString(image, (int)at + 16, end - (int)at - 16);
    return ((int)(nextAndType & 0x0F), nextAndType & 0xFFFFFFF0u, spec, name);
  }

  [Test]
  public void FreshImage_SumsToZeroOverTheFirst512Bytes() {
    var image = BuildImage(("A.BIN", Payload(1, 4096)), ("B.BIN", Payload(2, 300)));
    Assert.That(InitialChecksum(image), Is.Zero,
      "Linux sums the first 512 bytes and refuses the volume when the total is not zero");
  }

  [Test]
  public void FreshImage_EndsOnAWholeBlock() {
    var image = BuildImage(("A.BIN", Payload(1, 4096)));
    Assert.That(image.Length % 1024, Is.Zero,
      "a loop device rounds down to whole blocks and would cut the tail off");
  }

  [Test]
  public void RootRecord_IsADirectoryNamedDotAndIsTraversable() {
    var image = BuildImage(("A.BIN", Payload(1, 64)));
    var (type, next, spec, name) = ReadRecord(image, RootRecord(image));

    Assert.Multiple(() => {
      Assert.That(name, Is.EqualTo("."), "the kernel takes this record as the root inode");
      Assert.That(type & 7, Is.EqualTo(1), "the root inode has to be a directory");
      Assert.That(type & 8, Is.EqualTo(8),
        "without the executable bit the root is mode 0644 and nothing inside can be reached");
      Assert.That(spec, Is.EqualTo(RootRecord(image)), "the root's contents start at its own chain");
      Assert.That(next, Is.Not.Zero, "\"..\" follows \".\"");
    });

    var (parentType, _, _, parentName) = ReadRecord(image, next);
    Assert.Multiple(() => {
      Assert.That(parentName, Is.EqualTo(".."));
      Assert.That(parentType & 7, Is.EqualTo(1));
    });
  }

  [Test]
  public void Subdirectory_ChainOpensWithItsOwnDotRecords() {
    var image = BuildImage(("sub/A.BIN", Payload(3, 64)));

    // Walk the root chain to the "sub" record.
    var at = RootRecord(image);
    (int Type, long Next, long Spec, string Name) record;
    while (true) {
      record = ReadRecord(image, at);
      if (record.Name == "sub") break;
      Assert.That(record.Next, Is.Not.Zero, "the subdirectory must be in the root chain");
      at = record.Next;
    }

    Assert.That(record.Type & 8, Is.EqualTo(8), "a directory must be traversable");
    var (childType, childNext, childSpec, childName) = ReadRecord(image, record.Spec);
    Assert.Multiple(() => {
      Assert.That(childName, Is.EqualTo("."), "a directory's chain opens with its own \".\"");
      Assert.That(childSpec, Is.EqualTo(record.Spec), "\".\" names the chain it opens");
      Assert.That(childType & 7, Is.EqualTo(1));
    });

    var (_, _, parentSpec, parentName) = ReadRecord(image, childNext);
    Assert.Multiple(() => {
      Assert.That(parentName, Is.EqualTo(".."));
      Assert.That(parentSpec, Is.EqualTo(RootRecord(image)), "\"..\" names the chain above");
    });
  }

  [Test, Category("RoundTrip")]
  public void AddAndRemove_LeaveTheImageMountable() {
    var image = BuildImage(("A.BIN", Payload(1, 2048)), ("B.BIN", Payload(2, 2048)),
      ("C.BIN", Payload(3, 2048)));

    using var stream = new MemoryStream();
    stream.Write(image, 0, image.Length);

    var descriptor = new RomFsFormatDescriptor();
    stream.Position = 0;
    descriptor.Remove(stream, ["B.BIN"]);

    var added = Payload(9, 5000);
    var path = Path.Combine(Path.GetTempPath(), "cwb_romfs_" + Guid.NewGuid().ToString("N")[..8]);
    File.WriteAllBytes(path, added);
    try {
      stream.Position = 0;
      descriptor.Add(stream, [new ArchiveInputInfo(path, "D.BIN", false)]);
    } finally {
      File.Delete(path);
    }

    var modified = stream.ToArray();
    Assert.Multiple(() => {
      Assert.That(InitialChecksum(modified), Is.Zero,
        "adding and removing move bytes the superblock checksum covers");
      Assert.That(modified.Length % 1024, Is.Zero,
        "the appended record must not sit past the last whole block");
    });

    stream.Position = 0;
    var reader = new RomFsReader(stream);
    Assert.That(reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Name),
      Is.EquivalentTo(new[] { "A.BIN", "C.BIN", "D.BIN" }));
    Assert.That(reader.Extract(reader.Entries.First(e => e.Name == "D.BIN")), Is.EqualTo(added));
  }

  [Test, Category("RoundTrip")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void Defragment_LeavesTheImageMountable(DefragMode mode) {
    var image = BuildImage(("A.BIN", Payload(1, 2048)), ("B.BIN", Payload(2, 3072)),
      ("C.BIN", Payload(3, 2048)));

    using var stream = new MemoryStream();
    stream.Write(image, 0, image.Length);
    stream.Position = 0;
    new RomFsFormatDescriptor().Defragment(stream, new DefragOptions { Mode = mode });

    var after = stream.ToArray();
    Assert.Multiple(() => {
      Assert.That(InitialChecksum(after), Is.Zero, "the head of the image must still sum to zero");
      Assert.That(after.Length % 1024, Is.Zero, "the image must still end on a whole block");
    });

    var (type, _, _, name) = ReadRecord(after, RootRecord(after));
    Assert.Multiple(() => {
      Assert.That(name, Is.EqualTo("."), "the root inode must still be the root directory");
      Assert.That(type & 8, Is.EqualTo(8));
    });

    stream.Position = 0;
    var reader = new RomFsReader(stream);
    Assert.That(reader.Extract(reader.Entries.First(e => e.Name == "B.BIN")),
      Is.EqualTo(Payload(2, 3072)));
  }
}
