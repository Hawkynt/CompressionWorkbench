#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileFormat.MatlabV4;

namespace Compression.Tests.MatlabV4;

[TestFixture]
public class MatlabV4Tests {

  // ----- synthesis helpers -----

  /// <summary>Packs a (M,O,P,T) tuple into the MOPT decimal integer used as the type code.</summary>
  private static uint PackMopt(uint m, uint o, uint p, uint t) => (m * 1000) + (o * 100) + (p * 10) + t;

  private static void WriteUInt32(Stream s, uint v, bool littleEndian) {
    var buf = new byte[4];
    if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
    else BinaryPrimitives.WriteUInt32BigEndian(buf, v);
    s.Write(buf, 0, 4);
  }

  /// <summary>
  /// Writes a single MAT v4 record: 20-byte header + null-terminated name + (rows*cols*element_size)
  /// real bytes + (optional) imaginary bytes.
  /// </summary>
  private static void WriteRecord(Stream s, bool littleEndian, uint m, uint p, uint t, uint rows, uint cols, string name, byte[] realData, byte[]? imagData = null) {
    var nameBytes = Encoding.ASCII.GetBytes(name);
    var nameWithNul = new byte[nameBytes.Length + 1];
    Array.Copy(nameBytes, nameWithNul, nameBytes.Length);
    nameWithNul[^1] = 0;

    WriteUInt32(s, PackMopt(m, 0, p, t), littleEndian);
    WriteUInt32(s, rows, littleEndian);
    WriteUInt32(s, cols, littleEndian);
    WriteUInt32(s, imagData != null ? 1u : 0u, littleEndian);
    WriteUInt32(s, (uint)nameWithNul.Length, littleEndian);
    s.Write(nameWithNul, 0, nameWithNul.Length);
    s.Write(realData, 0, realData.Length);
    if (imagData != null) s.Write(imagData, 0, imagData.Length);
  }

  /// <summary>Builds a minimal 1x1 double scalar record (MOPT = 0 LE, name "x", value 3.14).</summary>
  private static byte[] BuildMinimalLE(string name = "x", double value = 3.14) {
    using var ms = new MemoryStream();
    var data = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(data, value);
    WriteRecord(ms, littleEndian: true, m: MatlabV4Constants.MachineLE, p: MatlabV4Constants.PrecisionDouble, t: MatlabV4Constants.TypeFullNumeric,
      rows: 1, cols: 1, name: name, realData: data);
    return ms.ToArray();
  }

  /// <summary>Builds a minimal 1x1 double scalar record but in big-endian host format (M=1).</summary>
  private static byte[] BuildMinimalBE(string name = "x", double value = 3.14) {
    using var ms = new MemoryStream();
    var data = new byte[8];
    BinaryPrimitives.WriteDoubleBigEndian(data, value);
    WriteRecord(ms, littleEndian: false, m: MatlabV4Constants.MachineBE, p: MatlabV4Constants.PrecisionDouble, t: MatlabV4Constants.TypeFullNumeric,
      rows: 1, cols: 1, name: name, realData: data);
    return ms.ToArray();
  }

