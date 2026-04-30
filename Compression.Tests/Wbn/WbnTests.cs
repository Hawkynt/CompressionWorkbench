#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.Wbn;

namespace Compression.Tests.Wbn;

[TestFixture]
public class WbnTests {

  /// <summary>
  /// Tiny CBOR writer scoped to the test fixture — emits the subset of items needed to
  /// build a structurally valid Web Bundle: byte strings, text strings, unsigned ints,
  /// arrays, and maps. Inverse of the reader's <c>CborWalker</c>.
  /// </summary>
  private static class Cbor {

    public static byte[] Uint(ulong value) {
      using var ms = new MemoryStream();
      WriteHeader(ms, WbnConstants.MajorTypeUnsignedInt, value);
      return ms.ToArray();
    }

    public static byte[] TextString(string s) {
      var utf8 = Encoding.UTF8.GetBytes(s);
      using var ms = new MemoryStream();
      WriteHeader(ms, WbnConstants.MajorTypeTextString, (ulong)utf8.Length);
      ms.Write(utf8, 0, utf8.Length);
      return ms.ToArray();
    }

    public static byte[] ByteString(byte[] bytes) {
      using var ms = new MemoryStream();
      WriteHeader(ms, WbnConstants.MajorTypeByteString, (ulong)bytes.Length);
      ms.Write(bytes, 0, bytes.Length);
      return ms.ToArray();
    }

    public static byte[] Array(params byte[][] items) {
      using var ms = new MemoryStream();
      WriteHeader(ms, WbnConstants.MajorTypeArray, (ulong)items.Length);
      foreach (var i in items) ms.Write(i, 0, i.Length);
      return ms.ToArray();
    }

    public static byte[] Map(params (byte[] Key, byte[] Value)[] pairs) {
      using var ms = new MemoryStream();
      WriteHeader(ms, WbnConstants.MajorTypeMap, (ulong)pairs.Length);
      foreach (var (k, v) in pairs) {
        ms.Write(k, 0, k.Length);
        ms.Write(v, 0, v.Length);
      }
      return ms.ToArray();
    }

    public static byte[] Concat(params byte[][] parts) {
      using var ms = new MemoryStream();
      foreach (var p in parts) ms.Write(p, 0, p.Length);
      return ms.ToArray();
    }

