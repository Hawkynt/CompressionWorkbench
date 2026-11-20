using System.Reflection;
using FileFormat.EcSbx;

namespace Compression.Tests.EcSbx;

/// <summary>
/// Error-correcting SeqBox. Both fixtures were written by blkar 7.2.7 for the same 3000-byte file (version 18,
/// 5+2 shards, burst 3; version 17, 10+3 shards, burst 1); <c>blkar show</c>'s view of them is pinned here.
/// See ReferenceVectors/README.md.
/// </summary>
[TestFixture]
public class EcSbxTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"EcSbxVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded EC-SBX vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [TestCase("fox-v18.ecsbx", 18, 5, 2)]
  [TestCase("fox-v17.ecsbx", 17, 10, 3)]
  [Category("ThirdPartyVector")]
  public void Given_BlkarContainer_When_Read_Then_TheOriginalFileAndBlkarsMetadata(string vector, int version, int data, int parity) {
    var c = EcSbxFormatDescriptor.Read(Vector(vector));
    Assert.That((c.Version, c.FileName, c.DataShards, c.ParityShards), Is.EqualTo((version, "fox.bin", data, parity)));
    Assert.That(c.Data, Is.EqualTo(Vector("fox.bin")));
  }

  [Test, Category("ThirdPartyVector")]
  public void Given_DamagedParityAndFirstMetadataCopy_When_Read_Then_StillTheOriginal() {
    var bytes = Vector("fox-v18.ecsbx");
    bytes[20] ^= 0xFF;               // metadata copy 0 (two more copies follow)
    var parity = FindSeq(bytes, 128, 6); // sequence 6 = first parity block of set 0 (5 data + 2 parity)
    bytes[parity + 40] ^= 0xFF;
    Assert.That(EcSbxFormatDescriptor.Read(bytes).Data, Is.EqualTo(Vector("fox.bin")));
  }

  [Test, Category("EdgeCase")]
  public void Given_DamagedDataBlock_When_Read_Then_RepairRefused() {
    var bytes = Vector("fox-v18.ecsbx");
    bytes[FindSeq(bytes, 128, 2) + 50] ^= 0xFF;
    Assert.Throws<NotSupportedException>(() => EcSbxFormatDescriptor.Read(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Given_NotABlockMultipleOrForeignVersion_When_Read_Then_InvalidData() {
    var bytes = Vector("fox-v18.ecsbx");
    Assert.Throws<InvalidDataException>(() => EcSbxFormatDescriptor.Read(bytes[..^1]));
    bytes[3] = 1; // plain SeqBox v1 belongs to the SeqBox descriptor
    Assert.Throws<InvalidDataException>(() => EcSbxFormatDescriptor.Read(bytes));
  }

  [Test, Category("HappyPath")]
  public void Given_EcSbxPath_When_ListedAndDetected_Then_OneFileNamedFromMetadata() {
    var list = new EcSbxFormatDescriptor().List(new MemoryStream(Vector("fox-v17.ecsbx")), null);
    Assert.That(list.Select(e => (e.Name, e.OriginalSize)), Is.EqualTo(new[] { ("fox.bin", 3000L) }));
    var path = Path.Combine(Path.GetTempPath(), $"cwb_ecsbx_{Guid.NewGuid():N}.ecsbx");
    File.WriteAllBytes(path, Vector("fox-v17.ecsbx"));
    try {
      Assert.That(Compression.Lib.FormatDetector.Detect(path).ToString(), Is.EqualTo("EcSbx"));
    } finally { File.Delete(path); }
  }

  /// <summary>The UID, SNM, FDT and SDT a container's metadata block holds.</summary>
  private static (byte[] Uid, string Snm, DateTime? Fdt, DateTime Sdt) Meta(byte[] container) {
    var uid = container[6..12];
    string snm = "";
    DateTime? fdt = null;
    var sdt = DateTime.MinValue;
    for (var at = 16; at + 4 <= container.Length && container[at] != 0x1A;) {
      var id = System.Text.Encoding.ASCII.GetString(container, at, 3);
      var len = container[at + 3];
      var value = container.AsSpan(at + 4, len);
      switch (id) {
        case "SNM": snm = System.Text.Encoding.UTF8.GetString(value); break;
        case "FDT": fdt = DateTimeOffset.FromUnixTimeSeconds((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value)).UtcDateTime; break;
        case "SDT": sdt = DateTimeOffset.FromUnixTimeSeconds((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(value)).UtcDateTime; break;
      }
      at += 4 + len;
    }
    return (uid, snm, fdt, sdt);
  }

  [TestCase("fox-v18.ecsbx", 18, 5, 2, 3)]
  [TestCase("fox-v17.ecsbx", 17, 10, 3, 1)]
  [Category("ThirdPartyVector")]
  public void Given_BlkarsInputAndParameters_When_WeCreateTheContainer_Then_ItIsByteForByteBlkars(string vector, int version, int data, int parity, int burst) {
    var expected = Vector(vector);
    var (uid, snm, fdt, sdt) = Meta(expected);
    var ours = EcSbxWriter.Create(Vector("fox.bin"), "fox.bin", snm, fdt, version, data, parity, burst, uid, sdt);
    Assert.That(ours, Is.EqualTo(expected));
  }

  [TestCase(0, 17, 10, 2, 12), TestCase(1, 17, 10, 2, 12), TestCase(496 * 10, 17, 10, 2, 0), TestCase(100_000, 19, 4, 3, 5),
   TestCase(5000, 18, 1, 1, 1), TestCase(70_000, 17, 128, 128, 2), TestCase(33_333, 17, 7, 3, 100)]
  [Category("RoundTrip")]
  public void Given_AFile_When_ContainedAndReadBack_Then_ItIsTheOriginal(int size, int version, int data, int parity, int burst) {
    var bytes = new byte[size];
    new Random(size + version).NextBytes(bytes);
    var c = EcSbxFormatDescriptor.Read(EcSbxWriter.Create(bytes, "in.bin", "in.bin.ecsbx", null, version, data, parity, burst));
    Assert.That((c.Version, c.FileName, c.DataShards, c.ParityShards), Is.EqualTo((version, "in.bin", data, parity)));
    Assert.That(c.Data, Is.EqualTo(bytes));
  }

  [Test, Category("Exception")]
  public void Given_BadParameters_When_Created_Then_Refused() {
    Assert.Throws<InvalidDataException>(() => EcSbxWriter.Create([1], "a", "a.ecsbx", version: 1));
    Assert.Throws<ArgumentOutOfRangeException>(() => EcSbxWriter.Create([1], "a", "a.ecsbx", dataShards: 0));
    Assert.Throws<ArgumentOutOfRangeException>(() => EcSbxWriter.Create([1], "a", "a.ecsbx", dataShards: 200, parityShards: 100));
    Assert.Throws<ArgumentOutOfRangeException>(() => EcSbxWriter.Create([1], "a", "a.ecsbx", burst: -1));
    Assert.Throws<ArgumentException>(() => EcSbxWriter.Create([1], "a", "a.ecsbx", uid: [1, 2]));
    Assert.Throws<ArgumentException>(() => EcSbxWriter.Create([1], new string('n', 60), new string('s', 60), version: 18));
  }

  [Test, Category("EndToEnd")]
  public void Given_OurContainer_When_BlkarDecodesAndRepairsIt_Then_TheFileComesBack() {
    var blkar = Environment.GetEnvironmentVariable("CWB_BLKAR");
    if (string.IsNullOrEmpty(blkar)) Assert.Ignore("Set CWB_BLKAR to a blkar binary (github.com/darrenldl/blockyarchive releases).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecsbx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = new byte[150_000];
      new Random(12).NextBytes(data);
      var container = new MemoryStream();
      new EcSbxFormatDescriptor().Create(container, [Compression.Registry.ArchiveInputInfo.InMemory("in.bin", data)],
        new Compression.Registry.FormatCreateOptions { FormatSpecific = { ["Version"] = "17", ["DataShards"] = "6", ["ParityShards"] = "3", ["Burst"] = "4" } });
      var bytes = container.ToArray();
      File.WriteAllBytes(Path.Combine(dir, "ours.ecsbx"), bytes);
      // a burst of 4 consecutive blocks and three more scattered ones
      Array.Clear(bytes, 512 * 40, 512 * 4);
      bytes[512 * 100 + 300] ^= 0xFF;
      bytes[512 * 151 + 20] ^= 0xFF;
      bytes[512 * 200 + 500] ^= 0xFF;
      File.WriteAllBytes(Path.Combine(dir, "bad.ecsbx"), bytes);
      var w = FsInteropToolbox.WinToWsl(dir);
      var tool = blkar.StartsWith('/') ? blkar : FsInteropToolbox.WinToWsl(blkar);
      var run = FsInteropToolbox.RunWsl($"cd {w} && {tool} check ours.ecsbx && {tool} decode ours.ecsbx out.bin >/dev/null && echo DECODED"
        + $" && {tool} repair --skip-warning bad.ecsbx && {tool} decode bad.ecsbx fixed.bin >/dev/null && echo REPAIRED");
      Assert.That(run.StdOut, Does.Contain("DECODED").And.Contain("REPAIRED"), run.StdOut + run.StdErr);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "out.bin")), Is.EqualTo(data));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "fixed.bin")), Is.EqualTo(data));
    } finally { Directory.Delete(dir, true); }
  }

  private static int FindSeq(byte[] container, int blockSize, uint seq) {
    for (var off = 0; off < container.Length; off += blockSize)
      if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(container.AsSpan(off + 12)) == seq) return off;
    throw new AssertionException($"sequence {seq} not found");
  }

  [Test, Category("EndToEnd")]
  public void Given_FreshBlkarContainer_When_ReadByUs_Then_SameAsBlkarDecodes() {
    var blkar = Environment.GetEnvironmentVariable("CWB_BLKAR");
    if (string.IsNullOrEmpty(blkar)) Assert.Ignore("Set CWB_BLKAR to a blkar binary (github.com/darrenldl/blockyarchive releases).");
    var dir = Path.Combine(Path.GetTempPath(), $"cwb_ecsbx_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var data = new byte[20000];
      new Random(9).NextBytes(data);
      File.WriteAllBytes(Path.Combine(dir, "in.bin"), data);
      var w = FsInteropToolbox.WinToWsl(dir);
      var tool = blkar.StartsWith('/') ? blkar : FsInteropToolbox.WinToWsl(blkar);
      var run = FsInteropToolbox.RunWsl($"cd {w} && {tool} encode --sbx-version 19 --rs-data 4 --rs-parity 2 --burst 2 in.bin c.ecsbx >/dev/null 2>&1; {tool} decode c.ecsbx out.bin >/dev/null 2>&1; cmp in.bin out.bin && echo same");
      Assert.That(run.StdOut, Does.Contain("same"), run.StdErr);
      Assert.That(EcSbxFormatDescriptor.Read(File.ReadAllBytes(Path.Combine(dir, "c.ecsbx"))).Data, Is.EqualTo(File.ReadAllBytes(Path.Combine(dir, "out.bin"))));
    } finally { Directory.Delete(dir, true); }
  }
}
