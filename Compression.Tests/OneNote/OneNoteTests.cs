#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.OneNote;

namespace Compression.Tests.OneNote;

[TestFixture]
public class OneNoteTests {

  private static readonly byte[] Guid2010Plus = [
    0xE4, 0x52, 0x5C, 0x7B,
    0x8C, 0xD8,
    0xA7, 0x4D,
    0xAE, 0xB1, 0x53, 0x78, 0xD0, 0x29, 0x96, 0xD3,
  ];

  private static readonly byte[] Guid2007 = [
    0x3F, 0xDD, 0x9A, 0x10,
    0x1B, 0x91,
    0xF5, 0x49,
    0xA5, 0xD0, 0x17, 0x91, 0xED, 0xC8, 0xAE, 0xD8,
  ];

  private static byte[] MakeOneNoteHeader(byte[] guid, int totalSize = 1024) {
    var blob = new byte[totalSize];
    Buffer.BlockCopy(guid, 0, blob, 0, guid.Length);
    return blob;
  }

  [Test, Category("HappyPath")]
  public void Detector_RecognizesOneNote2010() {
    var blob = MakeOneNoteHeader(Guid2010Plus);
    using var ms = new MemoryStream(blob);
    Assert.That(OneNoteDetector.Detect(ms), Is.EqualTo(OneNoteVariant.OneNote2010Plus));
  }

  [Test, Category("HappyPath")]
  public void Detector_RecognizesOneNote2007() {
    var blob = MakeOneNoteHeader(Guid2007);
    using var ms = new MemoryStream(blob);
    Assert.That(OneNoteDetector.Detect(ms), Is.EqualTo(OneNoteVariant.OneNote2007));
  }

  [Test, Category("ErrorHandling")]
  public void Detector_RejectsGarbage() {
    var blob = new byte[1024];
    var rng = new Random(1234);
    rng.NextBytes(blob);
    // Force first 16 bytes to be neither GUID — overwrite the leading bytes with a known
    // non-matching pattern (random bytes happen to never collide, but this makes it deterministic).
    for (var i = 0; i < 16; i++) blob[i] = 0xCC;
    using var ms = new MemoryStream(blob);
    Assert.That(OneNoteDetector.Detect(ms), Is.EqualTo(OneNoteVariant.Unknown));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var blob = MakeOneNoteHeader(Guid2010Plus);
    using var ms = new MemoryStream(blob);
    var entries = new OneNoteFormatDescriptor().List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.one"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
    Assert.That(entries[0].OriginalSize, Is.EqualTo(blob.Length));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullOne_PreservesBytes() {
    var blob = MakeOneNoteHeader(Guid2010Plus, totalSize: 4096);
    // Fill the post-header region with a recognisable pattern so we can verify byte-for-byte.
    for (var i = 16; i < blob.Length; i++) blob[i] = (byte)(i & 0xFF);

    var tmp = Path.Combine(Path.GetTempPath(), "onenote_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new OneNoteFormatDescriptor().Extract(ms, tmp, null, ["FULL.one"]);

      var outPath = Path.Combine(tmp, "FULL.one");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(blob));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsParseStatus() {
    var blob = MakeOneNoteHeader(Guid2010Plus);
    var tmp = Path.Combine(Path.GetTempPath(), "onenote_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(blob);
      new OneNoteFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("parse_status = partial"));
      Assert.That(text, Does.Contain("variant = OneNote 2010+"));
      Assert.That(text, Does.Contain("[onenote]"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new OneNoteFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("OneNote"));
    Assert.That(d.DisplayName, Is.EqualTo("Microsoft OneNote"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".one"));
    Assert.That(d.Extensions, Contains.Item(".one"));
    Assert.That(d.Extensions, Contains.Item(".onetoc2"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(2));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(Guid2010Plus));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(Guid2007));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[1].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("one"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("OneNote"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new OneNoteFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
