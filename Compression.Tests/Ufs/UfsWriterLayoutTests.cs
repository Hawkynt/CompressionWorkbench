#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Ufs;

namespace Compression.Tests.Ufs;

/// <summary>What the UFS1 writer allocates, checked on the bytes it writes.</summary>
[TestFixture]
public sealed class UfsWriterLayoutTests {
  private const int SuperblockOffset = 8192;

  private static byte[] Create(params (string Name, byte[] Data)[] files) {
    using var ms = new MemoryStream();
    new UfsFormatDescriptor().Create(ms, files.Select(f => ArchiveInputInfo.InMemory(f.Name, f.Data)).ToList(), new FormatCreateOptions());
    return ms.ToArray();
  }

  /// <summary>The 128-byte UFS1 inode <paramref name="ino"/> of a single-group-aware layout.</summary>
  private static ReadOnlySpan<byte> Inode(byte[] image, int ino) {
    var sb = image.AsSpan(SuperblockOffset);
    var iblkno = BinaryPrimitives.ReadInt32LittleEndian(sb[16..]);
    var fsize = BinaryPrimitives.ReadInt32LittleEndian(sb[52..]);
    var ipg = BinaryPrimitives.ReadInt32LittleEndian(sb[184..]);
    var fpg = BinaryPrimitives.ReadInt32LittleEndian(sb[188..]);
    var at = ((long)(ino / ipg) * fpg + iblkno) * fsize + (long)(ino % ipg) * 128;
    return image.AsSpan((int)at, 128);
  }

  private static int InodeOf(byte[] image, string name) {
    using var r = new UfsReader(new MemoryStream(image));
    return (int)r.Entries.Single(e => e.Name == name).Inode;
  }

  [Test]
  public void GivenAnEmptyFile_WhenWritten_ThenItHoldsNoBlock() {
    var image = Create(("empty.bin", []), ("x.txt", "x"u8.ToArray()));
    var inode = Inode(image, InodeOf(image, "empty.bin"));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(inode[104..]), Is.Zero, "di_blocks");
    for (var i = 0; i < 15; ++i) Assert.That(BinaryPrimitives.ReadInt32LittleEndian(inode[(40 + i * 4)..]), Is.Zero, $"pointer {i}");
  }

  // 8 KiB blocks, 1 KiB fragments: twelve direct blocks hold 98 304 bytes.
  [TestCase(98_304 + 1, 13 * 8 + 8)]   // one byte into block 13: a whole block, plus the pointer block
  [TestCase(98_304, 12 * 8)]           // exactly the direct blocks
  [TestCase(98_304 - 1, 12 * 8)]       // the last direct block is all but full: eight fragments
  [TestCase(81_920 + 1, 10 * 8 + 1)]   // a one-fragment tail in the eleventh block
  public void GivenAFileOfSize_WhenWritten_ThenDiBlocksCountsWholeBlocksPastTheDirectOnes(int size, int fragments) {
    var image = Create(("f.bin", new byte[size]));
    var inode = Inode(image, InodeOf(image, "f.bin"));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(inode[104..]), Is.EqualTo((uint)(fragments * 2)), "di_blocks in 512-byte sectors");
  }

  [TestCase("abcd")]
  [TestCase("efghijkl")]
  [TestCase("abc")]
  public void GivenANameAheadOfOthers_WhenWritten_ThenItsRecordHoldsTheNameAndItsNul(string name) {
    var image = Create((name, [1]), ("zz", [2]));
    using var r = new UfsReader(new MemoryStream(image));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { name, "zz" }));
    var dirsiz = (8 + name.Length + 1 + 3) & ~3;
    var at = image.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(name + "\0"));
    Assert.That(at, Is.GreaterThan(8));
    var reclen = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(at - 4));
    Assert.That(reclen, Is.GreaterThanOrEqualTo(dirsiz));
  }
}
