using System.Buffers.Binary;
using System.Text;
using FileFormat.Dicom;

namespace Compression.Tests.DicomDir;

[TestFixture]
public class DicomDirTests {

  /// <summary>
  /// Builds a minimal but valid DICOMDIR: 128-byte preamble + "DICM" magic +
  /// file-meta group 0002 elements (explicit VR LE), then a
  /// DirectoryRecordSequence (0004,1220) with two items each referencing a
  /// sibling file via ReferencedFileID (0004,1500).
  /// </summary>
  private static byte[] BuildDicomDir(params string[] referencedPaths) {
    using var ms = new MemoryStream();
    // 128-byte preamble.
    ms.Write(new byte[128]);
    // DICM magic.
    ms.Write(Encoding.ASCII.GetBytes("DICM"));

    // File meta group 0002: minimal (FileMetaInformationGroupLength + MediaStorageSOPClassUID)
    // — we keep it trivial: just write a FileSetID (0004,1130) CS element in the body, then the
    // DirectoryRecordSequence (0004,1220) SQ with undefined length.

    // (0004,1130) CS FileSetID "DEMO"
    WriteExplicitShort(ms, 0x0004, 0x1130, "CS", "DEMO "); // pad to even length

    // (0004,1220) SQ DirectoryRecordSequence — undefined length
    var sqHeader = new byte[12];
    BinaryPrimitives.WriteUInt16LittleEndian(sqHeader.AsSpan(0), 0x0004);
    BinaryPrimitives.WriteUInt16LittleEndian(sqHeader.AsSpan(2), 0x1220);
    sqHeader[4] = (byte)'S'; sqHeader[5] = (byte)'Q';
    // reserved 6..8 = 0
    BinaryPrimitives.WriteUInt32LittleEndian(sqHeader.AsSpan(8), 0xFFFFFFFFu);
    ms.Write(sqHeader);

    foreach (var path in referencedPaths) {
      // Item (FFFE,E000) with undefined length.
      var itemHeader = new byte[8];
      BinaryPrimitives.WriteUInt16LittleEndian(itemHeader.AsSpan(0), 0xFFFE);
      BinaryPrimitives.WriteUInt16LittleEndian(itemHeader.AsSpan(2), 0xE000);
      BinaryPrimitives.WriteUInt32LittleEndian(itemHeader.AsSpan(4), 0xFFFFFFFFu);
      ms.Write(itemHeader);

      // (0004,1430) CS DirectoryRecordType = "IMAGE"
      WriteExplicitShort(ms, 0x0004, 0x1430, "CS", "IMAGE ");

      // (0004,1500) CS ReferencedFileID = path (segments joined with backslash)
      var refValue = path.Replace('/', '\\');
      if ((refValue.Length & 1) == 1) refValue += " ";
      WriteExplicitShort(ms, 0x0004, 0x1500, "CS", refValue);

      // Item delimiter (FFFE,E00D) length=0
      var itemEnd = new byte[8];
      BinaryPrimitives.WriteUInt16LittleEndian(itemEnd.AsSpan(0), 0xFFFE);
      BinaryPrimitives.WriteUInt16LittleEndian(itemEnd.AsSpan(2), 0xE00D);
      ms.Write(itemEnd);
    }

    // Sequence delimiter (FFFE,E0DD) length=0
    var seqEnd = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(seqEnd.AsSpan(0), 0xFFFE);
    BinaryPrimitives.WriteUInt16LittleEndian(seqEnd.AsSpan(2), 0xE0DD);
    ms.Write(seqEnd);

    return ms.ToArray();
  }

  private static void WriteExplicitShort(Stream ms, ushort group, ushort elementId, string vr, string value) {
    var bytes = Encoding.ASCII.GetBytes(value);
    var header = new byte[8];
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0), group);
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), elementId);
    header[4] = (byte)vr[0]; header[5] = (byte)vr[1];
    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)bytes.Length);
    ms.Write(header);
    ms.Write(bytes);
  }

  [Test, Category("HappyPath")]
  public void Parser_DetectsDirectoryRecordSequence() {
    var data = BuildDicomDir("IMG00001", "IMG00002");
    var result = DicomDirParser.Parse(data);
    Assert.That(result.HasDirectoryRecordSequence, Is.True);
    Assert.That(result.Records.Count, Is.EqualTo(2));
    Assert.That(result.Records[0].RecordType, Is.EqualTo("IMAGE"));
    Assert.That(result.Records[0].ReferencedFileId, Is.Not.Null);
    Assert.That(result.Records[0].ReferencedFileId![0], Is.EqualTo("IMG00001"));
    Assert.That(result.FileSetId, Is.EqualTo("DEMO"));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_List_IncludesMetadataAndReferencedEntries() {
    var data = BuildDicomDir("A\\IMG1", "B\\IMG2");
    using var ms = new MemoryStream(data);
    var entries = new DicomDirFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Count(e => e.Name.EndsWith("IMG1", StringComparison.Ordinal)), Is.EqualTo(1));
    Assert.That(entries.Count(e => e.Name.EndsWith("IMG2", StringComparison.Ordinal)), Is.EqualTo(1));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Descriptor_Extract_ResolvesSiblingFiles() {
    var tmp = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    Directory.CreateDirectory(tmp);
    try {
      // Create sibling "IMG1" with known content.
      var siblingPath = Path.Combine(tmp, "IMG1");
      File.WriteAllBytes(siblingPath, Encoding.ASCII.GetBytes("PAYLOAD"));

      var data = BuildDicomDir("IMG1");
      var dcmdirPath = Path.Combine(tmp, "DICOMDIR");
      File.WriteAllBytes(dcmdirPath, data);

      var outDir = Path.Combine(tmp, "out");
      Directory.CreateDirectory(outDir);
      using var fs = File.OpenRead(dcmdirPath);
      new DicomDirFormatDescriptor().Extract(fs, outDir, null, null);

      var extracted = Path.Combine(outDir, "IMG1");
      Assert.That(File.Exists(extracted), Is.True, "Expected extracted sibling file");
      Assert.That(File.ReadAllBytes(extracted), Is.EqualTo(Encoding.ASCII.GetBytes("PAYLOAD")));
      Assert.That(File.Exists(Path.Combine(outDir, "metadata.ini")), Is.True);
    } finally {
      Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("EdgeCase")]
  public void Parser_NotDicom_ReturnsEmpty() {
    var data = new byte[200]; // no DICM magic
    var result = DicomDirParser.Parse(data);
    Assert.That(result.HasDirectoryRecordSequence, Is.False);
    Assert.That(result.Records, Is.Empty);
  }
}
