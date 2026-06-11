using System.Buffers.Binary;
using System.Text;
using FileFormat.Ffu;

namespace Compression.Tests.Ffu;

[TestFixture]
public class FfuTests {

  // Build a minimal FFU with a tiny chunk size so the synthetic sample stays small.
  // Security Header: signature "SignedImage\0" + chunkSizeInKb + algId + catalogSize
  // + hashTableSize, padded (with catalog + hash bytes) up to a chunk boundary, then
  // an Image Header "ImageFlash  " + manifestLength + chunkSize + manifest text, then
  // a chunk-aligned payload region.
  private static byte[] BuildSyntheticFfu() {
    const uint chunkKib = 1;          // 1 KiB chunk for a compact sample
    const int chunkBytes = (int)chunkKib * 1024;
    const uint catalogSize = 8;
    const uint hashSize = 8;

    using var ms = new MemoryStream();

    // --- Security header ---
    var sig = "SignedImage\0"u8.ToArray();
    ms.Write(sig);                                       // 12
    WriteU32(ms, chunkKib);                              // chunkSizeInKb
    WriteU32(ms, 0);                                     // algId
    WriteU32(ms, catalogSize);                           // catalogSize
    WriteU32(ms, hashSize);                              // hashTableSize
    // fixedLen = 12 + 16 = 28
    ms.Write(new byte[catalogSize]);                     // catalog
    ms.Write(new byte[hashSize]);                        // hash table
    // Pad security header up to the chunk boundary.
    PadToChunk(ms, chunkBytes);

    // --- Image header ---
    var imgSig = "ImageFlash  "u8.ToArray();
    var manifest = Encoding.ASCII.GetBytes("[Manifest]\nDevice=CWBTest\n");
    ms.Write(imgSig);                                    // 12
    WriteU32(ms, (uint)manifest.Length);                 // manifest length
    WriteU32(ms, chunkBytes);                            // chunk size (bytes)
    ms.Write(manifest);                                  // manifest body
    // Pad up to the chunk boundary -> payload begins here.
    PadToChunk(ms, chunkBytes);

    // --- Payload (one chunk of data) ---
    var payload = new byte[chunkBytes];
    Array.Fill(payload, (byte)0xEE);
    ms.Write(payload);

    return ms.ToArray();
  }

  private static void WriteU32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    s.Write(b);
  }

  private static void PadToChunk(MemoryStream ms, int chunkBytes) {
    var rem = (int)(ms.Length % chunkBytes);
    if (rem != 0) ms.Write(new byte[chunkBytes - rem]);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FfuFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Ffu"));
    Assert.That(d.Extensions, Contains.Item(".ffu"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndPayload() {
    var img = BuildSyntheticFfu();
    var d = new FfuFormatDescriptor();
    using var ms = new MemoryStream(img);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.ffu"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "payload.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdenticalAndParsesHeaders() {
    var img = BuildSyntheticFfu();
    var d = new FfuFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ffu_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(img);
      d.Extract(ms, dir, null, null);

      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ffu"));
      Assert.That(full, Is.EqualTo(img));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("valid=1"));
      Assert.That(meta, Does.Contain("chunk_size_kib=1"));
      Assert.That(meta, Does.Contain("image_header_found=1"));
      Assert.That(meta, Does.Contain("chunk_reconstruction=deferred"));
      Assert.That(meta, Does.Contain("parse_status=ok"));

      var payload = File.ReadAllBytes(Path.Combine(dir, "payload.bin"));
      Assert.That(payload.Length, Is.EqualTo(1024));
      Assert.That(payload[0], Is.EqualTo(0xEE));
      Assert.That(payload[^1], Is.EqualTo(0xEE));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[128];
    Array.Fill(garbage, (byte)0x44);
    var d = new FfuFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "ffu_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.ffu"));
      Assert.That(full, Is.EqualTo(garbage));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
