#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.UnixV7;

namespace Compression.Tests.UnixV7;

[TestFixture]
public sealed class UnixV7Tests {

  private static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)(i * 31 + seed + (i >> 9));
    return data;
  }

  private static byte[] Build(params (string Name, byte[] Data)[] files) {
    var w = new UnixV7Writer { Now = new DateTime(1979, 6, 8, 20, 58, 11, DateTimeKind.Utc) };
    foreach (var (name, data) in files) w.AddFile(name, data);
    return w.Build();
  }

  private static int[] Longs(byte[] image, int at) {
    var r = new int[50];
    for (var i = 0; i < 50; ++i) r[i] = PdpInt32(image.AsSpan(at + i * 4));
    return r;
  }

  private static int PdpInt32(ReadOnlySpan<byte> s)
    => (BinaryPrimitives.ReadUInt16LittleEndian(s) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);

  // 5120 = exactly the ten direct blocks; 5121 needs the single-indirect block;
  // 70657 needs the double-indirect tree (10 + 128 blocks + 1 byte).
  [TestCase(0)]
  [TestCase(1)]
  [TestCase(511)]
  [TestCase(512)]
  [TestCase(5120)]
  [TestCase(5121)]
  [TestCase(70656)]
  [TestCase(70657)]
  public void GivenAFileOfSize_WhenWrittenAndRead_ThenTheBytesRoundTrip(int size) {
    var data = Pattern(size, size);
    var image = Build(("f.bin", data));
    using var r = new UnixV7Reader(new MemoryStream(image));
    var e = r.Entries.Single();
    Assert.That(e.Size, Is.EqualTo(size));
    Assert.That(r.Extract(e), Is.EqualTo(data));
  }

  [Test, Category("Slow")]
  public void GivenAFileNeedingTheTripleIndirectTree_WhenWrittenAndRead_ThenTheBytesRoundTrip() {
    // 10 + 128 + 128*128 blocks reach 8 459 264 bytes; one more block needs the triple-indirect root.
    var data = Pattern(8_459_264 + 700, 7);
    var image = Build(("huge.bin", data));
    using var r = new UnixV7Reader(new MemoryStream(image));
    Assert.That(r.Extract(r.Entries.Single()), Is.EqualTo(data));
  }

  [Test]
  public void GivenNestedDirectories_WhenWrittenAndListed_ThenTheTreeAndTimesComeBack() {
    var w = new UnixV7Writer { Now = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
    w.AddFile("usr/src/cmd/ls.c", "main(){}\n"u8.ToArray(), new DateTime(1979, 1, 10, 12, 0, 0, DateTimeKind.Utc));
    w.AddFile("etc/passwd", "root::0:0::/:\n"u8.ToArray());
    w.AddDirectory("tmp");
    w.AddFile("abcdefghijklmn", [1]); // exactly 14 characters
    using var r = new UnixV7Reader(new MemoryStream(w.Build()));
    var names = r.Entries.Select(e => e.Name).ToList();
    Assert.That(names, Is.EquivalentTo(new[] { "usr", "usr/src", "usr/src/cmd", "usr/src/cmd/ls.c", "etc", "etc/passwd", "tmp", "abcdefghijklmn" }));
    var ls = r.Entries.Single(e => e.Name == "usr/src/cmd/ls.c");
    Assert.That(ls.LastModified, Is.EqualTo(new DateTime(1979, 1, 10, 12, 0, 0, DateTimeKind.Utc)));
    Assert.That(r.Entries.Single(e => e.Name == "tmp").IsDirectory, Is.True);
  }

  [Test]
  public void GivenAWrittenVolume_WhenTheFreeListIsWalked_ThenItHoldsExactlySTfreeUnusedBlocks() {
    var image = Build(("a", Pattern(40_000, 1)), ("b", Pattern(70_657, 2)));
    var sb = image.AsSpan(512, 512);
    var isize = BinaryPrimitives.ReadUInt16LittleEndian(sb);
    var fsize = PdpInt32(sb[2..]);
    var tfree = PdpInt32(sb[418..]);

    var seen = new HashSet<int>();
    var nfree = BinaryPrimitives.ReadInt16LittleEndian(sb[6..]);
    var cache = Longs(image, 512 + 8);
    while (true) {
      Assert.That(nfree, Is.InRange(1, 50));
      for (var i = nfree - 1; i >= 1; --i) Assert.That(seen.Add(cache[i]), Is.True, $"block {cache[i]} freed twice");
      var link = cache[0];
      if (link == 0) break;
      Assert.That(seen.Add(link), Is.True);
      var chain = image.AsSpan(link * 512, 512);
      nfree = BinaryPrimitives.ReadInt16LittleEndian(chain);
      cache = Longs(image, link * 512 + 2);
    }
    Assert.That(seen, Has.Count.EqualTo(tfree));
    Assert.That(seen.All(b => b >= isize && b < fsize), Is.True, "free blocks lie in the data area");
  }

  [Test]
  public void GivenANameOfFifteenCharacters_WhenOffered_ThenItIsRefused() {
    var d = new UnixV7FormatDescriptor();
    Assert.That(d.CanAccept(ArchiveInputInfo.InMemory("abcdefghijklmno", new byte[] { 1 }), out var why), Is.False);
    Assert.That(why, Does.Contain("14"));
    Assert.Throws<ArgumentException>(() => new UnixV7Writer().AddFile("dir/abcdefghijklmno", [1]));
  }

  [Test]
  public void GivenAFileAndADirectoryOfTheSameName_WhenAdded_ThenTheWriterRefuses() {
    var w = new UnixV7Writer();
    w.AddFile("x/y", [1]);
    Assert.Throws<ArgumentException>(() => w.AddFile("x", [2]));
  }

  [Test]
  public void GivenTheDescriptor_WhenCreatingAndListing_ThenEntriesComeBack() {
    var d = new UnixV7FormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("docs/readme", "hi"u8.ToArray())], new FormatCreateOptions());
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "docs", "docs/readme" }));
  }

  [Test]
  public void GivenARootInodeThatIsNotADirectory_WhenRead_ThenItIsRejected() {
    var image = Build(("a", [1]));
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1024 + 64), 0x8000 | 0x1A4);
    Assert.Throws<InvalidDataException>(() => _ = new UnixV7Reader(new MemoryStream(image)));
  }

  [TestCase(6, 51)]     // s_nfree past NICFREE
  [TestCase(208, 101)]  // s_ninode past NICINOD
  [TestCase(0, 2)]      // s_isize below the first inode block
  public void GivenAnImplausibleSuperblockField_WhenRead_ThenItIsRejected(int offset, int value) {
    var image = Build(("a", [1]));
    BinaryPrimitives.WriteInt16LittleEndian(image.AsSpan(512 + offset), (short)value);
    Assert.Throws<InvalidDataException>(() => _ = new UnixV7Reader(new MemoryStream(image)));
  }

  [Test]
  public void GivenAnImageTooSmall_WhenRead_ThenItIsRejected()
    => Assert.Throws<InvalidDataException>(() => _ = new UnixV7Reader(new MemoryStream(new byte[700])));

  [Test]
  public void GivenAVolumeCutShort_WhenListed_ThenListingSucceedsAndALostFileRefusesToExtract() {
    var image = Build(("small", [1, 2, 3]), ("big", Pattern(200_000, 9)));
    using var full = new UnixV7Reader(new MemoryStream(image));
    var cut = image.AsSpan(0, image.Length / 2).ToArray();
    using var r = new UnixV7Reader(new MemoryStream(cut));
    Assert.That(r.Extract(r.Entries.Single(e => e.Name == "small")), Is.EqualTo(new byte[] { 1, 2, 3 }));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.Single(e => e.Name == "big")));
  }
}