    private static void WriteHeader(Stream s, byte major, ulong value) {
      var prefix = (byte)(major << 5);
      if (value < 24) {
        s.WriteByte((byte)(prefix | (byte)value));
      } else if (value <= byte.MaxValue) {
        s.WriteByte((byte)(prefix | 24));
        s.WriteByte((byte)value);
      } else if (value <= ushort.MaxValue) {
        s.WriteByte((byte)(prefix | 25));
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)value);
        s.Write(buf);
      } else if (value <= uint.MaxValue) {
        s.WriteByte((byte)(prefix | 26));
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)value);
        s.Write(buf);
      } else {
        s.WriteByte((byte)(prefix | 27));
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf);
      }
    }
  }

  /// <summary>
  /// Builds a minimal Web Bundle whose top-level CBOR-array-of-4 contains:
  /// magic (8-byte byte string), version (4-byte byte string), primary URL (text string),
  /// and a section-lengths byte string. This deliberately omits the sections array and
  /// length suffix that conformant b2 files include — readers should still surface
  /// magic_ok=true and the version/primary URL even when the structure is truncated.
  /// </summary>
  private static byte[] BuildMinimalB2(string version, string primaryUrl) {
    // Magic byte string body: 8 bytes of UTF-8 emoji.
    var magicBody = new byte[] {
      0xF0, 0x9F, 0x8C, 0xD0,
      0xF0, 0x9F, 0x93, 0xA6,
    };
    var versionBody = Encoding.UTF8.GetBytes(version.PadRight(4, '\0'));
    var sectionLengths = Cbor.Array(); // empty section-lengths array

    return Cbor.Concat(
      // 0x84 = array(4); we hand-build to keep the magic byte sequence canonical.
      [0x84],
      Cbor.ByteString(magicBody),
      Cbor.ByteString(versionBody),
      Cbor.TextString(primaryUrl),
      Cbor.ByteString(sectionLengths)
    );
  }

  /// <summary>
  /// Builds a fuller Web Bundle that includes a non-empty index section. Sections array
  /// has exactly one element: the index map keyed by URL → [offset, length].
  /// </summary>
  private static byte[] BuildBundleWithIndex(string version, string primaryUrl, string[] urls) {
    var magicBody = new byte[] {
      0xF0, 0x9F, 0x8C, 0xD0,
      0xF0, 0x9F, 0x93, 0xA6,
    };
    var versionBody = Encoding.UTF8.GetBytes(version.PadRight(4, '\0'));

    // Build an index map: { url1 -> [0, 1], url2 -> [1, 1], ... }
    var indexPairs = new (byte[] Key, byte[] Value)[urls.Length];
    for (var i = 0; i < urls.Length; i++) {
      indexPairs[i] = (
        Cbor.TextString(urls[i]),
        Cbor.Array(Cbor.Uint((ulong)i), Cbor.Uint(1))
      );
    }
    var indexMap = Cbor.Map(indexPairs);

    var sectionLengthsArray = Cbor.Array(
      Cbor.TextString("index"),
      Cbor.Uint((ulong)indexMap.Length)
    );

    var sectionsArray = Cbor.Array(indexMap);

    return Cbor.Concat(
      [0x84],
      Cbor.ByteString(magicBody),
      Cbor.ByteString(versionBody),
      Cbor.TextString(primaryUrl),
      Cbor.ByteString(sectionLengthsArray),
      sectionsArray
    );
  }

  [Test, Category("HappyPath")]
  public void Magic_IsCorrect() {
    var d = new WbnFormatDescriptor();
    var expected = new byte[] {
      0x84, 0x48,
      0xF0, 0x9F, 0x8C, 0xD0,
      0xF0, 0x9F, 0x93, 0xA6,
    };
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(expected));
    Assert.That(d.MagicSignatures[0].Bytes, Has.Length.EqualTo(10));
  }

  [Test, Category("HappyPath")]
  public void Reader_AcceptsValidMagic() {
    var bundle = BuildMinimalB2("b2", "https://example.test/");
    using var ms = new MemoryStream(bundle);
    var r = new WbnReader(ms);
    Assert.That(r.MagicOk, Is.True);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xCC);
    using var ms = new MemoryStream(garbage);
    Assert.Throws<InvalidDataException>(() => _ = new WbnReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsVersionString() {
    var bundle = BuildMinimalB2("b2", "https://example.test/");
    using var ms = new MemoryStream(bundle);
    var r = new WbnReader(ms);
    Assert.That(r.Version, Is.EqualTo("b2"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ExtractsPrimaryUrl() {
    const string Url = "https://example.test/index.html";
    var bundle = BuildMinimalB2("b2", Url);
    using var ms = new MemoryStream(bundle);
    var r = new WbnReader(ms);
    Assert.That(r.PrimaryUrl, Is.EqualTo(Url));
  }

  [Test, Category("HappyPath")]
  public void Reader_CountsIndexEntries() {
    var bundle = BuildBundleWithIndex("b2", "https://example.test/", [
      "https://example.test/",
      "https://example.test/styles.css",
      "https://example.test/script.js",
    ]);
    using var ms = new MemoryStream(bundle);
    var r = new WbnReader(ms);
    Assert.That(r.ResourceCount, Is.EqualTo(3));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_PartialOnTruncated() {
    // Magic-only file: no version, no URL, no sections.
    var bundle = WbnConstants.Magic;
    using var ms = new MemoryStream(bundle);
    var r = new WbnReader(ms);
    Assert.That(r.MagicOk, Is.True);
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
    Assert.That(r.Version, Is.EqualTo("unknown"));
    Assert.That(r.PrimaryUrl, Is.EqualTo("unknown"));
    Assert.That(r.ResourceCount, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var bundle = BuildMinimalB2("b2", "https://example.test/");
    using var ms = new MemoryStream(bundle);
    var entries = new WbnFormatDescriptor().List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.wbn"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullWbn_PreservesBytes() {
    var bundle = BuildMinimalB2("b2", "https://example.test/");
    var tmp = Path.Combine(Path.GetTempPath(), "wbn_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bundle);
      new WbnFormatDescriptor().Extract(ms, tmp, null, ["FULL.wbn"]);

      var outPath = Path.Combine(tmp, "FULL.wbn");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(bundle));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsParseStatus() {
    var bundle = BuildBundleWithIndex("b2", "https://example.test/", [
      "https://example.test/",
      "https://example.test/a.css",
    ]);
    var tmp = Path.Combine(Path.GetTempPath(), "wbn_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bundle);
      new WbnFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[webbundle]"));
      Assert.That(text, Does.Contain("magic_ok = true"));
      Assert.That(text, Does.Contain("version = b2"));
      Assert.That(text, Does.Contain("primary_url = https://example.test/"));
      Assert.That(text, Does.Contain("resource_count = 2"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new WbnFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new WbnFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Wbn"));
    Assert.That(d.DisplayName, Is.EqualTo("Web Bundle"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".wbn"));
    Assert.That(d.Extensions, Contains.Item(".wbn"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(WbnConstants.Magic));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("webbundle"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("Web Bundle"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
