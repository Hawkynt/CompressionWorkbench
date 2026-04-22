#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.Psb;

namespace Compression.Tests.Psb;

[TestFixture]
public class PsbTests {

  /// <summary>
  /// Builds a minimal-but-valid PSB: 8BPS magic, version 2, 6 reserved bytes,
  /// 3 channels, 100×200 dimensions, 8-bit, RGB, empty color-mode/resources/layer
  /// sections, and a tiny image-data section.
  /// </summary>
  private static byte[] MakeMinimalPsb() {
    using var ms = new MemoryStream();
    ms.Write("8BPS"u8);
    Write(ms, (ushort)2, bigEndian: true);                 // version 2 = PSB
    ms.Write(new byte[6]);                                  // reserved
    Write(ms, (ushort)3, bigEndian: true);                 // 3 channels
    Write(ms, 100u, bigEndian: true);                      // height
    Write(ms, 200u, bigEndian: true);                      // width
    Write(ms, (ushort)8, bigEndian: true);                 // 8-bit depth
    Write(ms, (ushort)3, bigEndian: true);                 // RGB

    Write(ms, 0u, bigEndian: true);                        // color mode data: empty
    Write(ms, 0u, bigEndian: true);                        // image resources: empty

    // Layer & Mask Info — 64-bit zero.
    Write(ms, 0UL, bigEndian: true);

    // Image data: compression method + a few bytes of fake pixel data.
    Write(ms, (ushort)0, bigEndian: true);
    ms.Write(new byte[16]);

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

  private static void Write(Stream ms, ulong v, bool bigEndian) {
    Span<byte> b = stackalloc byte[8];
    if (bigEndian) BinaryPrimitives.WriteUInt64BigEndian(b, v);
    else BinaryPrimitives.WriteUInt64LittleEndian(b, v);
    ms.Write(b);
  }

  [Test]
  public void MinimalPsb_ListsFullPlusMetadata() {
    var data = MakeMinimalPsb();
    using var ms = new MemoryStream(data);
    var entries = new PsbFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.psb"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "image_data.bin"), Is.True);
  }

  [Test]
  public void MetadataIniContainsHeaderFields() {
    var data = MakeMinimalPsb();
    var tmp = Path.Combine(Path.GetTempPath(), "psb_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new PsbFormatDescriptor().Extract(ms, tmp, null, null);
      var ini = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(ini, Does.Contain("version=psb"));
      Assert.That(ini, Does.Contain("width=200"));
      Assert.That(ini, Does.Contain("height=100"));
      Assert.That(ini, Does.Contain("color_mode=RGB"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
    }
  }

  [Test]
  public void Version1Psd_IsIgnored() {
    // If someone passes a version=1 file to the PSB descriptor, it should not
    // emit PSB-specific entries — just FULL.psb and return early.
    using var ms = new MemoryStream();
    ms.Write("8BPS"u8);
    Write(ms, (ushort)1, bigEndian: true); // version 1 = PSD, not PSB
    ms.Write(new byte[6]);
    Write(ms, (ushort)3, bigEndian: true);
    Write(ms, 10u, bigEndian: true);
    Write(ms, 10u, bigEndian: true);
    Write(ms, (ushort)8, bigEndian: true);
    Write(ms, (ushort)3, bigEndian: true);
    Write(ms, 0u, bigEndian: true);
    Write(ms, 0u, bigEndian: true);

    ms.Position = 0;
    var entries = new PsbFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.psb"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.False,
      "PSB descriptor must not claim PSD (version 1) files.");
  }

  [Test]
  public void CorruptInput_DoesNotThrow() {
    var junk = new byte[32];
    "8BPS"u8.CopyTo(junk.AsSpan());
    junk[5] = 2; // version 2 (BE)
    using var ms = new MemoryStream(junk);
    Assert.DoesNotThrow(() => new PsbFormatDescriptor().List(ms, null));
  }
}
