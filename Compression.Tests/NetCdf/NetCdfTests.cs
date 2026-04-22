using System.Buffers.Binary;
using System.Text;
using FileFormat.NetCdf;

namespace Compression.Tests.NetCdf;

[TestFixture]
public class NetCdfTests {
  // ── Minimal CDF-1 builder ────────────────────────────────────────────────

  private static void WriteI32(Stream s, int v) {
    Span<byte> buf = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buf, v);
    s.Write(buf);
  }

  private static void WritePaddedString(Stream s, string str) {
    var bytes = Encoding.ASCII.GetBytes(str);
    WriteI32(s, bytes.Length);
    s.Write(bytes);
    var pad = (4 - bytes.Length % 4) % 4;
    for (var i = 0; i < pad; i++) s.WriteByte(0);
  }

  private static byte[] BuildMinimalCdf1() {
    // CDF-1 header: one dimension "x" of length 4, one NC_INT variable "v" of shape [x].
    var ms = new MemoryStream();
    ms.Write([(byte)'C', (byte)'D', (byte)'F', 0x01]);
    WriteI32(ms, 0); // numrecs = 0

    // dim_list: NC_DIMENSION, 1, "x", 4
    WriteI32(ms, 0x0A);      // NC_DIMENSION
    WriteI32(ms, 1);          // one dim
    WritePaddedString(ms, "x");
    WriteI32(ms, 4);

    // gatt_list: empty
    WriteI32(ms, 0);
    WriteI32(ms, 0);

    // var_list: NC_VARIABLE, 1
    WriteI32(ms, 0x0B);
    WriteI32(ms, 1);
    // name
    WritePaddedString(ms, "v");
    // dimrank + dim id
    WriteI32(ms, 1);
    WriteI32(ms, 0);
    // vatt_list: empty
    WriteI32(ms, 0);
    WriteI32(ms, 0);
    // nc_type = NC_INT (4)
    WriteI32(ms, 4);
    // vsize (4 ints = 16 bytes)
    WriteI32(ms, 16);
    // begin (for CDF-1: int32 offset). We'll put the data right after the header.
    var beginPosition = ms.Position;
    WriteI32(ms, 0); // placeholder

    // Now we know the header length; record the data offset and write payload.
    var dataOffset = (int)ms.Position;
    // patch begin
    var current = ms.Position;
    ms.Position = beginPosition;
    WriteI32(ms, dataOffset);
    ms.Position = current;

    // 16 bytes of data: 0x00 0x00 0x00 0x01, 0x00 0x00 0x00 0x02, ...
    for (var i = 1; i <= 4; i++)
      WriteI32(ms, i);

    return ms.ToArray();
  }

  // ── Descriptor metadata ──────────────────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void Descriptor_ReportsNetCdfExtensions() {
    var d = new NetCdfFormatDescriptor();
    Assert.That(d.DefaultExtension, Is.EqualTo(".nc"));
    Assert.That(d.Extensions, Does.Contain(".nc"));
  }

  [Category("HappyPath")]
  [Test]
  public void Descriptor_HasThreeMagicSignatures() {
    var d = new NetCdfFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
    foreach (var sig in d.MagicSignatures) {
      Assert.That(sig.Bytes[0], Is.EqualTo((byte)'C'));
      Assert.That(sig.Bytes[1], Is.EqualTo((byte)'D'));
      Assert.That(sig.Bytes[2], Is.EqualTo((byte)'F'));
    }
  }

  // ── List / Extract on synthetic CDF-1 ────────────────────────────────────

  [Category("HappyPath")]
  [Test]
  public void List_MinimalCdf1_ReturnsVariableAndMetadata() {
    var blob = BuildMinimalCdf1();
    using var ms = new MemoryStream(blob);
    var d = new NetCdfFormatDescriptor();
    var entries = d.List(ms, null);

    var names = entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("FULL.nc"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("vars/v.bin"));
  }

  [Category("HappyPath")]
  [Test]
  public void Extract_MinimalCdf1_WritesVariableBytes() {
    var blob = BuildMinimalCdf1();
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      using var ms = new MemoryStream(blob);
      var d = new NetCdfFormatDescriptor();
      d.Extract(ms, tmp, null, null);

      var metaText = File.ReadAllText(Path.Combine(tmp, "metadata.ini"));
      Assert.That(metaText, Does.Contain("parse_status=ok"));
      Assert.That(metaText, Does.Contain("dim_count=1"));
      Assert.That(metaText, Does.Contain("var_count=1"));

      var varBytes = File.ReadAllBytes(Path.Combine(tmp, "vars", "v.bin"));
      Assert.That(varBytes, Has.Length.EqualTo(16));
      // First int32 (big-endian) should be 1
      Assert.That(varBytes[3], Is.EqualTo(1));
      // Fourth int32 should be 4
      Assert.That(varBytes[15], Is.EqualTo(4));
    } finally {
      Directory.Delete(tmp, true);
    }
  }

  // ── Robustness ───────────────────────────────────────────────────────────

  [Category("Robustness")]
  [Test]
  public void List_CorruptedHeader_DoesNotThrow() {
    var blob = new byte[] {
      (byte)'C', (byte)'D', (byte)'F', 0x01,
      0xFF, 0xFF, 0xFF, 0xFF, // numrecs = STREAMING
      0xAA, 0xBB, 0xCC, 0xDD, // garbage
    };
    using var ms = new MemoryStream(blob);
    var d = new NetCdfFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("FULL.nc"));
    Assert.That(entries.Select(e => e.Name), Does.Contain("metadata.ini"));
  }
}
