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
