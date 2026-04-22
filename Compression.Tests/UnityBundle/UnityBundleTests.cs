#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.UnityBundle;

namespace Compression.Tests.UnityBundle;

[TestFixture]
public class UnityBundleTests {

  // Build a minimal UnityFS bundle with a single stored block and a single node pointing to it.
  // Header layout (all multi-byte fields big-endian):
  //   "UnityFS\0" + formatVersion(uint32) + unityVersion(cstring) + unityRevision(cstring)
  //   + totalSize(int64) + compressedBlocksInfoSize(uint32) + uncompressedBlocksInfoSize(uint32)
  //   + flags(uint32)
  // Followed by the (stored) BlocksInfo then the (stored) payload.
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

    // Signature
    header.Write("UnityFS\0"u8);
    // format version 6, Unity version strings
    WriteU32BE(6u);
    WriteCStr("5.x.x");
    WriteCStr("2019.4.11f1");

    // Build BlocksInfo (stored, no compression).
    using var biMs = new MemoryStream();
    biMs.Write(new byte[16]); // 16-byte hash
    void BiU32BE(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); biMs.Write(b); }
    void BiI32BE(int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); biMs.Write(b); }
    void BiI64BE(long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); biMs.Write(b); }
    void BiU16BE(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); biMs.Write(b); }
    void BiCStr(string s) { biMs.Write(Encoding.UTF8.GetBytes(s)); biMs.WriteByte(0); }

    // 1 block: uncompressedSize=payload.Length, compressedSize=payload.Length, flags=0 (stored)
    BiI32BE(1);
    BiU32BE((uint)payload.Length);
    BiU32BE((uint)payload.Length);
    BiU16BE(0);

    // 1 node: offset=0, size=payload.Length, flags=0, path
    BiI32BE(1);
    BiI64BE(0);
    BiI64BE(payload.Length);
    BiU32BE(0);
    BiCStr(nodeName);

    var blocksInfo = biMs.ToArray();
    // totalSize is header + blocksInfo + payload — but we just write the file; totalSize is informational.
    // Flags = 0 → BlocksInfo is NOT at end (immediately after header), compression type 0 (stored).
    WriteI64BE(0); // totalSize (unchecked)
    WriteU32BE((uint)blocksInfo.Length); // compressedBlocksInfoSize
    WriteU32BE((uint)blocksInfo.Length); // uncompressedBlocksInfoSize
    WriteU32BE(0); // flags (stored, inline)

    header.Write(blocksInfo);
    header.Write(payload);
    return header.ToArray();
  }

  [Test]
  public void Reader_ParsesHeader_AndSurfacesSingleNode() {
    var payload = Encoding.UTF8.GetBytes("Hello, Unity!");
    var bundle = MakeMinimalBundle("Assets/hello.txt", payload);

    var reader = new UnityBundleReader(bundle);

    Assert.That(reader.Signature, Is.EqualTo("UnityFS"));
    Assert.That(reader.FormatVersion, Is.EqualTo(6u));
    Assert.That(reader.UnityVersion, Is.EqualTo("5.x.x"));
    Assert.That(reader.UnityRevision, Is.EqualTo("2019.4.11f1"));
    Assert.That(reader.Blocks, Has.Count.EqualTo(1));
    Assert.That(reader.Nodes, Has.Count.EqualTo(1));
    Assert.That(reader.Nodes[0].Path, Is.EqualTo("Assets/hello.txt"));
    Assert.That(reader.Nodes[0].Size, Is.EqualTo(payload.Length));
  }

  [Test]
  public void Reader_ExtractsStoredNodePayload() {
    var payload = Encoding.UTF8.GetBytes("Hello, Unity!");
    var bundle = MakeMinimalBundle("Assets/hello.txt", payload);

    var reader = new UnityBundleReader(bundle);
    var extracted = reader.ExtractNode(reader.Nodes[0]);

    Assert.That(extracted, Is.EqualTo(payload));
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
      var extracted = File.ReadAllBytes(Path.Combine(tmpDir, "dir", "inner", "file.bin"));
      Assert.That(extracted, Is.EqualTo(payload));
    } finally {
      if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test]
  public void Reader_RejectsNonUnityData() {
    var bogus = "NotAUnityBundle\0"u8.ToArray();
    Assert.That(() => new UnityBundleReader(bogus),
      Throws.InstanceOf<InvalidDataException>());
  }
}
