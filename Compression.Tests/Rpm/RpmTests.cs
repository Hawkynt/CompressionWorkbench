using System.Buffers.Binary;
using System.Text;
using Compression.Core.Streams;
using FileFormat.Cpio;
using FileFormat.Rpm;

namespace Compression.Tests.Rpm;

[TestFixture]
public class RpmTests {

  // ── Synthetic RPM builder ─────────────────────────────────────────────────

  /// <summary>
  /// Builds a minimal synthetic RPM byte array.
  /// </summary>
  /// <param name="name">Package name for the NAME tag (1000).</param>
  /// <param name="version">Package version for the VERSION tag (1001).</param>
  /// <param name="release">Package release for the RELEASE tag (1002).</param>
  /// <param name="arch">Architecture for the ARCH tag (1022).</param>
  /// <param name="compressor">Payload compressor string for the PAYLOADCOMPRESSOR tag (1125). Null omits the tag.</param>
  /// <param name="payload">Raw payload bytes (already compressed).</param>
  private static byte[] BuildRpm(
    string name       = "testpkg",
    string version    = "1.0",
    string release    = "1",
    string arch       = "x86_64",
    string? compressor = "gzip",
    byte[]? payload   = null) {

    using var ms = new MemoryStream();

    // ── Lead (96 bytes) ──────────────────────────────────────────────────
    WriteLead(ms, name);

    // ── Signature (minimal: 0 entries, 0 store bytes) + 8-byte alignment ─
    WriteHeader(ms, []);
    AlignTo8(ms);

    // ── Main header with package metadata tags ────────────────────────────
    var tags = new List<(int Tag, string Value)> {
      (1000, name),
      (1001, version),
      (1002, release),
      (1022, arch),
    };
    if (compressor is not null)
      tags.Add((1125, compressor));

    WriteHeader(ms, tags);
    // No alignment required between main header and payload.

    // ── Payload ───────────────────────────────────────────────────────────
    if (payload is not null && payload.Length > 0)
      ms.Write(payload);

    return ms.ToArray();
  }

  private static void WriteLead(Stream s, string name) {
    Span<byte> lead = stackalloc byte[96];
    lead.Clear();

    // Magic
    lead[0] = 0xED; lead[1] = 0xAB; lead[2] = 0xEE; lead[3] = 0xDB;

    // Major/minor version
    lead[4] = 3; lead[5] = 0;

    // Type = binary (0), big-endian uint16
    lead[6] = 0; lead[7] = 0;

    // Architecture = 1 (i386 / generic), big-endian uint16
    lead[8] = 0; lead[9] = 1;

    // Package name: up to 65 null-terminated bytes at offset 10
    var nameBytes = Encoding.ASCII.GetBytes(name);
    int copyLen   = Math.Min(nameBytes.Length, 65);
    nameBytes.AsSpan(0, copyLen).CopyTo(lead[10..]);

    // OS = 1, big-endian uint16
    lead[76] = 0; lead[77] = 1;

    // Signature type = 5, big-endian uint16
    lead[78] = 0; lead[79] = 5;

    // Bytes 80-95 are reserved (already zero)
    s.Write(lead);
  }

  /// <summary>
  /// Writes an RPM header structure with NUL-terminated string tags.
  /// </summary>
  private static void WriteHeader(Stream s, IList<(int Tag, string Value)> tags) {
    // Build store: concatenate NUL-terminated UTF-8 strings
    using var storeMs = new MemoryStream();
    var offsets = new int[tags.Count];
    for (int i = 0; i < tags.Count; ++i) {
      offsets[i] = (int)storeMs.Position;
      var b = Encoding.UTF8.GetBytes(tags[i].Value);
      storeMs.Write(b);
      storeMs.WriteByte(0); // NUL terminator
    }
    byte[] store = storeMs.ToArray();

    // Preamble: magic(3) + version(1) + reserved(4) + nindex(4BE) + hsize(4BE) = 16 bytes
    Span<byte> preamble = stackalloc byte[16];
    preamble[0] = 0x8E; preamble[1] = 0xAD; preamble[2] = 0xE8;
    preamble[3] = 1; // version
    preamble[4] = 0; preamble[5] = 0; preamble[6] = 0; preamble[7] = 0; // reserved
    BinaryPrimitives.WriteInt32BigEndian(preamble[8..],  tags.Count);
    BinaryPrimitives.WriteInt32BigEndian(preamble[12..], store.Length);
    s.Write(preamble);

    // Index entries: tag(4BE) + type(4BE) + offset(4BE) + count(4BE)
    Span<byte> entry = stackalloc byte[16];
    for (int i = 0; i < tags.Count; ++i) {
      BinaryPrimitives.WriteInt32BigEndian(entry,       tags[i].Tag);
      BinaryPrimitives.WriteInt32BigEndian(entry[4..],  6);          // type = STRING
      BinaryPrimitives.WriteInt32BigEndian(entry[8..],  offsets[i]);
      BinaryPrimitives.WriteInt32BigEndian(entry[12..], 1);          // count = 1
      s.Write(entry);
    }

    // Store
    s.Write(store);
  }

