using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Zfs;

namespace Compression.Tests.Zfs;

[TestFixture]
public class ZfsTests {

  private static byte[] BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new ZfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var desc = new ZfsFormatDescriptor();
    Assert.That(desc.Id, Is.EqualTo("Zfs"));
    Assert.That(desc.DefaultExtension, Is.EqualTo(".zfs"));
    Assert.That(desc.Extensions, Does.Contain(".zpool"));
    Assert.That(desc.Category, Is.EqualTo(FormatCategory.Archive));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_TooSmall_Throws() {
    using var ms = new MemoryStream(new byte[100]);
    Assert.Throws<InvalidDataException>(() => _ = new ZfsReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_NoUberblock_Throws() {
    var data = new byte[512 * 1024];
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => _ = new ZfsReader(ms));
  }

  // ---------- Writer spec tests ----------

  [Test, Category("HappyPath")]
  public void Writer_FourLabelsIdentical() {
    var img = BuildImage(("hello.txt", "hello world"u8.ToArray()));
    const int ls = 256 * 1024;
    var l0 = img.AsSpan(0, ls).ToArray();
    var l1 = img.AsSpan(ls, ls).ToArray();
    var l2 = img.AsSpan(img.Length - 2 * ls, ls).ToArray();
    var l3 = img.AsSpan(img.Length - ls, ls).ToArray();
    Assert.That(l1, Is.EqualTo(l0), "L1 must equal L0");
    Assert.That(l2, Is.EqualTo(l0), "L2 must equal L0");
    Assert.That(l3, Is.EqualTo(l0), "L3 must equal L0");
  }

  [Test, Category("HappyPath")]
  public void Writer_UberblockMagicCorrect() {
    var img = BuildImage(("f.txt", new byte[] { 1, 2, 3 }));
    const int ubArrayOff = 128 * 1024;
    var magic = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(ubArrayOff, 8));
    Assert.That(magic, Is.EqualTo(0x00BAB10CUL), "Uberblock magic at label 0");
  }

  [Test, Category("HappyPath")]
  public void Writer_UberblockRingAllSlotsExist() {
    // 127 slots should be zero, 1 active (slot 0 in our case).
    var img = BuildImage(("f.txt", new byte[] { 1 }));
    const int ubArrayOff = 128 * 1024;
    const int slotSize = 1024;
    var activeCount = 0;
    for (var i = 0; i < 128; i++) {
      var off = ubArrayOff + i * slotSize;
      var magic = BinaryPrimitives.ReadUInt64LittleEndian(img.AsSpan(off, 8));
      if (magic == 0x00BAB10CUL) activeCount++;
    }
    Assert.That(activeCount, Is.EqualTo(1), "Exactly one uberblock slot should be active");
  }

  [Test, Category("HappyPath")]
  public void Writer_Fletcher4ComputesKnownVector() {
    // Fletcher-4 of 16 bytes "ZFS-test-vector!" (LE uint32 decomposition).
    // Words (LE): 0x532D53465A, 0x74736574, 0x6576 ...
    // Simpler: compute against zeros and a known ascending pattern.
    var zeros = new byte[64];
    var fz = Fletcher4.Compute(zeros);
    Assert.That(fz.A, Is.EqualTo(0UL));
    Assert.That(fz.B, Is.EqualTo(0UL));
    Assert.That(fz.C, Is.EqualTo(0UL));
    Assert.That(fz.D, Is.EqualTo(0UL));

    // Pattern: 4 words [1, 2, 3, 4].
    var pat = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(pat.AsSpan(0, 4), 1u);
    BinaryPrimitives.WriteUInt32LittleEndian(pat.AsSpan(4, 4), 2u);
    BinaryPrimitives.WriteUInt32LittleEndian(pat.AsSpan(8, 4), 3u);
    BinaryPrimitives.WriteUInt32LittleEndian(pat.AsSpan(12, 4), 4u);
    var fp = Fletcher4.Compute(pat);
    // a = 1+2+3+4 = 10
    // b = 1 + 3 + 6 + 10 = 20
    // c = 1 + 4 + 10 + 20 = 35
    // d = 1 + 5 + 15 + 35 = 56
    Assert.That(fp.A, Is.EqualTo(10UL));
    Assert.That(fp.B, Is.EqualTo(20UL));
    Assert.That(fp.C, Is.EqualTo(35UL));
    Assert.That(fp.D, Is.EqualTo(56UL));
  }

