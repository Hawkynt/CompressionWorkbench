#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Compression.Registry;
using FileFormat.Matlab;

namespace Compression.Tests.Matlab;

[TestFixture]
public class MatlabTests {

  // ----- synthesis helpers -----

  /// <summary>Writes the 128-byte MAT v5 header (description padded with spaces, version 0x0100, endian 'IM').</summary>
  private static void WriteHeader(Stream s, string? description = null) {
    var desc = description ?? "MATLAB 5.0 MAT-file, Platform: TEST, Created on: 2026-01-01";
    var descBytes = new byte[MatlabConstants.DescriptionLength];
    for (var i = 0; i < descBytes.Length; i++) descBytes[i] = 0x20;
    var d = Encoding.ASCII.GetBytes(desc);
    Array.Copy(d, 0, descBytes, 0, Math.Min(d.Length, descBytes.Length));
    s.Write(descBytes, 0, descBytes.Length);

    // 8 bytes subsystem-data offset (zeros).
    s.Write(new byte[8], 0, 8);

    // Version 0x0100 little-endian.
    var version = new byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(version, MatlabConstants.ExpectedVersion);
    s.Write(version, 0, 2);

    // Endian indicator "IM".
    s.Write(MatlabConstants.EndianIM, 0, 2);
  }

  /// <summary>Writes a long-form data element: 4-byte type, 4-byte length, payload, padded to 8-byte boundary.</summary>
  private static void WriteElement(Stream s, uint type, byte[] payload) {
    var typeBuf = new byte[4];
    var lenBuf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(typeBuf, type);
    BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, (uint)payload.Length);
    s.Write(typeBuf, 0, 4);
    s.Write(lenBuf, 0, 4);
    s.Write(payload, 0, payload.Length);
    var pad = (8 - (payload.Length & 7)) & 7;
    if (pad > 0) s.Write(new byte[pad], 0, pad);
  }

  /// <summary>Builds a miMATRIX payload: ArrayFlags + Dimensions + Name + Real-data sub-elements.</summary>
  private static byte[] BuildMatrixPayload(byte classCode, int[] dims, string name, uint dataType, byte[] data) {
    using var ms = new MemoryStream();

    // ArrayFlags: 8 bytes payload (2 x uint32). First uint32 packs class in low byte.
    var flagsPayload = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(flagsPayload.AsSpan(0, 4), classCode);
    WriteElement(ms, MatlabConstants.MiUINT32, flagsPayload);

    // Dimensions: int32 array.
    var dimsPayload = new byte[dims.Length * 4];
    for (var i = 0; i < dims.Length; i++)
      BinaryPrimitives.WriteInt32LittleEndian(dimsPayload.AsSpan(i * 4, 4), dims[i]);
    WriteElement(ms, MatlabConstants.MiINT32, dimsPayload);

    // Array name (miINT8).
    WriteElement(ms, MatlabConstants.MiINT8, Encoding.ASCII.GetBytes(name));

    // Real data.
    WriteElement(ms, dataType, data);

    return ms.ToArray();
  }

  /// <summary>Builds a complete MAT v5 file with a single 1x1 double scalar element named <paramref name="name"/>.</summary>
  private static byte[] BuildScalarDoubleMat(string name, double value, string? description = null) {
    using var ms = new MemoryStream();
    WriteHeader(ms, description);

    var dataBytes = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(dataBytes, value);
    var matrixPayload = BuildMatrixPayload(
      MatlabConstants.MxDOUBLE_CLASS, [1, 1], name, MatlabConstants.MiDOUBLE, dataBytes);
    WriteElement(ms, MatlabConstants.MiMATRIX, matrixPayload);
    return ms.ToArray();
  }

  /// <summary>Wraps a miMATRIX element body in a zlib-compressed miCOMPRESSED element.</summary>
  private static byte[] BuildCompressedMat(string name, double value) {
    // Build the inner element bytes (type+length+payload, no outer header).
    using var inner = new MemoryStream();
    var dataBytes = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(dataBytes, value);
    var matrixPayload = BuildMatrixPayload(
      MatlabConstants.MxDOUBLE_CLASS, [1, 1], name, MatlabConstants.MiDOUBLE, dataBytes);
    WriteElement(inner, MatlabConstants.MiMATRIX, matrixPayload);
    var innerBytes = inner.ToArray();

    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
      zlib.Write(innerBytes, 0, innerBytes.Length);
    var compressedBytes = compressed.ToArray();

    using var ms = new MemoryStream();
    WriteHeader(ms);
    WriteElement(ms, MatlabConstants.MiCOMPRESSED, compressedBytes);
    return ms.ToArray();
  }

  // ----- tests -----

  [Test, Category("HappyPath")]
  public void Magic_StartsWithMATLAB() {
    var d = new MatlabFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("MATLAB"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalFile() {
    var mat = BuildScalarDoubleMat("x", 3.14);
    using var ms = new MemoryStream(mat);
    var r = new MatlabReader(ms);

    Assert.That(r.Arrays, Has.Count.EqualTo(1));
    Assert.That(r.Arrays[0].Name, Is.EqualTo("x"));
    Assert.That(r.Arrays[0].ClassName, Is.EqualTo("double"));
    Assert.That(r.Arrays[0].Dimensions, Is.EqualTo(new[] { 1, 1 }));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsLittleEndian() {
    var mat = BuildScalarDoubleMat("y", 1.0);
    using var ms = new MemoryStream(mat);
    var r = new MatlabReader(ms);

    Assert.That(r.IsLittleEndian, Is.True);
    Assert.That(r.Version, Is.EqualTo(1));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadHeader() {
    var garbage = new byte[256];
    Array.Fill(garbage, (byte)0xCC);
    using var ms = new MemoryStream(garbage);
    Assert.Throws<InvalidDataException>(() => _ = new MatlabReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesCompressedElement() {
    var mat = BuildCompressedMat("z", 2.5);
    using var ms = new MemoryStream(mat);
    var r = new MatlabReader(ms);

    Assert.That(r.Arrays, Has.Count.EqualTo(1));
    Assert.That(r.Arrays[0].Name, Is.EqualTo("z"));
    Assert.That(r.Arrays[0].ClassName, Is.EqualTo("double"));
    Assert.That(r.Arrays[0].Dimensions, Is.EqualTo(new[] { 1, 1 }));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_PartialOnTruncated() {
    using var ms = new MemoryStream();
    WriteHeader(ms);

    // Write a long-form element header that promises 1024 bytes but only deliver 16.
    var typeBuf = new byte[4];
    var lenBuf = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(typeBuf, MatlabConstants.MiMATRIX);
    BinaryPrimitives.WriteUInt32LittleEndian(lenBuf, 1024);
    ms.Write(typeBuf, 0, 4);
    ms.Write(lenBuf, 0, 4);
    ms.Write(new byte[16], 0, 16);

    ms.Position = 0;
    Assert.DoesNotThrow(() => {
      var r = new MatlabReader(ms);
      Assert.That(r.ParseStatus, Is.EqualTo("partial"));
    });
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var mat = BuildScalarDoubleMat("a", 1.0);
    using var ms = new MemoryStream(mat);
    var entries = new MatlabFormatDescriptor().List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mat"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullMat_PreservesBytes() {
    var mat = BuildScalarDoubleMat("a", 1.0);
    var tmp = Path.Combine(Path.GetTempPath(), "matlab_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(mat);
      new MatlabFormatDescriptor().Extract(ms, tmp, null, ["FULL.mat"]);

      var outPath = Path.Combine(tmp, "FULL.mat");
      Assert.That(File.Exists(outPath), Is.True);
      Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(mat));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsArrayCount() {
    var mat = BuildScalarDoubleMat("foo", 7.0);
    var tmp = Path.Combine(Path.GetTempPath(), "matlab_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(mat);
      new MatlabFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[matlab]"));
      Assert.That(text, Does.Contain("array_count = 1"));
      Assert.That(text, Does.Contain("endian = LE"));
      Assert.That(text, Does.Contain("version = 1"));
      Assert.That(text, Does.Contain("foo:double:[1,1]"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new MatlabFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new MatlabFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Matlab"));
    Assert.That(d.DisplayName, Is.EqualTo("MATLAB MAT v5"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".mat"));
    Assert.That(d.Extensions, Contains.Item(".mat"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("MATLAB"u8.ToArray()));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("matlab-v5"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("MATLAB v5"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