  private static void AlignTo8(Stream s) {
    long rem = s.Position % 8;
    if (rem != 0) {
      int pad = (int)(8 - rem);
      Span<byte> zeros = stackalloc byte[pad];
      zeros.Clear();
      s.Write(zeros);
    }
  }

  // ── Tests ─────────────────────────────────────────────────────────────────

  [Category("Exception")]
  [Test]
  public void Reader_InvalidMagic_Throws() {
    byte[] rpm = BuildRpm();
    // Corrupt the first magic byte
    rpm[0] = 0x00;

    Assert.Throws<InvalidDataException>(() => {
      using var _ = new RpmReader(new MemoryStream(rpm));
    });
  }

  [Category("Exception")]
  [Test]
  public void Reader_InvalidMagic_SecondByte_Throws() {
    byte[] rpm = BuildRpm();
    rpm[1] = 0x00;

    Assert.Throws<InvalidDataException>(() => {
      using var _ = new RpmReader(new MemoryStream(rpm));
    });
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void RoundTrip_SyntheticRpm_HeaderFields() {
    byte[] payload = "synthetic-payload"u8.ToArray();
    byte[] rpm = BuildRpm(
      name:       "mypkg",
      version:    "2.3.4",
      release:    "5",
      arch:       "aarch64",
      compressor: "xz",
      payload:    payload);

    using var reader = new RpmReader(new MemoryStream(rpm));

    Assert.That(reader.Name,              Is.EqualTo("mypkg"));
    Assert.That(reader.Version,           Is.EqualTo("2.3.4"));
    Assert.That(reader.Release,           Is.EqualTo("5"));
    Assert.That(reader.Architecture,      Is.EqualTo("aarch64"));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("xz"));
  }