  [Test, Category("HappyPath")]
  public void Writer_NvListParsableSpecCompliant() {
    var img = BuildImage(("f.txt", new byte[] { 1, 2, 3 }));
    const int nvOff = 16 * 1024;
    const int nvLen = 112 * 1024;
    var nvBytes = img.AsSpan(nvOff, nvLen).ToArray();
    Assert.That(nvBytes[0], Is.EqualTo(0x01), "NV_ENCODE_XDR");
    Assert.That(nvBytes[1], Is.EqualTo(0x00), "NV_BIG_ENDIAN");

    var nv = XdrNvList.Decode(nvBytes);
    string? name = null;
    ulong version = 0;
    XdrNvList.NvList? vdevTree = null;
    foreach (var (k, t, v) in nv.Pairs) {
      if (k == "name" && t == XdrNvList.DataType.String) name = (string)v;
      if (k == "version" && t == XdrNvList.DataType.UInt64) version = (ulong)v;
      if (k == "vdev_tree" && t == XdrNvList.DataType.NvList) vdevTree = (XdrNvList.NvList)v;
    }
    Assert.That(name, Is.EqualTo("compworkbench"));
    Assert.That(version, Is.EqualTo(28UL));
    Assert.That(vdevTree, Is.Not.Null);
    var vdevType = vdevTree!.Pairs.First(p => p.Name == "type").Value;
    Assert.That(vdevType, Is.EqualTo("disk"));
    var ashift = vdevTree.Pairs.First(p => p.Name == "ashift").Value;
    Assert.That(ashift, Is.EqualTo(9UL));
  }

  [Test, Category("HappyPath")]
  public void Writer_MosObjectDirectoryHasRootDataset() {
    var img = BuildImage(("a.txt", new byte[] { 0xAA }));
    using var ms = new MemoryStream(img);
    using var r = new ZfsReader(ms);
    Assert.That(r.PoolName, Is.EqualTo("compworkbench"));
    Assert.That(r.Entries.Count, Is.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("a.txt"));
  }

  [Test, Category("HappyPath")]
  public void Writer_RoundTripsMultipleFiles_VariousSizes() {
    // 3 files: tiny, medium, and > 8 KB to force multi-sector data block.
    var tiny = new byte[] { 1, 2, 3, 4, 5 };
    var medium = new byte[2048];
    for (var i = 0; i < medium.Length; i++) medium[i] = (byte)(i & 0xFF);
    var big = new byte[16 * 1024];   // 16 KB
    for (var i = 0; i < big.Length; i++) big[i] = (byte)((i * 37) & 0xFF);

    var img = BuildImage(
      ("tiny.bin", tiny),
      ("medium.bin", medium),
      ("big.bin", big));

    using var ms = new MemoryStream(img);
    using var r = new ZfsReader(ms);
    Assert.That(r.Entries.Count, Is.EqualTo(3));

    var tinyEntry = r.Entries.First(e => e.Name == "tiny.bin");
    var mediumEntry = r.Entries.First(e => e.Name == "medium.bin");
    var bigEntry = r.Entries.First(e => e.Name == "big.bin");
    Assert.That(tinyEntry.Size, Is.EqualTo(tiny.Length));
    Assert.That(mediumEntry.Size, Is.EqualTo(medium.Length));
    Assert.That(bigEntry.Size, Is.EqualTo(big.Length));

    Assert.That(r.Extract(tinyEntry), Is.EqualTo(tiny));
    Assert.That(r.Extract(mediumEntry), Is.EqualTo(medium));
    Assert.That(r.Extract(bigEntry), Is.EqualTo(big));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_CreateThenList() {
    var desc = new ZfsFormatDescriptor();
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanCreate));
    Assert.That(desc.MinTotalArchiveSize, Is.EqualTo(64L * 1024 * 1024));

    // ICreate path uses File.ReadAllBytes, so write inputs to disk.
    var tmpDir = Path.Combine(Path.GetTempPath(), "cwb-zfs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    var a = Path.Combine(tmpDir, "a.txt");
    File.WriteAllBytes(a, "aaa"u8.ToArray());

    try {
      var inputs = new List<ArchiveInputInfo> {
        new(a, "a.txt", false),
      };
      using var ms = new MemoryStream();
      desc.Create(ms, inputs, new FormatCreateOptions());
      ms.Position = 0;
      var list = desc.List(ms, null);
      Assert.That(list.Count, Is.EqualTo(1));
      Assert.That(list[0].Name, Is.EqualTo("a.txt"));
      Assert.That(list[0].OriginalSize, Is.EqualTo(3));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsDirectories() {
    var desc = new ZfsFormatDescriptor();
    Assert.That(desc.CanAccept(new ArchiveInputInfo("/x", "x", IsDirectory: true), out var reason), Is.False);
    Assert.That(reason, Is.Not.Null.And.Contains("Flat root"));
  }

  [Test, Category("ErrorHandling")]
  public void Writer_RejectsLongFilenames() {
    var desc = new ZfsFormatDescriptor();
    var longName = new string('x', 60);
    Assert.That(desc.CanAccept(new ArchiveInputInfo("/x", longName, IsDirectory: false), out var reason), Is.False);
    Assert.That(reason, Is.Not.Null.And.Contains("49-char"));
  }
}
