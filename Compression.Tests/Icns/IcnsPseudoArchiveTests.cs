#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.PngCrushAdapters;

namespace Compression.Tests.Icns;

/// <summary>
/// Behaviour of <see cref="IcnsFormatDescriptor"/> as a sub-image pseudo-archive:
/// FULL.icns + metadata.ini + one entry per icon element. Uses a synthetic ICNS
/// with one PNG-payload element and one raw element.
/// </summary>
[TestFixture]
public class IcnsPseudoArchiveTests {

  private static readonly byte[] PngStub = [
    0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF,
  ];
  private static readonly byte[] RawStub = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

  private static byte[] BuildIcns() {
    // 'icns' + total length, then [OSType][len][data] elements.
    var el0Len = 8 + PngStub.Length; // ic07 (PNG)
    var el1Len = 8 + RawStub.Length; // is32 (raw)
    var total = 8 + el0Len + el1Len;

    var buf = new byte[total];
    Encoding.ASCII.GetBytes("icns").CopyTo(buf, 0);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), (uint)total);

    var p = 8;
    WriteElement(buf, ref p, "ic07", PngStub);
    WriteElement(buf, ref p, "is32", RawStub);
    return buf;
  }

  private static void WriteElement(byte[] buf, ref int p, string osType, byte[] data) {
    Encoding.ASCII.GetBytes(osType).CopyTo(buf, p);
    BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(p + 4), (uint)(8 + data.Length));
    data.CopyTo(buf, p + 8);
    p += 8 + data.Length;
  }

  [Test]
  public void List_Exposes_Full_Metadata_And_IconElements() {
    var desc = new IcnsFormatDescriptor();
    using var s = new MemoryStream(BuildIcns());
    var entries = desc.List(s, null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.Multiple(() => {
      Assert.That(names, Does.Contain("FULL.icns"));
      Assert.That(names, Does.Contain("metadata.ini"));
      Assert.That(names, Does.Contain("icons/ic07.png"));   // PNG payload → .png
      Assert.That(names, Does.Contain("icons/is32.bin"));   // raw payload → .bin
    });
    Assert.That(entries.First(e => e.Name == "icons/ic07.png").Kind, Is.EqualTo("Frame"));
    Assert.That(entries.First(e => e.Name == "icons/is32.bin").Kind, Is.EqualTo("Sample"));
  }

  [Test]
  public void Extract_Full_ByteIdentical_And_Elements_Carry_Payloads() {
    var original = BuildIcns();
    var desc = new IcnsFormatDescriptor();
    using var s = new MemoryStream(original);
    var outDir = Path.Combine(Path.GetTempPath(), $"cwb_icns_{Guid.NewGuid():N}");
    try {
      desc.Extract(s, outDir, null, null);
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "FULL.icns")), Is.EqualTo(original));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "icons", "ic07.png")), Is.EqualTo(PngStub));
      Assert.That(File.ReadAllBytes(Path.Combine(outDir, "icons", "is32.bin")), Is.EqualTo(RawStub));
    } finally {
      try { Directory.Delete(outDir, true); } catch { /* best effort */ }
    }
  }

  [Test]
  public void List_DoesNotThrow_On_Malformed() {
    var desc = new IcnsFormatDescriptor();
    using var s = new MemoryStream([(byte)'i', (byte)'c', (byte)'n', (byte)'s', 0x00, 0x00, 0xFF, 0xFF]);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = desc.List(s, null));
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.icns"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