  [Category("EdgeCase")]
  [Test]
  public void Reader_DefaultCompressor_WhenTagAbsent() {
    // Build without a PAYLOADCOMPRESSOR tag
    byte[] rpm = BuildRpm(compressor: null);

    using var reader = new RpmReader(new MemoryStream(rpm));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("gzip"));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_HeaderParsing_EntryCount() {
    // 4 mandatory tags + 1 compressor tag = 5 entries in main header
    byte[] rpm = BuildRpm(
      name:       "pkg",
      version:    "1.0",
      release:    "1",
      arch:       "x86_64",
      compressor: "zstd");

    using var reader = new RpmReader(new MemoryStream(rpm));
    Assert.That(reader.Header.Entries, Has.Count.EqualTo(5));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_HeaderParsing_TagValues() {
    byte[] rpm = BuildRpm(
      name:       "querypkg",
      version:    "9.9",
      release:    "rc1",
      arch:       "s390x",
      compressor: "bzip2");

    using var reader = new RpmReader(new MemoryStream(rpm));

    // Verify GetString for each known tag
    Assert.That(reader.Header.GetString(1000), Is.EqualTo("querypkg"), "NAME tag");
    Assert.That(reader.Header.GetString(1001), Is.EqualTo("9.9"),      "VERSION tag");
    Assert.That(reader.Header.GetString(1002), Is.EqualTo("rc1"),      "RELEASE tag");
    Assert.That(reader.Header.GetString(1022), Is.EqualTo("s390x"),    "ARCH tag");
    Assert.That(reader.Header.GetString(1125), Is.EqualTo("bzip2"),    "PAYLOADCOMPRESSOR tag");
  }

  [Category("EdgeCase")]
  [Test]
  public void Reader_HeaderParsing_MissingTag_ReturnsNull() {
    byte[] rpm = BuildRpm();

    using var reader = new RpmReader(new MemoryStream(rpm));
    // Tag 9999 does not exist
    Assert.That(reader.Header.GetString(9999), Is.Null);
    Assert.That(reader.Header.GetInt32(9999),  Is.Null);
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_SignatureHeader_HasZeroEntries() {
    // Our builder writes an empty signature (0 index entries)
    byte[] rpm = BuildRpm();

    using var reader = new RpmReader(new MemoryStream(rpm));
    Assert.That(reader.SignatureHeader.Entries, Is.Empty);
    Assert.That(reader.SignatureHeader.Store,   Is.Empty);
  }

  [Category("HappyPath")]
  [Test]
  public void GetPayloadStream_ReturnsCorrectData() {
    byte[] payload = [0x01, 0x02, 0x03, 0x04, 0x05];
    byte[] rpm = BuildRpm(payload: payload);

    using var reader = new RpmReader(new MemoryStream(rpm));
    using var payloadStream = reader.GetPayloadStream();

    byte[] read = new byte[payload.Length];
    payloadStream.ReadExactly(read);
    Assert.That(read, Is.EqualTo(payload));
  }

  [Category("EdgeCase")]
  [Test]
  public void GetPayloadStream_EmptyPayload_ReturnsStreamAtEnd() {
    byte[] rpm = BuildRpm(payload: null);

    using var reader = new RpmReader(new MemoryStream(rpm));
    using var payloadStream = reader.GetPayloadStream();

    // Nothing to read — stream should be at its end
    int b = payloadStream.ReadByte();
    Assert.That(b, Is.EqualTo(-1));
  }

  [Category("HappyPath")]
  [Test]
  public void GetPayloadStream_IsCallableTwice_SeekableStream() {
    byte[] payload = "hello rpm payload"u8.ToArray();
    byte[] rpm = BuildRpm(payload: payload);

    using var reader = new RpmReader(new MemoryStream(rpm));

    // First call
    using (var s1 = reader.GetPayloadStream()) {
      byte[] buf = new byte[payload.Length];
      s1.ReadExactly(buf);
      Assert.That(buf, Is.EqualTo(payload));
    }

    // Second call should reposition to the same payload start
    using (var s2 = reader.GetPayloadStream()) {
      byte[] buf = new byte[payload.Length];
      s2.ReadExactly(buf);
      Assert.That(buf, Is.EqualTo(payload));
    }
  }

  [Category("Boundary")]
  [Test]
  public void Reader_LongPackageName_IsReadCorrectly() {
    // Names up to 65 bytes are stored in the lead; the main header has no length limit.
    const string name = "a-package-with-a-rather-long-name-indeed";
    byte[] rpm = BuildRpm(name: name);

    using var reader = new RpmReader(new MemoryStream(rpm));
    Assert.That(reader.Name, Is.EqualTo(name));
  }

  [Category("HappyPath")]
  [Test]
  public void Reader_CompressorVariants_AreReadBack() {
    foreach (string comp in new[] { "gzip", "bzip2", "xz", "lzma", "zstd" }) {
      byte[] rpm = BuildRpm(compressor: comp);
      using var reader = new RpmReader(new MemoryStream(rpm));
      Assert.That(reader.PayloadCompressor, Is.EqualTo(comp), $"Compressor: {comp}");
    }
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void ExtractFiles_GzipPayload() {
    // Build a cpio archive with two regular files
    byte[] cpioArchive;
    using (var cpioMs = new MemoryStream()) {
      using (var writer = new CpioWriter(cpioMs, leaveOpen: true)) {
        writer.AddFile("usr/bin/hello", "Hello World!"u8);
        writer.AddFile("etc/config.txt", "key=value"u8);
      }
      cpioArchive = cpioMs.ToArray();
    }

    // Compress the cpio archive with gzip
    byte[] gzipPayload;
    using (var gzMs = new MemoryStream()) {
      using (var gz = new FileFormat.Gzip.GzipStream(gzMs, CompressionStreamMode.Compress, leaveOpen: true))
        gz.Write(cpioArchive);
      gzipPayload = gzMs.ToArray();
    }

    // Build synthetic RPM with gzip-compressed cpio payload
    byte[] rpm = BuildRpm(compressor: "gzip", payload: gzipPayload);

    using var reader = new RpmReader(new MemoryStream(rpm));
    var files = reader.ExtractFiles();

    Assert.That(files, Has.Count.EqualTo(2));

    var hello = files.First(f => f.Path == "usr/bin/hello");
    Assert.That(Encoding.UTF8.GetString(hello.Data), Is.EqualTo("Hello World!"));

    var config = files.First(f => f.Path == "etc/config.txt");
    Assert.That(Encoding.UTF8.GetString(config.Data), Is.EqualTo("key=value"));
  }

  [Category("Exception")]
  [Test]
  public void ExtractFiles_UnsupportedCompressor_Throws() {
    byte[] rpm = BuildRpm(compressor: "unknown", payload: [0x00]);

    using var reader = new RpmReader(new MemoryStream(rpm));
    Assert.Throws<NotSupportedException>(() => reader.ExtractFiles());
  }

  // ── RpmWriter tests ────────────────────────────────────────────────────────

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_SingleFile_Gzip() {
    var writer = new RpmWriter {
      Name = "testpkg",
      Version = "1.0",
      Release = "1",
      Architecture = "x86_64",
      PayloadCompressor = "gzip",
    };
    writer.AddFile("usr/bin/hello", "Hello World!"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.Name, Is.EqualTo("testpkg"));
    Assert.That(reader.Version, Is.EqualTo("1.0"));
    Assert.That(reader.Release, Is.EqualTo("1"));
    Assert.That(reader.Architecture, Is.EqualTo("x86_64"));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("gzip"));

    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Path, Is.EqualTo("usr/bin/hello"));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("Hello World!"));
  }

