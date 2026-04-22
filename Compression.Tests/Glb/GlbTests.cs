#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Glb;

namespace Compression.Tests.Glb;

[TestFixture]
public class GlbTests {

  private static byte[] MakeMinimalGlb() {
    // Minimal GLB with a simple JSON chunk + a tiny BIN chunk.
    var json = "{\"asset\":{\"version\":\"2.0\"}}";
    var jsonBytes = Encoding.UTF8.GetBytes(json);
    // JSON is 4-byte aligned; pad with spaces.
    while (jsonBytes.Length % 4 != 0) {
      Array.Resize(ref jsonBytes, jsonBytes.Length + 1);
      jsonBytes[^1] = 0x20;
    }

    var binBody = new byte[12];
    while (binBody.Length % 4 != 0) Array.Resize(ref binBody, binBody.Length + 1);

    var totalLen = 12 + 8 + jsonBytes.Length + 8 + binBody.Length;
    using var ms = new MemoryStream();
    ms.Write("glTF"u8);
    Span<byte> u4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(u4, 2); ms.Write(u4);                           // version
    BinaryPrimitives.WriteUInt32LittleEndian(u4, (uint)totalLen); ms.Write(u4);              // length
    // JSON chunk
    BinaryPrimitives.WriteUInt32LittleEndian(u4, (uint)jsonBytes.Length); ms.Write(u4);
    ms.Write("JSON"u8);
    ms.Write(jsonBytes);
    // BIN chunk
    BinaryPrimitives.WriteUInt32LittleEndian(u4, (uint)binBody.Length); ms.Write(u4);
    ms.Write([(byte)'B', (byte)'I', (byte)'N', 0]);
    ms.Write(binBody);
    return ms.ToArray();
  }

  [Test]
  public void MinimalGlb_SurfacesJsonAndBin() {
    var data = MakeMinimalGlb();
    using var ms = new MemoryStream(data);
    var entries = new GlbFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.glb"), Is.True);
    Assert.That(entries.Any(e => e.Name == "scene.gltf"), Is.True);
    Assert.That(entries.Any(e => e.Name == "binary.bin"), Is.True);
  }

  [Test]
  public void ExtractedSceneIsParsableJson() {
    var data = MakeMinimalGlb();
    using var ms = new MemoryStream(data);
    using var output = new MemoryStream();
    new GlbFormatDescriptor().ExtractEntry(ms, "scene.gltf", output, null);
    var text = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(text, Does.Contain("\"version\":\"2.0\""));
  }
}
