using System.Buffers.Binary;
using System.Security.Cryptography;
using Compression.Core.Checksums;
using FileFormat.DiskDoubler;

namespace Compression.Tests.DiskDoubler;

/// <summary>
/// DiskDoubler methods 2, 5 and 7 and delta type 1, in files built from our encoders, read by our
/// reader and by The Unarchiver (<c>unar</c>), the format's maintained reader. Real files with
/// method 8 are covered by <see cref="DiskDoublerOracleTests"/>.
/// </summary>
[TestFixture]
public sealed class DiskDoublerMethodTests {
  internal static byte[] Payload(int size, int seed) {
    var r = new Random(seed);
    var words = new[] { "Disk", "Doubler ", "Salient ", "Mac ", "fork ", "\r" };
    var text = new System.Text.StringBuilder();
    while (text.Length < size)
      text.Append(words[r.Next(words.Length)]);
    var data = System.Text.Encoding.ASCII.GetBytes(text.ToString(0, size));
    for (var i = 0; i < size / 10; ++i)
      data[r.Next(size)] = (byte)r.Next(256);
    return data;
  }

  /// <summary>A DiskDoubler file with one data fork stored as <paramref name="packed"/> by <paramref name="method"/>.</summary>
  internal static byte[] File(byte[] packed, long size, int method, ushort checksum, int delta = 0, byte info1 = 0x2A) {
    var header = new byte[84];
    BinaryPrimitives.WriteUInt32BigEndian(header, 0xABCD0054);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)size);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), (uint)packed.Length);
    header[20] = (byte)method;
    header[22] = info1;
    "TEXTttxt"u8.CopyTo(header.AsSpan(32));
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(48), checksum);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(54), (ushort)delta);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(82), Crc16Ccitt.Compute(header.AsSpan(0, 82)));
    return [.. header, .. packed];
  }

  /// <summary>Methods 2 and 5: splay-tree coded bytes, XOR 0x5A on the output, byte-sum check.</summary>
  internal static byte[] Splay(byte[] data, int trees, bool countByte, int delta = 0) {
    var stored = delta == 1 ? DiskDoublerCodecs.ApplyDelta(data) : (byte[])data.Clone();
    var sum = 0;
    foreach (var b in stored)
      sum += b;
    var masked = stored.Select(b => (byte)(b ^ 0x5A)).ToArray();
    var coded = DiskDoublerCodecs.SplayEncode(masked, trees);
    var packed = countByte ? [(byte)(trees == 256 ? 0 : trees), .. coded] : coded;
    return File(packed, data.Length, countByte ? 5 : 2, (ushort)sum, delta);
  }

  /// <summary>Method 7: an empty block table, then bit-inverted Stac LZS of the inverted bytes, XOR-sum check.</summary>
  internal static byte[] Lzs(byte[] data, int delta = 0) {
    var stored = delta == 1 ? DiskDoublerCodecs.ApplyDelta(data) : (byte[])data.Clone();
    byte xor = 0;
    foreach (var b in stored)
      xor ^= b;
    var coded = DiskDoublerCodecs.LzsEncode(stored.Select(b => (byte)(b ^ 0xFF)).ToArray()).Select(b => (byte)(b ^ 0xFF));
    var packed = new byte[18].Concat(coded).ToArray();
    var check = (byte)(xor ^ ((data.Length & 1) == 0 ? 0xFF : 0));
    return File(packed, data.Length, 7, check, delta);
  }

  public static IEnumerable<TestCaseData> Files() {
    var data = Payload(30000, 7);
    yield return new TestCaseData("method2", Splay(data, 256, countByte: false)).SetArgDisplayNames("method 2");
    yield return new TestCaseData("method5-16", Splay(data, 16, countByte: true)).SetArgDisplayNames("method 5, 16 trees");
    yield return new TestCaseData("method5-256", Splay(data, 256, countByte: true)).SetArgDisplayNames("method 5, 256 trees");
    yield return new TestCaseData("method7", Lzs(data)).SetArgDisplayNames("method 7");
    yield return new TestCaseData("method7-delta", Lzs(data, delta: 1)).SetArgDisplayNames("method 7, delta 1");
    yield return new TestCaseData("method2-delta", Splay(data, 256, countByte: false, delta: 1)).SetArgDisplayNames("method 2, delta 1");
  }

  [TestCaseSource(nameof(Files))]
  public void GivenAFileOfTheMethod_WhenRead_ThenTheForkIsRestored(string _, byte[] file) {
    using var r = new DiskDoublerReader(new MemoryStream(file), name: "x");
    Assert.That(r.Entries[0].IsSupported, Is.True);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(Payload(30000, 7)));
  }

  [Test]
  public void GivenADamagedChecksum_WhenRead_ThenItIsRefused() {
    var file = Lzs(Payload(5000, 3));
    file[49] ^= 1;
    BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(82), Crc16Ccitt.Compute(file.AsSpan(0, 82)));
    using var r = new DiskDoublerReader(new MemoryStream(file), name: "x");
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries[0]));
  }

  [TestCase(3)]
  [TestCase(4)]
  [TestCase(10)]
  public void GivenAMethodWithoutAProvenDecoder_WhenRead_ThenItIsRefused(int method) {
    var file = File([1, 2, 3, 4], 4, method, 0);
    using var r = new DiskDoublerReader(new MemoryStream(file), name: "x");
    Assert.That(r.Entries[0].IsSupported, Is.False);
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[0]));
  }

  [Test]
  public void GivenADeltaTypeOtherThanOne_WhenRead_ThenItIsRefused() {
    var file = File([1, 2, 3, 4], 4, 0, 0, delta: 2);
    using var r = new DiskDoublerReader(new MemoryStream(file), name: "x");
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries[0]));
  }

  [TestCaseSource(nameof(Files))]
  [Category("ExternalInterop")]
  public void GivenAFileOfTheMethod_WhenUnarExtractsIt_ThenTheForkMatches(string name, byte[] file) {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("unar"))
      Assert.Ignore("unar is not installed: `sudo apt install -y unar`.");
    var dir = Path.Combine(Path.GetTempPath(), "cwb-ddm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      var path = Path.Combine(dir, name + ".dd");
      System.IO.File.WriteAllBytes(path, file);
      var outDir = Path.Combine(dir, "out");
      var r = FsInteropToolbox.RunWsl($"unar -q -f -D -o {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(path)}");
      TestContext.Out.WriteLine(r.StdOut + r.StdErr);
      Assert.That(r.ExitCode, Is.Zero, "unar refused the file");
      var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
      Assert.That(files, Has.Length.EqualTo(1));
      Assert.That(SHA256.HashData(System.IO.File.ReadAllBytes(files[0])), Is.EqualTo(SHA256.HashData(Payload(30000, 7))));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