  [Category("End2End")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_MultipleFiles() {
    var writer = new RpmWriter { Name = "multi" };
    writer.AddFile("file1.txt", "First"u8.ToArray());
    writer.AddFile("file2.txt", "Second"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(2));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("First"));
    Assert.That(Encoding.UTF8.GetString(files[1].Data), Is.EqualTo("Second"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_Bzip2Compressor() {
    var writer = new RpmWriter {
      Name = "bz2pkg",
      PayloadCompressor = "bzip2",
    };
    writer.AddFile("data.bin", "bzip2 test"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("bzip2"));
    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("bzip2 test"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_XzCompressor() {
    var writer = new RpmWriter {
      Name = "xzpkg",
      PayloadCompressor = "xz",
    };
    writer.AddFile("data.bin", "xz test"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("xz"));
    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("xz test"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_ZstdCompressor() {
    var writer = new RpmWriter {
      Name = "zstdpkg",
      PayloadCompressor = "zstd",
    };
    writer.AddFile("data.bin", "zstd test"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("zstd"));
    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("zstd test"));
  }

  [Category("HappyPath")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_RoundTrip_LzmaCompressor() {
    var writer = new RpmWriter {
      Name = "lzmapkg",
      PayloadCompressor = "lzma",
    };
    writer.AddFile("data.bin", "lzma test"u8.ToArray());

    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.PayloadCompressor, Is.EqualTo("lzma"));
    var files = reader.ExtractFiles();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(Encoding.UTF8.GetString(files[0].Data), Is.EqualTo("lzma test"));
  }

  [Category("Exception")]
  [Test]
  public void Writer_UnsupportedCompressor_Throws() {
    var writer = new RpmWriter { PayloadCompressor = "unknown" };
    writer.AddFile("x.txt", "x"u8.ToArray());

    using var ms = new MemoryStream();
    Assert.Throws<NotSupportedException>(() => writer.WriteTo(ms));
  }

  [Category("EdgeCase")]
  [Category("RoundTrip")]
  [Test]
  public void Writer_EmptyPackage_RoundTrips() {
    var writer = new RpmWriter { Name = "empty" };
    byte[] rpmBytes = writer.ToArray();

    using var reader = new RpmReader(new MemoryStream(rpmBytes));
    Assert.That(reader.Name, Is.EqualTo("empty"));
    var files = reader.ExtractFiles();
    Assert.That(files, Is.Empty);
  }
}
