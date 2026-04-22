#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Psd;

namespace Compression.Tests.Psd;

[TestFixture]
public class PsdTests {

  private static byte[] MakeMinimalPsd() {
    using var ms = new MemoryStream();
    ms.Write("8BPS"u8);                                  // magic
    Write(ms, (ushort)1, bigEndian: true);               // version 1 = PSD
    ms.Write(new byte[6]);                                // reserved
    Write(ms, (ushort)3, bigEndian: true);               // 3 channels
    Write(ms, 100u, bigEndian: true);                    // height
    Write(ms, 200u, bigEndian: true);                    // width
    Write(ms, (ushort)8, bigEndian: true);               // 8-bit depth
    Write(ms, (ushort)3, bigEndian: true);               // RGB color mode

    // Color Mode Data: empty
    Write(ms, 0u, bigEndian: true);

    // Image Resources: empty (length 0)
    Write(ms, 0u, bigEndian: true);

    // Layer & Mask Info: empty
    Write(ms, 0u, bigEndian: true);

    // Image Data: compression (0 = raw) + body
    Write(ms, (ushort)0, bigEndian: true);
    ms.Write(new byte[100 * 200 * 3]);
    return ms.ToArray();
  }

  private static void Write(Stream ms, ushort v, bool bigEndian) {
    Span<byte> b = stackalloc byte[2];
    if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(b, v);
    else BinaryPrimitives.WriteUInt16LittleEndian(b, v);
    ms.Write(b);
  }

  private static void Write(Stream ms, uint v, bool bigEndian) {
    Span<byte> b = stackalloc byte[4];
    if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(b, v);
    else BinaryPrimitives.WriteUInt32LittleEndian(b, v);
    ms.Write(b);
  }

  [Test]
  public void MinimalPsd_ListsFullPlusMetadata() {
    var data = MakeMinimalPsd();
    using var ms = new MemoryStream(data);
    var entries = new PsdFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.psd"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void MetadataIniContainsHeaderFields() {
    var data = MakeMinimalPsd();
    var tmp = Path.Combine(Path.GetTempPath(), "psd_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PsdFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("width=200"));
      Assert.That(ini, Does.Contain("height=100"));
      Assert.That(ini, Does.Contain("color_mode=RGB"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }
}