  // ----- tests -----

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalLE() {
    var bytes = BuildMinimalLE();
    using var ms = new MemoryStream(bytes);
    var r = new MatlabV4Reader(ms);

    Assert.That(r.Variables, Has.Count.EqualTo(1));
    Assert.That(r.Variables[0].Name, Is.EqualTo("x"));
    Assert.That(r.Variables[0].TypeName, Is.EqualTo("double"));
    Assert.That(r.Variables[0].Rows, Is.EqualTo(1u));
    Assert.That(r.Variables[0].Cols, Is.EqualTo(1u));
    Assert.That(r.Variables[0].IsImaginary, Is.False);
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimalBE() {
    var bytes = BuildMinimalBE();
    using var ms = new MemoryStream(bytes);
    var r = new MatlabV4Reader(ms);

    Assert.That(r.Variables, Has.Count.EqualTo(1));
    Assert.That(r.Variables[0].Name, Is.EqualTo("x"));
    Assert.That(r.Variables[0].TypeName, Is.EqualTo("double"));
    Assert.That(r.Variables[0].Rows, Is.EqualTo(1u));
    Assert.That(r.Variables[0].Cols, Is.EqualTo(1u));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_DetectsEndian() {
    var le = BuildMinimalLE();
    using (var ms = new MemoryStream(le)) {
      var r = new MatlabV4Reader(ms);
      Assert.That(r.IsLittleEndian, Is.True);
      Assert.That(r.Machine, Is.EqualTo(MatlabV4Constants.MachineLE));
    }

    var be = BuildMinimalBE();
    using (var ms = new MemoryStream(be)) {
      var r = new MatlabV4Reader(ms);
      Assert.That(r.IsLittleEndian, Is.False);
      Assert.That(r.Machine, Is.EqualTo(MatlabV4Constants.MachineBE));
    }
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsImpossibleHeader() {
    using var ms = new MemoryStream();
    // M=99, which exceeds the maximum machine code of 4 — invalid in both endiannesses.
    WriteUInt32(ms, 99000u, littleEndian: true);
    WriteUInt32(ms, 1u, littleEndian: true);
    WriteUInt32(ms, 1u, littleEndian: true);
    WriteUInt32(ms, 0u, littleEndian: true);
    WriteUInt32(ms, 2u, littleEndian: true);
    ms.Write(new byte[] { (byte)'x', 0 }, 0, 2);
    ms.Write(new byte[8], 0, 8);
    ms.Position = 0;

    Assert.Throws<InvalidDataException>(() => _ = new MatlabV4Reader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_PartialOnTruncated() {
    // Valid header promising 100x100 doubles (80,000 data bytes) but EOF arrives immediately.
    using var ms = new MemoryStream();
    WriteUInt32(ms, PackMopt(0, 0, 0, 0), littleEndian: true);
    WriteUInt32(ms, 100u, littleEndian: true);
    WriteUInt32(ms, 100u, littleEndian: true);
    WriteUInt32(ms, 0u, littleEndian: true);
    WriteUInt32(ms, 2u, littleEndian: true);
    ms.Write(new byte[] { (byte)'a', 0 }, 0, 2);
    // No data bytes — promised 80,000 but stream ends here.
    ms.Position = 0;

    MatlabV4Reader? r = null;
    Assert.DoesNotThrow(() => r = new MatlabV4Reader(ms));
    Assert.That(r, Is.Not.Null);
    Assert.That(r!.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesMultipleVariables() {
    using var ms = new MemoryStream();
    var d1 = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(d1, 1.0);
    var d2 = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(d2, 2.0);
    var d3 = new byte[8];
    BinaryPrimitives.WriteDoubleLittleEndian(d3, 3.0);
    WriteRecord(ms, true, MatlabV4Constants.MachineLE, MatlabV4Constants.PrecisionDouble, MatlabV4Constants.TypeFullNumeric, 1, 1, "alpha", d1);
    WriteRecord(ms, true, MatlabV4Constants.MachineLE, MatlabV4Constants.PrecisionDouble, MatlabV4Constants.TypeFullNumeric, 1, 1, "beta", d2);
    WriteRecord(ms, true, MatlabV4Constants.MachineLE, MatlabV4Constants.PrecisionDouble, MatlabV4Constants.TypeFullNumeric, 1, 1, "gamma", d3);
    ms.Position = 0;

    var r = new MatlabV4Reader(ms);
    Assert.That(r.Variables, Has.Count.EqualTo(3));
    Assert.That(r.Variables[0].Name, Is.EqualTo("alpha"));
    Assert.That(r.Variables[1].Name, Is.EqualTo("beta"));
    Assert.That(r.Variables[2].Name, Is.EqualTo("gamma"));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesTextType() {
    // Text record: T=1, P=5 (uint8 ASCII codes), 1x5 matrix with name "msg".
    using var ms = new MemoryStream();
    var data = "hello"u8.ToArray();
    WriteRecord(ms, true, MatlabV4Constants.MachineLE, MatlabV4Constants.PrecisionUInt8, MatlabV4Constants.TypeText, 1, 5, "msg", data);
    ms.Position = 0;

    var r = new MatlabV4Reader(ms);
    Assert.That(r.Variables, Has.Count.EqualTo(1));
    Assert.That(r.Variables[0].Name, Is.EqualTo("msg"));
    Assert.That(r.Variables[0].TypeName, Is.EqualTo("text"));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var bytes = BuildMinimalLE();
    using var ms = new MemoryStream(bytes);
    var entries = new MatlabV4FormatDescriptor().List(ms, null);

    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.mat"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullMat_PreservesBytes() {
    var bytes = BuildMinimalLE();
    var tmp = Path.Combine(Path.GetTempPath(), "matlabv4_full_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new MatlabV4FormatDescriptor().Extract(ms, tmp, null, ["FULL.mat"]);

      var outPath = Path.Combine(tmp, "FULL.mat");
      Assert.That(File.Exists(outPath), Is.True);
      Assert.That(File.ReadAllBytes(outPath), Is.EqualTo(bytes));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsVariables() {
    var bytes = BuildMinimalLE("foo", 7.0);
    var tmp = Path.Combine(Path.GetTempPath(), "matlabv4_meta_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(bytes);
      new MatlabV4FormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[matlab_v4]"));
      Assert.That(text, Does.Contain("endian = LE"));
      Assert.That(text, Does.Contain("variable_count = 1"));
      Assert.That(text, Does.Contain("foo:double:[1,1]"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new MatlabV4FormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new MatlabV4FormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("MatlabV4"));
    Assert.That(d.DisplayName, Is.EqualTo("MATLAB MAT v4"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".mat"));
    Assert.That(d.Extensions, Contains.Item(".mat"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("matlab-v4"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("MATLAB v4"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
    Assert.That(d.Description, Is.EqualTo("MATLAB MAT v4 file (read-only pseudo-archive)"));
  }
}
