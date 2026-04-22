#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Avif;
using FileFormat.Heif;

namespace Compression.Tests.Avif;

[TestFixture]
public class AvifTests {

  // Minimal AVIF: ftyp (avif) + meta (hdlr/pitm/iinf with one av01 item/iloc) + mdat.
  private static byte[] MakeMinimalAvif() {
    var av1Payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };

    var hdlr = BuildBox("hdlr", [
      0, 0, 0, 0, 0, 0, 0, 0,
      (byte)'p', (byte)'i', (byte)'c', (byte)'t',
      0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ]);
    var pitm = BuildBox("pitm", [0, 0, 0, 0, 0, 1]);
    var infe = BuildBox("infe", [
      2, 0, 0, 0,
      0, 1,
      0, 0,
      (byte)'a', (byte)'v', (byte)'0', (byte)'1',
      0,
    ]);
    var iinfBody = new List<byte> { 0, 0, 0, 0, 0, 1 };
    iinfBody.AddRange(infe);
    var iinf = BuildBox("iinf", iinfBody.ToArray());

    var ilocBody = new List<byte> {
      1, 0, 0, 0,
      0x44,
      0x00,
      0, 1,
      0, 1,
      0, 0,
      0, 0,
      0, 1,
    };
    var offPos = ilocBody.Count; ilocBody.AddRange([0, 0, 0, 0]);
    AppendUInt32BE(ilocBody, (uint)av1Payload.Length);
    var iloc = BuildBox("iloc", ilocBody.ToArray());

    var metaBody = new List<byte> { 0, 0, 0, 0 };
    metaBody.AddRange(hdlr);
    metaBody.AddRange(pitm);
    metaBody.AddRange(iinf);
    metaBody.AddRange(iloc);
    var meta = BuildBox("meta", metaBody.ToArray());

    var ftypBody = new List<byte>();
    ftypBody.AddRange("avif"u8.ToArray());
    ftypBody.AddRange([0, 0, 0, 0]);
    ftypBody.AddRange("avif"u8.ToArray());
    ftypBody.AddRange("mif1"u8.ToArray());
    var ftyp = BuildBox("ftyp", ftypBody.ToArray());

    var mdat = BuildBox("mdat", av1Payload);

    var file = new List<byte>();
    file.AddRange(ftyp);
    var metaStart = file.Count;
    file.AddRange(meta);
    var mdatStart = file.Count;
    file.AddRange(mdat);

    var mdatBodyStart = (uint)(mdatStart + 8);
    var ilocAbs = FindBoxAbs(file, "iloc", metaStart);
    var arr = file.ToArray();
    WriteUInt32BE(arr, ilocAbs + 8 + offPos, mdatBodyStart);
    return arr;
  }

  private static byte[] BuildBox(string type, byte[] body) {
    var size = 8 + body.Length;
    var r = new byte[size];
    BinaryPrimitives.WriteUInt32BigEndian(r.AsSpan(0), (uint)size);
    Encoding.ASCII.GetBytes(type).CopyTo(r, 4);
    body.CopyTo(r, 8);
    return r;
  }

  private static void AppendUInt32BE(List<byte> dst, uint v) {
    dst.Add((byte)(v >> 24));
    dst.Add((byte)(v >> 16));
    dst.Add((byte)(v >> 8));
    dst.Add((byte)v);
  }

  private static void WriteUInt32BE(byte[] dst, int pos, uint v) =>
    BinaryPrimitives.WriteUInt32BigEndian(dst.AsSpan(pos), v);

  private static int FindBoxAbs(List<byte> buf, string type, int searchStart) {
    var t = Encoding.ASCII.GetBytes(type);
    for (var i = searchStart; i + 8 <= buf.Count; i++)
      if (buf[i + 4] == t[0] && buf[i + 5] == t[1] && buf[i + 6] == t[2] && buf[i + 7] == t[3])
        return i;
    return -1;
  }

  [Test]
  public void ReaderIdentifiesAvifBrand() {
    var data = MakeMinimalAvif();
    var reader = new HeifReader(data);
    Assert.Multiple(() => {
      Assert.That(reader.MajorBrand, Is.EqualTo("avif"));
      Assert.That(reader.MatchesAnyBrand(HeifReader.AvifBrands), Is.True);
      Assert.That(reader.Items, Has.Count.EqualTo(1));
      Assert.That(reader.Items[0].Type, Is.EqualTo("av01"));
    });
  }

  [Test]
  public void Descriptor_List_HasFullAndAv1Entry() {
    var data = MakeMinimalAvif();
    using var ms = new MemoryStream(data);
    var entries = new AvifFormatDescriptor().List(ms, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.avif"), Is.True);
      Assert.That(entries.Any(e => e.Name.EndsWith(".av1")), Is.True);
    });
  }

  [Test]
  public void Descriptor_Extract_WritesFiles() {
    var data = MakeMinimalAvif();
    var dir = Path.Combine(Path.GetTempPath(), "avif_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(data);
      new AvifFormatDescriptor().Extract(ms, dir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(dir, "FULL.avif")), Is.True);
        Assert.That(Directory.GetFiles(dir, "*.av1").Length, Is.EqualTo(1));
      });
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void HeifBrand_Rejected() {
    var data = MakeMinimalAvif();
    // Rewrite brands to heic/mif1 — AVIF descriptor should refuse.
    data[8] = (byte)'h'; data[9] = (byte)'e'; data[10] = (byte)'i'; data[11] = (byte)'c';
    data[16] = (byte)'h'; data[17] = (byte)'e'; data[18] = (byte)'i'; data[19] = (byte)'c';
    data[20] = (byte)'m'; data[21] = (byte)'i'; data[22] = (byte)'f'; data[23] = (byte)'1';
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => new AvifFormatDescriptor().List(ms, null));
  }
}
