#pragma warning disable CS1591
using System.Buffers.Binary;
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

  private static readonly byte[] GuidTableOfContents = [
    0xA1, 0x2F, 0xFF, 0x43,
    0xD9, 0xEF,
    0x76, 0x4C,
    0x9E, 0xE2, 0x10, 0xEA, 0x57, 0x22, 0x76, 0x5F,
  ];

  private static byte[] MakeOneNoteHeader(byte[] guid, int totalSize = 4096) {
    var blob = new byte[totalSize];
    Buffer.BlockCopy(guid, 0, blob, 0, guid.Length);
    if (guid.SequenceEqual(Guid2010Plus) || guid.SequenceEqual(GuidTableOfContents)) {
      Buffer.BlockCopy(Guid2007, 0, blob, 0x30, Guid2007.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x40), 42);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x44), 42);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x48), 42);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x4C), 42);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0xA0), 0x800);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0xA8), 0x400);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0xAC), 0x400);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0xB4), 0x400);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0xC4), (ulong)totalSize);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0x400), 0xA4567AB1F5F7F4C4);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x408), 0x10);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x40C), 0);
      BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(0x410), 0x95006C08);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0x7EC), ulong.MaxValue);
      BinaryPrimitives.WriteUInt64LittleEndian(blob.AsSpan(0x7F8), 0x8BC215C38233BA4B);
    }
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

  [Test, Category("HappyPath")]
  public void Detector_RecognizesTableOfContentsFileType() {
    using var ms = new MemoryStream(MakeOneNoteHeader(GuidTableOfContents));
    Assert.That(OneNoteDetector.DetectFileType(ms), Is.EqualTo(OneNoteFileType.TableOfContents));
    Assert.That(OneNoteDetector.Detect(ms), Is.EqualTo(OneNoteVariant.OneNote2010Plus));
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

    Assert.That(entries, Has.Count.EqualTo(4));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.one"));
    Assert.That(entries[1].Name, Is.EqualTo("root_file_node_list.bin"));
    Assert.That(entries[2].Name, Is.EqualTo("transaction_log_fragment.bin"));
    Assert.That(entries[3].Name, Is.EqualTo("metadata.ini"));
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
  public void Extract_ExposesBoundedRootAndTransactionChunks() {
    var blob = MakeOneNoteHeader(Guid2010Plus, 4096);
    for (var i = 0x430; i < blob.Length; i++) blob[i] = (byte)(i * 17);
    var tmp = Path.Combine(Path.GetTempPath(), "onenote_chunks_" + Guid.NewGuid().ToString("N"));
    try {
      using var stream = new MemoryStream(blob);
      new OneNoteFormatDescriptor().Extract(stream, tmp, null, ["root_file_node_list.bin", "transaction_log_fragment.bin"]);
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "root_file_node_list.bin")), Is.EqualTo(blob.AsSpan(0x400, 0x400).ToArray()));
      Assert.That(File.ReadAllBytes(Path.Combine(tmp, "transaction_log_fragment.bin")), Is.EqualTo(blob.AsSpan(0x800, 0x400).ToArray()));
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
      Assert.That(text, Does.Contain("header_status = validated"));
      Assert.That(text, Does.Contain("root_file_node_list_offset = 1024"));
      Assert.That(text, Does.Contain("root_file_node_fragment_node_count = 1"));
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
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(3));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(Guid2010Plus));
    Assert.That(d.MagicSignatures[1].Bytes, Is.EqualTo(GuidTableOfContents));
    Assert.That(d.MagicSignatures[2].Bytes, Is.EqualTo(Guid2007));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[1].Offset, Is.EqualTo(0));
    Assert.That(d.MagicSignatures[2].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("one"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("OneNote"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Capabilities_OfferReadingButNotCreation() {
    // Copying an existing file byte for byte is not a OneNote writer; creation stays off until
    // something can build a revision store that OneNote itself opens.
    var d = new OneNoteFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.False);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void GivenATableOfContents_WhenTheWholeFileIsExtracted_ThenItIsNamedAndPreservedAsOnetoc2() {
    var expected = MakeOneNoteHeader(GuidTableOfContents);
    using var stream = new MemoryStream(expected, writable: false);
    Assert.That(new OneNoteFormatDescriptor().ExtractEntryToMemory(stream, "FULL.onetoc2", null), Is.EqualTo(expected));
  }

  [Test, Category("ErrorHandling")]
  public void List_DoesNotExposeOutOfBoundsOneStoreReferences() {
    var malformed = MakeOneNoteHeader(Guid2010Plus);
    BinaryPrimitives.WriteUInt64LittleEndian(malformed.AsSpan(0xA0), (ulong)malformed.Length - 8);
    BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(0xA8), 0x400);
    using var stream = new MemoryStream(malformed);

    Assert.That(new OneNoteFormatDescriptor().List(stream, null).Select(x => x.Name),
      Is.EqualTo(new[] { "FULL.one", "metadata.ini" }));
  }

  [Test, Category("ErrorHandling")]
  public void GivenAMalformedRootFileNodeList_WhenInspected_ThenMetadataSaysSoAndNothingThrows() {
    var data = MakeOneNoteHeader(Guid2010Plus);
    BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x400), 0);     // fragment header magic gone
    using var stream = new MemoryStream(data, writable: false);
    var meta = Encoding.UTF8.GetString(new OneNoteFormatDescriptor().ExtractEntryToMemory(stream, "metadata.ini", null));
    Assert.That(meta, Does.Contain("root_file_node_list_status = malformed"));
  }

  // ── Sections written by OneNote (Apache Tika test documents, see ReferenceVectors/README.md) ──

  private static byte[] Vector(string name) {
    using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream($"OneNoteVectors.{name}")
      ?? throw new FileNotFoundException($"Embedded OneNote vector '{name}' is missing from the test assembly.");
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  [TestCase("testOneNote.one")]
  [TestCase("test-tika-4303-Chinese-notes.one")]
  public void GivenASectionWrittenByOneNote_WhenInspected_ThenHeaderAndRootFragmentValidate(string name) {
    var data = Vector(name);
    using var stream = new MemoryStream(data, writable: false);
    var d = new OneNoteFormatDescriptor();
    Assert.That(OneNoteDetector.DetectFileType(stream), Is.EqualTo(OneNoteFileType.Section));
    Assert.That(d.List(stream, null).Select(e => e.Name),
      Is.EqualTo(new[] { "FULL.one", "root_file_node_list.bin", "transaction_log_fragment.bin", "metadata.ini" }));
    var meta = Encoding.UTF8.GetString(d.ExtractEntryToMemory(stream, "metadata.ini", null));
    Assert.That(meta, Does.Contain("header_status = validated"));
    Assert.That(meta, Does.Contain($"expected_file_size = {data.Length}"));
    Assert.That(meta, Does.Contain("root_file_node_list_id = "));
    Assert.That(meta, Does.Not.Contain("malformed"));
    Assert.That(d.ExtractEntryToMemory(stream, "FULL.one", null), Is.EqualTo(data));
  }

  [TestCase("testOneNote.one")]
  [TestCase("test-tika-4303-Chinese-notes.one")]
  public void GivenASectionWrittenByOneNote_WhenTheRootListIsExtracted_ThenItIsAFragmentBetweenItsMagics(string name) {
    using var stream = new MemoryStream(Vector(name), writable: false);
    var root = new OneNoteFormatDescriptor().ExtractEntryToMemory(stream, "root_file_node_list.bin", null);
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(root), Is.EqualTo(0xA4567AB1F5F7F4C4));
    Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(root.AsSpan(root.Length - 8)), Is.EqualTo(0x8BC215C38233BA4B));
  }
}
