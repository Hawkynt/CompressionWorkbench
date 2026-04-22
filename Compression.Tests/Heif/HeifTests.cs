#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Heif;

namespace Compression.Tests.Heif;

[TestFixture]
public class HeifTests {

  // Build a minimal HEIC fixture: ftyp + meta (with hdlr/pitm/iinf/iloc) + mdat.
  // One hvc1 item (id=1) + one Exif item (id=2). Construction method 0, extents
  // point into mdat.
  private static byte[] MakeMinimalHeic() {
    // --- Payloads to stash in mdat ---
    var hvcPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
    var exifPayload = new byte[] { 0x00, 0x00, 0x00, 0x00, // 4-byte TIFF header offset
                                   (byte)'I', (byte)'I', 0x2A, 0x00 };
    var mdatBody = new List<byte>();
    var hvcOff = 0; // relative to start of mdat body
    mdatBody.AddRange(hvcPayload);
    var exifOff = mdatBody.Count;
    mdatBody.AddRange(exifPayload);

    // We'll emit: ftyp (24 bytes), meta (variable), mdat.
    // Meta = FullBox header (4) + hdlr + pitm + iinf + iloc.
    var hdlr = BuildBox("hdlr", FullBoxBody([
      // version+flags
      0, 0, 0, 0,
      0, 0, 0, 0,        // pre-defined
      (byte)'p', (byte)'i', (byte)'c', (byte)'t', // handler_type
      0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,          // reserved[3]
      0,                  // name: empty cstring
    ]));
    var pitm = BuildBox("pitm", FullBoxBody([
      0, 0, 0, 0,
      0, 1, // primary item_id = 1
    ]));

    // iinf with entry_count=2, children are infe boxes (v2).
    var infe1 = BuildBox("infe", FullBoxBody([
      2, 0, 0, 0,            // version=2, flags=0
      0, 1,                   // item_id=1
      0, 0,                   // protection_index
      (byte)'h', (byte)'v', (byte)'c', (byte)'1',
      0,                      // item_name: empty cstring
    ], version: 2));
    var infe2 = BuildBox("infe", FullBoxBody([
      2, 0, 0, 0,
      0, 2,
      0, 0,
      (byte)'E', (byte)'x', (byte)'i', (byte)'f',
      0,
    ], version: 2));

    var iinfBody = new List<byte> { 0, 0, 0, 0, 0, 2 }; // version=0, flags, count=2
    iinfBody.AddRange(infe1);
    iinfBody.AddRange(infe2);
    var iinf = BuildBox("iinf", iinfBody.ToArray());

    // iloc v1: offset_size=4, length_size=4, base_offset_size=0, index_size=0.
    // We fill extents with placeholders and patch real offsets once we know mdat position.
    // Per-item record (v1): item_id(2) + reserved_construction(2) + dataref(2) + base(0) + ext_count(2) + (offset(4)+length(4)).
    var ilocBody = new List<byte> {
      1, 0, 0, 0,     // version=1, flags
      0x44,           // offset_size=4, length_size=4
      0x00,           // base_offset_size=0, index_size=0
      0, 2,           // item_count=2
    };
    // Item 1 (hvc1)
    ilocBody.AddRange([0, 1]);       // id
    ilocBody.AddRange([0, 0]);       // construction_method = 0 (file offset)
    ilocBody.AddRange([0, 0]);       // data_reference_index
    // base_offset_size=0 → no bytes
    ilocBody.AddRange([0, 1]);       // extent_count=1
    // extent offset placeholder (4 bytes) + length (4 bytes)
    var hvcOffPos = ilocBody.Count; ilocBody.AddRange([0, 0, 0, 0]);
    AppendUInt32BE(ilocBody, (uint)hvcPayload.Length);
    // Item 2 (Exif)
    ilocBody.AddRange([0, 2]);
    ilocBody.AddRange([0, 0]);
    ilocBody.AddRange([0, 0]);
    ilocBody.AddRange([0, 1]);
    var exifOffPos = ilocBody.Count; ilocBody.AddRange([0, 0, 0, 0]);
    AppendUInt32BE(ilocBody, (uint)exifPayload.Length);
    var iloc = BuildBox("iloc", ilocBody.ToArray());

    // meta = FullBox (4 header bytes) + children
    var metaBody = new List<byte> { 0, 0, 0, 0 };
    metaBody.AddRange(hdlr);
    metaBody.AddRange(pitm);
    metaBody.AddRange(iinf);
    metaBody.AddRange(iloc);
    var meta = BuildBox("meta", metaBody.ToArray());

    // ftyp: major brand "heic", minor 0, compatible "heic" "mif1"
    var ftypBody = new List<byte>();
    ftypBody.AddRange("heic"u8.ToArray());
    ftypBody.AddRange([0, 0, 0, 0]);
    ftypBody.AddRange("heic"u8.ToArray());
    ftypBody.AddRange("mif1"u8.ToArray());
    var ftyp = BuildBox("ftyp", ftypBody.ToArray());

    // mdat
    var mdat = BuildBox("mdat", mdatBody.ToArray());

    // Assemble final buffer.
    var file = new List<byte>();
    file.AddRange(ftyp);
    var metaStart = file.Count;
    file.AddRange(meta);
    var mdatStart = file.Count;
    file.AddRange(mdat);

    // mdat body starts at mdatStart + 8 (box header).
    var mdatBodyStart = (uint)(mdatStart + 8);
    // Patch offsets in iloc — these live inside the meta box.
    // meta body starts at metaStart + 8; then 4 FullBox bytes; children begin at metaStart+12.
    // iloc is the last child; we patched its body-relative positions. Locate iloc in file.
    var ilocAbs = FindBoxAbs(file, "iloc", metaStart);
    var arr = file.ToArray();
    // Body of iloc starts at ilocAbs + 8.
    var ilocBodyAbs = ilocAbs + 8;
    WriteUInt32BE(arr, ilocBodyAbs + hvcOffPos, mdatBodyStart + (uint)hvcOff);
    WriteUInt32BE(arr, ilocBodyAbs + exifOffPos, mdatBodyStart + (uint)exifOff);
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

  private static byte[] FullBoxBody(byte[] body, byte version = 0) {
    // FullBox header is 4 bytes (version + 3 flags). Passed body already carries them
    // for iinf/iloc; for infe/hdlr/pitm the caller supplies it explicitly. Returned
    // as-is because all BuildBox callers already include the 4 FullBox bytes.
    return body;
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
    for (var i = searchStart; i + 8 <= buf.Count; i++) {
      if (buf[i + 4] == t[0] && buf[i + 5] == t[1] && buf[i + 6] == t[2] && buf[i + 7] == t[3])
        return i;
    }
    return -1;
  }

  [Test]
  public void ReaderFindsPrimaryAndItems() {
    var data = MakeMinimalHeic();
    var reader = new HeifReader(data);
    Assert.Multiple(() => {
      Assert.That(reader.MajorBrand, Is.EqualTo("heic"));
      Assert.That(reader.PrimaryItemId, Is.EqualTo(1u));
      Assert.That(reader.Items, Has.Count.EqualTo(2));
      Assert.That(reader.Items[0].Type, Is.EqualTo("hvc1"));
      Assert.That(reader.Items[1].Type, Is.EqualTo("Exif"));
    });
    var hvc = reader.ReadItem(1);
    Assert.That(hvc, Has.Length.EqualTo(8));
    Assert.That(hvc[0], Is.EqualTo(0x01));
  }

  [Test]
  public void Descriptor_List_HasFullAndItemEntries() {
    var data = MakeMinimalHeic();
    using var ms = new MemoryStream(data);
    var entries = new HeifFormatDescriptor().List(ms, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "FULL.heic"), Is.True);
      Assert.That(entries.Any(e => e.Name.StartsWith("primary_item_001_hvc1") && e.Name.EndsWith(".hevc")), Is.True);
      Assert.That(entries.Any(e => e.Name == "metadata/exif.bin"), Is.True);
    });
  }

  [Test]
  public void Descriptor_Extract_WritesAllFiles() {
    var data = MakeMinimalHeic();
    var dir = Path.Combine(Path.GetTempPath(), "heif_test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(data);
      new HeifFormatDescriptor().Extract(ms, dir, null, null);
      Assert.Multiple(() => {
        Assert.That(File.Exists(Path.Combine(dir, "FULL.heic")), Is.True);
        Assert.That(File.Exists(Path.Combine(dir, "metadata", "exif.bin")), Is.True);
        Assert.That(Directory.GetFiles(dir, "primary_item_001_hvc1.hevc").Length, Is.EqualTo(1));
      });
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test]
  public void BadBrand_Throws() {
    var data = MakeMinimalHeic();
    // Corrupt the major brand to something the descriptor rejects.
    data[8] = (byte)'m'; data[9] = (byte)'p'; data[10] = (byte)'4'; data[11] = (byte)'2';
    data[16] = (byte)'m'; data[17] = (byte)'p'; data[18] = (byte)'4'; data[19] = (byte)'2';
    data[20] = (byte)'m'; data[21] = (byte)'p'; data[22] = (byte)'4'; data[23] = (byte)'2';
    using var ms = new MemoryStream(data);
    Assert.Throws<InvalidDataException>(() => new HeifFormatDescriptor().List(ms, null));
  }
}
