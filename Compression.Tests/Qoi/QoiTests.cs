using System.Text;
using Compression.Registry;
using FileFormat.Qoi;

namespace Compression.Tests.Qoi;

[TestFixture]
public class QoiTests {

  // Best-effort temp-dir cleanup: a cleanup failure (a transient handle held by an
  // AV/indexer on Windows under combined-run disk pressure) must never fail the test
  // whose assertions already passed.
  private static void SafeDelete(string dir) {
    try { Directory.Delete(dir, recursive: true); }
    catch (IOException) { /* transient handle — leave for the OS temp sweep */ }
    catch (UnauthorizedAccessException) { /* same */ }
  }

  // Build a 4x4 RGBA test pattern exercising runs, diffs, index hits and full RGBA.
  private static byte[] BuildRgba(int w, int h) {
    var px = new byte[w * h * 4];
    for (var i = 0; i < w * h; ++i) {
      var o = i * 4;
      px[o] = (byte)((i * 17) & 0xFF);
      px[o + 1] = (byte)((i * 3) & 0xFF);
      px[o + 2] = (byte)((i * 29) & 0xFF);
      px[o + 3] = (byte)(i % 2 == 0 ? 255 : 200);
    }
    // Inject a run of identical pixels when the image is large enough.
    for (var i = 4; i < 8 && i < w * h; ++i) {
      var o = i * 4;
      px[o] = 10; px[o + 1] = 20; px[o + 2] = 30; px[o + 3] = 255;
    }
    return px;
  }

  // Encode RGBA into QOI via the descriptor's Create, returning the .qoi bytes.
  private static byte[] EncodeViaCreate(byte[] rgba, int w, int h, int channels) {
    var d = new QoiFormatDescriptor();
    var meta = $"[Qoi]\nwidth={w}\nheight={h}\nchannels={channels}\ncolorspace=0\n";
    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("pixels.bin", rgba),
      ArchiveInputInfo.InMemory("metadata.ini", Encoding.UTF8.GetBytes(meta)),
    };
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new QoiFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Qoi"));
    Assert.That(d.Extensions, Contains.Item(".qoi"));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void RoundTrip_EncodeThenDecode_PixelsIdentical() {
    const int w = 4, h = 4;
    var rgba = BuildRgba(w, h);
    var qoi = EncodeViaCreate(rgba, w, h, 4);

    // Valid QOI header + end marker.
    Assert.That(Encoding.ASCII.GetString(qoi, 0, 4), Is.EqualTo("qoif"));
    Assert.That(qoi[^1], Is.EqualTo(0x01));

    var d = new QoiFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "qoi_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(qoi);
      d.Extract(ms, dir, null, null);
      var decoded = File.ReadAllBytes(Path.Combine(dir, "pixels.bin"));
      Assert.That(decoded, Is.EqualTo(rgba));

      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("width=4"));
      Assert.That(meta, Does.Contain("height=4"));
      Assert.That(meta, Does.Contain("channels=4"));
      Assert.That(meta, Does.Contain("parse_status=ok"));
    } finally {
      SafeDelete(dir);
    }
  }

  [Test, Category("HappyPath")]
  public void List_ExposesFullMetadataAndPixels() {
    var rgba = BuildRgba(2, 2);
    var qoi = EncodeViaCreate(rgba, 2, 2, 4);
    var d = new QoiFormatDescriptor();
    using var ms = new MemoryStream(qoi);
    var entries = d.List(ms, null);
    Assert.That(entries[0].Name, Is.EqualTo("FULL.qoi"));
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "pixels.bin"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_FullByteIdentical() {
    var rgba = BuildRgba(3, 3);
    var qoi = EncodeViaCreate(rgba, 3, 3, 4);
    var d = new QoiFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "qoi_full_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(qoi);
      d.Extract(ms, dir, null, null);
      var full = File.ReadAllBytes(Path.Combine(dir, "FULL.qoi"));
      Assert.That(full, Is.EqualTo(qoi));
    } finally {
      SafeDelete(dir);
    }
  }

  [Test, Category("Boundary")]
  public void Encode_Deterministic() {
    var rgba = BuildRgba(4, 4);
    var a = EncodeViaCreate(rgba, 4, 4, 4);
    var b = EncodeViaCreate(rgba, 4, 4, 4);
    Assert.That(a, Is.EqualTo(b));
  }

  [Test, Category("Exceptional")]
  public void Malformed_DoesNotThrow() {
    var garbage = new byte[16];
    Array.Fill(garbage, (byte)0x77);
    var d = new QoiFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "qoi_bad_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(garbage);
      Assert.DoesNotThrow(() => d.List(ms, null));
      ms.Position = 0;
      Assert.DoesNotThrow(() => d.Extract(ms, dir, null, null));
      var meta = File.ReadAllText(Path.Combine(dir, "metadata.ini"));
      Assert.That(meta, Does.Contain("parse_status=partial"));
    } finally {
      SafeDelete(dir);
    }
  }
}
