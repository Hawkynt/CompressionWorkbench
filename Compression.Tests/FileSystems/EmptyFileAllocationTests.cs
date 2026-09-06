using Compression.Lib;
using Compression.Registry;
using NUnit.Framework;

namespace Compression.Tests.FileSystems;

/// <summary>
/// A zero-length file must not be given storage.
/// </summary>
/// <remarks>
/// <para>
/// Three writers rounded every file up to at least one block, empty ones
/// included, so the directory entry claimed a chain the size said was not there.
/// Each filesystem's own checker calls that corruption:
/// </para>
/// <list type="bullet">
///   <item><c>fsck.fat</c>: "File size is 0 bytes, cluster chain length is &gt; 0 bytes."</item>
///   <item><c>fsck.exfat</c>: "size 0, but the first cluster 0x…".</item>
///   <item><c>fsck.jfs</c>: corrupt data on the object, plus a block the allocation maps say is used and nothing owns.</item>
///   <item><c>fsck_hfs</c>: "Invalid extent entry" — an unused HFS+ extent descriptor is the all-zero one, so naming a start block with a count of zero is not a legal way to say "no blocks".</item>
///   <item><c>btrfs check</c>: "bad file extent" — btrfs gives an empty file no EXTENT_DATA item at all, rather than an inline extent describing zero bytes.</item>
/// </list>
/// <para>
/// A subdirectory is the opposite case and still needs a block of its own,
/// because its "." and ".." entries have to live somewhere — so these tests also
/// read back a file stored inside one, which only works if the directory kept
/// its allocation.
/// </para>
/// </remarks>
[TestFixture]
public sealed class EmptyFileAllocationTests {

  private static readonly IReadOnlyList<ArchiveInputInfo> Inputs = [
    ArchiveInputInfo.InMemory("empty.txt", []),
    ArchiveInputInfo.InMemory("payload.bin", System.Text.Encoding.ASCII.GetBytes(new string('x', 5_000))),
    new("dir", "dir", IsDirectory: true),
    ArchiveInputInfo.InMemory("dir/inside.txt", System.Text.Encoding.ASCII.GetBytes("inside\n")),
  ];

  private static byte[] Create(string id) {
    FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps(id);
    Assert.That(ops, Is.InstanceOf<IArchiveCreatable>(), $"{id} cannot create volumes");

    using var output = new MemoryStream();
    ((IArchiveCreatable)ops!).Create(output, Inputs, new FormatCreateOptions());
    return output.ToArray();
  }

  [TestCase("Fat")]
  [TestCase("ExFat")]
  [TestCase("Jfs")]
  [TestCase("HfsPlus")]
  [TestCase("Btrfs")]
  [Category("RoundTrip")]
  public void AnEmptyFileSurvivesTheRoundTripWithoutStorage(string id) {
    var image = Create(id);

    using var input = new MemoryStream(image, writable: false);
    var ops = (IArchiveFormatOperations)FormatRegistry.GetArchiveOps(id)!;
    var entries = ops.List(input, password: null);

    var empty = entries.SingleOrDefault(entry =>
      !entry.IsDirectory && entry.Name.Replace('\\', '/').TrimEnd('/').EndsWith("empty.txt", StringComparison.OrdinalIgnoreCase));
    Assert.That(empty, Is.Not.Null, $"{id} lost the empty file");
    Assert.That(empty!.OriginalSize, Is.Zero);

    // the file with content is untouched by the change
    var payload = entries.SingleOrDefault(entry =>
      !entry.IsDirectory && entry.Name.Replace('\\', '/').TrimEnd('/').EndsWith("payload.bin", StringComparison.OrdinalIgnoreCase));
    Assert.That(payload, Is.Not.Null, $"{id} lost the non-empty file");
    Assert.That(payload!.OriginalSize, Is.EqualTo(5_000));

    // A subdirectory still needs its own allocation, so its contents must survive
    // — the fix must not have generalised from files to directories.
    var nested = entries.SingleOrDefault(entry =>
      !entry.IsDirectory && entry.Name.Replace('\\', '/').EndsWith("inside.txt", StringComparison.OrdinalIgnoreCase));
    Assert.That(nested, Is.Not.Null, $"{id} lost the file inside the subdirectory");
    Assert.That(nested!.OriginalSize, Is.EqualTo(7));
  }

  /// <summary>
  /// The directory entry of an empty FAT file names cluster 0 — the format's way
  /// of saying "no chain". Anything else leaves a chain longer than the size.
  /// </summary>
  [Test]
  [Category("RoundTrip")]
  public void FatNamesClusterZeroForAnEmptyFile() {
    var image = Create("Fat");

    // Walk the fixed root directory region and find the 8.3 entry for EMPTY.TXT.
    var bytesPerSector = BitConverter.ToUInt16(image, 11);
    var reservedSectors = BitConverter.ToUInt16(image, 14);
    var fatCount = image[16];
    var rootEntries = BitConverter.ToUInt16(image, 17);
    var sectorsPerFat = BitConverter.ToUInt16(image, 22);
    var rootOffset = (reservedSectors + fatCount * sectorsPerFat) * bytesPerSector;

    var found = false;
    for (var i = 0; i < rootEntries; ++i) {
      var entry = rootOffset + i * 32;
      if (entry + 32 > image.Length) break;
      if (image[entry] == 0x00) break;                       // end of directory
      if ((image[entry + 11] & 0x0F) == 0x0F) continue;      // long-name slot

      var name = System.Text.Encoding.ASCII.GetString(image, entry, 11).Trim();
      if (!name.StartsWith("EMPTY", StringComparison.Ordinal)) continue;

      found = true;
      var size = BitConverter.ToUInt32(image, entry + 28);
      var firstCluster = (BitConverter.ToUInt16(image, entry + 20) << 16) | BitConverter.ToUInt16(image, entry + 26);
      Assert.Multiple(() => {
        Assert.That(size, Is.Zero, "the entry should report no bytes");
        Assert.That(firstCluster, Is.Zero, "an empty file names cluster 0");
      });
      break;
    }

    Assert.That(found, Is.True, "no directory entry for the empty file");
  }
}
