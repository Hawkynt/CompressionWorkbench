using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Rpa;

namespace Compression.Tests.Rpa;

[TestFixture]
public class RpaTests {

  /// <summary>
  /// Build a minimal RPA-3.0 archive: header + data blob + zlib(pickle index).
  /// Pickle: <c>{"hello.txt": [(data_offset, data_length, b"")]}</c> with offset and length
  /// XOR'd against the header key.
  /// </summary>
  private static byte[] BuildMinimalRpa3(uint xorKey, out string expectedName, out byte[] expectedData) {
    expectedName = "hello.txt";
    expectedData = "world!"u8.ToArray();

    // Data is placed after a placeholder header we will backfill
    var archive = new MemoryStream();
    var headerPlaceholder = new byte[64];
    archive.Write(headerPlaceholder, 0, headerPlaceholder.Length);
    long dataOffset = archive.Position;
    archive.Write(expectedData);
    long indexStart = archive.Position;

    // Build a minimal pickle payload for: {"hello.txt": [(dataOffset^key, length^key, b"")]}
    var pickle = new MemoryStream();
    pickle.WriteByte(0x80); pickle.WriteByte(0x02); // PROTO 2
    pickle.WriteByte((byte)'}'); // EMPTY_DICT
    pickle.WriteByte((byte)'('); // MARK
    // Key: SHORT_BINUNICODE "hello.txt"
    pickle.WriteByte(0x8C);
    pickle.WriteByte((byte)expectedName.Length);
    pickle.Write(Encoding.UTF8.GetBytes(expectedName));
    // Value: EMPTY_LIST + APPENDS [tuple]
    pickle.WriteByte((byte)']');
    pickle.WriteByte((byte)'('); // MARK inner for APPENDS
    // One tuple: (offset^key, length^key, b"")
    pickle.WriteByte((byte)'('); // MARK for tuple
    WriteBinInt(pickle, (int)((uint)dataOffset ^ xorKey));
    WriteBinInt(pickle, (int)((uint)expectedData.Length ^ xorKey));
    pickle.WriteByte((byte)'C'); pickle.WriteByte(0); // SHORT_BINBYTES (0x43) length 0 (empty prefix)
    pickle.WriteByte((byte)'t'); // TUPLE (pop to last mark)
    pickle.WriteByte((byte)'e'); // APPENDS (pop to mark)
    pickle.WriteByte((byte)'u'); // SETITEMS (pop to mark)
    pickle.WriteByte((byte)'.'); // STOP

    // zlib-compress the pickle
    var zlibBytes = new MemoryStream();
    using (var z = new ZLibStream(zlibBytes, CompressionLevel.Fastest, leaveOpen: true))
      z.Write(pickle.ToArray());
    archive.Write(zlibBytes.ToArray());

    // Now backfill header
    var header = Encoding.ASCII.GetBytes($"RPA-3.0 {indexStart:x16} {xorKey:x8}\n");
    archive.Position = 0;
    archive.Write(header);
    // Fill rest of placeholder with spaces (RPA header line defines only what we wrote; the extra bytes
    // before data don't matter because indexStart points past all of them and data is retrieved at dataOffset)
    var pad = new byte[headerPlaceholder.Length - header.Length];
    Array.Fill(pad, (byte)' ');
    archive.Write(pad);

    return archive.ToArray();
  }

  private static void WriteBinInt(Stream s, int v) {
    s.WriteByte((byte)'J'); // BININT (4-byte signed LE)
    Span<byte> b = stackalloc byte[4];
    b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 24);
    s.Write(b);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new RpaFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Rpa"));
    Assert.That(d.Extensions, Contains.Item(".rpa"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesVersionAndIndex() {
    const uint key = 0xDEADBEEF;
    var bytes = BuildMinimalRpa3(key, out var name, out var data);
    using var ms = new MemoryStream(bytes);
    var r = new RpaReader(ms);
    Assert.That(r.Version, Is.EqualTo("RPA-3.0"));
    Assert.That(r.XorKey, Is.EqualTo(key));
    Assert.That(r.PickleParsed, Is.True);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Path, Is.EqualTo(name));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsAtLeastPassthroughAndMetadataAndEntry() {
    var bytes = BuildMinimalRpa3(0x12345678, out _, out _);
    using var ms = new MemoryStream(bytes);
    var d = new RpaFormatDescriptor();
    var list = d.List(ms, null);
    Assert.That(list.Count, Is.GreaterThanOrEqualTo(3));
    Assert.That(list.Any(e => e.Name == "FULL.rpa"), Is.True);
    Assert.That(list.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(list.Any(e => e.Name == "hello.txt"), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesFilesToDisk() {
    var bytes = BuildMinimalRpa3(0xCAFEBABE, out var name, out var expected);
    var dir = Path.Combine(Path.GetTempPath(), "rpa_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      var d = new RpaFormatDescriptor();
      d.Extract(ms, dir, null, null);

      Assert.That(File.Exists(Path.Combine(dir, "FULL.rpa")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, "metadata.ini")), Is.True);
      Assert.That(File.Exists(Path.Combine(dir, name)), Is.True);
      Assert.That(File.ReadAllBytes(Path.Combine(dir, name)), Is.EqualTo(expected));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void BadMagic_Throws() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)'X');
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new RpaReader(ms));
  }
}
