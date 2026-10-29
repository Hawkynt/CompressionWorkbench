using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Xar;

namespace Compression.Tests.Xar;

/// <summary>
/// XAR members in every encoding xar defines, from archives libarchive's bsdtar wrote (see
/// Xar/ReferenceVectors/README.md). The reader checks each member against the SHA-1 the TOC records,
/// and the five archives hold the same files, so all encodings must decode to identical bytes.
/// </summary>
[TestFixture]
public class XarEncodingVectorTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("XarVectors." + name)
      ?? throw new FileNotFoundException("Embedded XAR vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ExtractAll(byte[] archive, out List<XarEntry> entries) {
    var reader = new XarReader(new MemoryStream(archive));
    entries = reader.Entries.ToList();
    return reader.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.FileName, reader.Extract, StringComparer.Ordinal);
  }

  [Test, Category("HappyPath")]
  [TestCase("none.xar", "none")]
  [TestCase("gzip.xar", "zlib")]
  [TestCase("bzip2.xar", "bzip2")]
  [TestCase("lzma.xar", "lzma")]
  [TestCase("xz.xar", "xz")]
  public void RealArchive_EveryEncodingDecodesAndPassesItsChecksum(string vector, string method) {
    var files = ExtractAll(Vector(vector), out var entries);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "TEXT.TXT", "RANDOM.BIN", "REPEAT.BIN", "ONE.TXT" }));
    Assert.That(entries.Single(e => e.FileName == "TEXT.TXT").Method, Is.EqualTo(method));
    Assert.That(Encoding.ASCII.GetString(files["TEXT.TXT"]), Does.StartWith("line 00001: the quick brown fox"));
    Assert.That(files["REPEAT.BIN"], Is.EqualTo(files["RANDOM.BIN"].Concat(files["RANDOM.BIN"]).ToArray()));
    Assert.That(files["ONE.TXT"], Is.EqualTo("a"u8.ToArray()));
    // SHA-256 of the inputs as the generator script printed them (see ReferenceVectors/README.md).
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files["TEXT.TXT"])),
      Is.EqualTo("c0541069eb5a1c9d4a11c2abb3993a554ba4a272fa671ea926f035775548de8e"));
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files["RANDOM.BIN"])),
      Is.EqualTo("44763bf1aa0863dd94c239c350c8fe4aa8dc640d51fe567e94f1b956b261a46f"));
  }

  [Test, Category("HappyPath")]
  public void AllEncodings_DecodeToTheSameFiles() {
    var reference = ExtractAll(Vector("none.xar"), out _);
    foreach (var vector in new[] { "gzip.xar", "bzip2.xar", "lzma.xar", "xz.xar" }) {
      var other = ExtractAll(Vector(vector), out _);
      foreach (var (name, bytes) in reference)
        Assert.That(other[name], Is.EqualTo(bytes).AsCollection, $"{vector}: {name}");
    }
  }

  // ── Hand-built archives for the refusal paths ─────────────────────────────────

  /// <summary>Builds a one-member XAR whose heap holds <paramref name="heap"/> under the given encoding style.</summary>
  private static byte[] BuildXar(byte[] heap, long size, string style, string? sha1Hex) {
    var checksum = sha1Hex is null ? "" : $"<extracted-checksum style=\"sha1\">{sha1Hex}</extracted-checksum>";
    var toc = $"""
      <?xml version="1.0" encoding="UTF-8"?>
      <xar><toc><file id="1"><name>m.bin</name><type>file</type>
      <data><length>{heap.Length}</length><offset>0</offset><size>{size}</size>
      <encoding style="{style}"/>{checksum}</data></file></toc></xar>
      """;
    var tocBytes = Encoding.UTF8.GetBytes(toc);
    using var z = new MemoryStream();
    using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
      zs.Write(tocBytes);
    var tocZ = z.ToArray();

    var header = new byte[28];
    "xar!"u8.CopyTo(header);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 28);
    BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), 1);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)tocZ.Length);
    BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(16), (ulong)tocBytes.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), 0);
    return [.. header, .. tocZ, .. heap];
  }

  [Test, Category("Exception")]
  public void UnknownEncoding_IsRefusedNotReturnedRaw() {
    var archive = BuildXar("payload"u8.ToArray(), 7, "application/x-unheard-of", null);
    var reader = new XarReader(new MemoryStream(archive));
    Assert.That(reader.Entries[0].Method, Is.EqualTo("application/x-unheard-of"));
    Assert.Throws<NotSupportedException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void ChecksumMismatch_Throws() {
    var data = "payload"u8.ToArray();
    var wrong = Convert.ToHexString(SHA1.HashData("other"u8));
    var reader = new XarReader(new MemoryStream(BuildXar(data, data.Length, "application/octet-stream", wrong)));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test, Category("HappyPath")]
  public void ChecksumMatch_LowerCaseHex_IsAccepted() {
    var data = "payload"u8.ToArray();
    var right = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    var reader = new XarReader(new MemoryStream(BuildXar(data, data.Length, "application/octet-stream", right)));
    Assert.That(reader.Extract(reader.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("Exception")]
  public void SizeMismatch_Throws() {
    var data = "payload"u8.ToArray();
    var reader = new XarReader(new MemoryStream(BuildXar(data, data.Length + 1, "application/octet-stream", null)));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries[0]));
  }

  [Test, Category("Exception")]
  public void XzStyleWithoutXzMagic_Throws() {
    var data = "not an xz stream"u8.ToArray();
    var reader = new XarReader(new MemoryStream(BuildXar(data, 5, "application/x-xz", null)));
    Assert.Throws<InvalidDataException>(() => reader.Extract(reader.Entries[0]));
  }
}
