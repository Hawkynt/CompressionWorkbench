#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.UnityBundle;

namespace Compression.Tests.UnityBundle;

[TestFixture]
public class UnityBundleTests {

  private static byte[] MakeMinimalBundle(string nodeName, byte[] payload) {
    using var header = new MemoryStream();

    void WriteCStr(string s) {
      var b = Encoding.UTF8.GetBytes(s);
      header.Write(b);
      header.WriteByte(0);
    }
    void WriteU32BE(uint v) {
      Span<byte> b = stackalloc byte[4];
      BinaryPrimitives.WriteUInt32BigEndian(b, v);
      header.Write(b);
    }
    void WriteI64BE(long v) {
      Span<byte> b = stackalloc byte[8];
      BinaryPrimitives.WriteInt64BigEndian(b, v);
      header.Write(b);
    }

    header.Write("UnityFS\0"u8);
    WriteU32BE(6u);
    WriteCStr("5.x.x");
    WriteCStr("2019.4.11f1");

    using var biMs = new MemoryStream();
    biMs.Write(new byte[16]);
    void BiU32BE(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); biMs.Write(b); }
    void BiI32BE(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); biMs.Write(b); }
    void BiI64BE(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); biMs.Write(b); }
    void BiU16BE(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); biMs.Write(b); }
    void BiCStr(string s) { biMs.Write(Encoding.UTF8.GetBytes(s)); biMs.WriteByte(0); }

    BiI32BE(1);
    BiU32BE((uint)payload.Length);
    BiU32BE((uint)payload.Length);
    BiU16BE(0);
    BiI32BE(1);
    BiI64BE(0);
    BiI64BE(payload.Length);
    BiU32BE(0);
    BiCStr(nodeName);

    var blocksInfo = biMs.ToArray();
    WriteI64BE(0); // tolerated legacy/synthetic value
    WriteU32BE((uint)blocksInfo.Length);
    WriteU32BE((uint)blocksInfo.Length);
    WriteU32BE(0);
    header.Write(blocksInfo);
    header.Write(payload);
    return header.ToArray();
  }

  private static byte[] CreateBundle(
      string method,
      IReadOnlyList<ArchiveInputInfo> inputs,
      IReadOnlyDictionary<string, string>? formatSpecific = null,
      bool optimize = false) {
    using var ms = new MemoryStream();
    var d = new UnityBundleFormatDescriptor();
    d.Create(ms, inputs, new FormatCreateOptions {
      MethodName = method,
      Optimize = optimize,
      FormatSpecific = formatSpecific,
    });
    return ms.ToArray();
  }

  [Test]
  public void Reader_ParsesHeader_AndSurfacesSingleNode() {
    var payload = Encoding.UTF8.GetBytes("Hello, Unity!");
    var bundle = MakeMinimalBundle("Assets/hello.txt", payload);
    var reader = new UnityBundleReader(bundle);

    Assert.Multiple(() => {
      Assert.That(reader.Signature, Is.EqualTo("UnityFS"));
      Assert.That(reader.FormatVersion, Is.EqualTo(6u));
      Assert.That(reader.UnityVersion, Is.EqualTo("5.x.x"));
      Assert.That(reader.UnityRevision, Is.EqualTo("2019.4.11f1"));
      Assert.That(reader.Blocks, Has.Count.EqualTo(1));
      Assert.That(reader.Nodes, Has.Count.EqualTo(1));
      Assert.That(reader.Nodes[0].Path, Is.EqualTo("Assets/hello.txt"));
      Assert.That(reader.Nodes[0].Size, Is.EqualTo(payload.Length));
    });
  }

  [Test]
  public void Reader_ExtractsStoredNodePayload() {
    var payload = Encoding.UTF8.GetBytes("Hello, Unity!");
    var bundle = MakeMinimalBundle("Assets/hello.txt", payload);
    var reader = new UnityBundleReader(bundle);
    Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(payload));
  }

  [Test]
  public void Descriptor_ListsAndExtracts() {
    var payload = Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz");
    var bundle = MakeMinimalBundle("dir/inner/file.bin", payload);
    var descriptor = new UnityBundleFormatDescriptor();
    using var ms = new MemoryStream(bundle);
    var entries = descriptor.List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("dir/inner/file.bin"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(payload.Length));

    var tmpDir = Path.Combine(Path.GetTempPath(), "UnityBundleTests_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      ms.Position = 0;
      descriptor.Extract(ms, tmpDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(tmpDir, "dir", "inner", "file.bin")), Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test]
  public void Descriptor_AdvertisesWormAndRebuildVerbsWithoutFalseInPlaceClaim() {
    var d = new UnityBundleFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsOptimize), Is.True);
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
      Assert.That(d, Is.InstanceOf<IArchiveDefragmentable>());
      Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }

  [TestCase("stored", 0)]
  [TestCase("lzma", 1)]
  [TestCase("lz4", 2)]
  [TestCase("lz4hc", 3)]
  public void Create_AllStorageMethods_RoundTrip(string method, int expectedFlag) {
    var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("UnityFS payload with repetition. ", 300)));
    var bundle = CreateBundle(method, [ArchiveInputInfo.InMemory("Assets/data.bin", payload)]);
    var reader = new UnityBundleReader(bundle);

    Assert.Multiple(() => {
      Assert.That(reader.TotalSize, Is.EqualTo(bundle.Length));
      Assert.That(reader.Nodes, Has.Count.EqualTo(1));
      Assert.That(reader.Blocks, Is.Not.Empty);
      Assert.That(reader.Blocks.All(b => (b.Flags & 0x3F) == expectedFlag), Is.True);
      Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(payload));
    });
  }

  [TestCase("stored", 0)]
  [TestCase("lzma", 1)]
  [TestCase("lz4", 2)]
  [TestCase("lz4hc", 3)]
  public void Create_AllBlocksInfoMethods_RoundTrip(string infoMethod, int expectedFlag) {
    var payload = Encoding.UTF8.GetBytes("blocks-info compression round-trip");
    var bundle = CreateBundle("stored", [ArchiveInputInfo.InMemory("x.txt", payload)],
      new Dictionary<string, string> { ["BlocksInfoCompression"] = infoMethod });
    var reader = new UnityBundleReader(bundle);

    Assert.Multiple(() => {
      Assert.That(reader.Flags & 0x3F, Is.EqualTo((uint)expectedFlag));
      Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(payload));
    });
  }

  [Test]
  public void Create_Auto_UsesMixedStoredAndCompressedBlocksWhenAppropriate() {
    var random = new byte[4096];
    new Random(12345).NextBytes(random);
    var repeated = Enumerable.Repeat((byte)'A', 4096).ToArray();
    var payload = random.Concat(repeated).ToArray();
    var bundle = CreateBundle("auto", [ArchiveInputInfo.InMemory("mixed.bin", payload)],
      new Dictionary<string, string> {
        ["BlockSize"] = "4096",
        ["BlocksInfoCompression"] = "stored",
      });
    var reader = new UnityBundleReader(bundle);
    var methods = reader.Blocks.Select(b => (int)(b.Flags & 0x3F)).ToArray();

    Assert.Multiple(() => {
      Assert.That(reader.Blocks, Has.Count.EqualTo(2));
      Assert.That(methods, Does.Contain(0));
      Assert.That(methods, Does.Contain(3));
      Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(payload));
    });
  }

  [Test]
  public void Create_BlockInfoAtEnd_Version7_RoundTrips() {
    var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("end-layout-", 100)));
    var bundle = CreateBundle("lz4hc", [ArchiveInputInfo.InMemory("CAB-test", payload)],
      new Dictionary<string, string> {
        ["FormatVersion"] = "7",
        ["BlocksInfoAtEnd"] = "true",
      });
    var reader = new UnityBundleReader(bundle);

    Assert.Multiple(() => {
      Assert.That(reader.FormatVersion, Is.EqualTo(7u));
      Assert.That((reader.Flags & 0x80) != 0, Is.True);
      Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(payload));
    });
  }

  [Test]
  public void Create_MultipleUnicodeNestedNodes_PreservesNamesAndBytes() {
    var first = "alpha"u8.ToArray();
    var second = Encoding.UTF8.GetBytes("βeta-data");
    var bundle = CreateBundle("lz4", [
      ArchiveInputInfo.InMemory("Assets/a.txt", first),
      ArchiveInputInfo.InMemory("StreamingAssets/日本語/β.bin", second),
    ]);
    var reader = new UnityBundleReader(bundle);

    Assert.That(reader.Nodes.Select(n => n.Path), Is.EqualTo(new[] {
      "Assets/a.txt",
      "StreamingAssets/日本語/β.bin",
    }));
    Assert.That(reader.ExtractNode(reader.Nodes[0]), Is.EqualTo(first));
    Assert.That(reader.ExtractNode(reader.Nodes[1]), Is.EqualTo(second));
  }

  [Test]
  public void RebuildBackedAddRemove_RoundTripsWithoutClaimingCanModify() {
    var descriptor = new UnityBundleFormatDescriptor();
    var initial = CreateBundle("stored", [ArchiveInputInfo.InMemory("a.txt", "first"u8.ToArray())]);
    using var archive = new MemoryStream();
    archive.Write(initial);
    archive.Position = 0;

    descriptor.Add(archive, [ArchiveInputInfo.InMemory("nested/b.txt", "second"u8.ToArray())]);
    archive.Position = 0;
    var afterAdd = descriptor.List(archive, null).Select(e => e.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    Assert.That(afterAdd, Is.EqualTo(new[] { "a.txt", "nested/b.txt" }));

    archive.Position = 0;
    descriptor.Remove(archive, ["a.txt"]);
    archive.Position = 0;
    var afterRemove = descriptor.List(archive, null);
    Assert.That(afterRemove.Select(e => e.Name), Is.EqualTo(new[] { "nested/b.txt" }));
    archive.Position = 0;
    var restored = ((IArchiveFormatOperations)descriptor).ExtractEntryToMemory(archive, "nested/b.txt", null);
    Assert.That(restored, Is.EqualTo("second"u8.ToArray()));
  }

  [Test]
  public void Create_EmptyBundle_IsValidUnityFs() {
    var bundle = CreateBundle("auto", []);
    var reader = new UnityBundleReader(bundle);
    Assert.Multiple(() => {
      Assert.That(reader.Nodes, Is.Empty);
      Assert.That(reader.Blocks, Is.Empty);
      Assert.That(reader.TotalSize, Is.EqualTo(bundle.Length));
    });
  }

  [Test]
  public void Create_IsDeterministicForSameInputsAndOptions() {
    ArchiveInputInfo[] inputs = [
      ArchiveInputInfo.InMemory("z.bin", "zzz"u8.ToArray()),
      ArchiveInputInfo.InMemory("a.bin", "aaa"u8.ToArray()),
    ];
    var one = CreateBundle("lz4hc", inputs, optimize: true);
    var two = CreateBundle("lz4hc", inputs, optimize: true);
    Assert.That(two, Is.EqualTo(one));
  }

  [Test]
  public void Reader_RejectsStoredBlockSizeMismatch() {
    var bundle = MakeMinimalBundle("x", "hello"u8.ToArray());
    var p = 8 + 4 + Encoding.UTF8.GetByteCount("5.x.x") + 1 + Encoding.UTF8.GetByteCount("2019.4.11f1") + 1;
    p += 8;
    var compressedInfoSize = BinaryPrimitives.ReadUInt32BigEndian(bundle.AsSpan(p, 4));
    Assert.That(compressedInfoSize, Is.GreaterThan(0u));
    p += 12;
    var compressedSizeField = p + 16 + 4 + 4;
    BinaryPrimitives.WriteUInt32BigEndian(bundle.AsSpan(compressedSizeField, 4), 4u);
    Assert.That(() => new UnityBundleReader(bundle).GetDataStream(), Throws.InstanceOf<InvalidDataException>());
  }

  [Test]
  public void Create_RejectsUnsafeDuplicateAndUnsupportedOptions() {
    var descriptor = new UnityBundleFormatDescriptor();

    void Write(IReadOnlyList<ArchiveInputInfo> entries, FormatCreateOptions options) {
      using var ms = new MemoryStream();
      descriptor.Create(ms, entries, options);
    }

    Assert.Multiple(() => {
      Assert.Throws<ArgumentException>(() => Write(
        [ArchiveInputInfo.InMemory("../escape.bin", [1])], new FormatCreateOptions()));
      Assert.Throws<ArgumentException>(() => Write([
        ArchiveInputInfo.InMemory("same.bin", [1]),
        ArchiveInputInfo.InMemory("/same.bin", [2]),
      ], new FormatCreateOptions()));
      Assert.Throws<NotSupportedException>(() => Write(
        [ArchiveInputInfo.InMemory("x", [1])], new FormatCreateOptions { MethodName = "brotli" }));
      Assert.Throws<NotSupportedException>(() => Write(
        [ArchiveInputInfo.InMemory("x", [1])], new FormatCreateOptions { Password = "secret" }));
    });
  }

  [Test]
  public void Reader_RejectsNonUnityData() {
    var bogus = "NotAUnityBundle\0"u8.ToArray();
    Assert.That(() => new UnityBundleReader(bogus), Throws.InstanceOf<InvalidDataException>());
  }
}
