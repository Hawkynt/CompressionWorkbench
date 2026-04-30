#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileFormat.Arsc;

namespace Compression.Tests.Arsc;

[TestFixture]
public class ArscTests {

  private static void WriteUInt16Le(MemoryStream ms, ushort value) {
    ms.WriteByte((byte)(value & 0xFF));
    ms.WriteByte((byte)((value >> 8) & 0xFF));
  }

  private static void WriteUInt32Le(MemoryStream ms, uint value) {
    ms.WriteByte((byte)(value & 0xFF));
    ms.WriteByte((byte)((value >> 8) & 0xFF));
    ms.WriteByte((byte)((value >> 16) & 0xFF));
    ms.WriteByte((byte)((value >> 24) & 0xFF));
  }

  private static byte[] BuildMinimalArsc(uint declaredPackageCount = 0) {
    using var ms = new MemoryStream();
    WriteUInt16Le(ms, ArscConstants.ResTableType);
    WriteUInt16Le(ms, ArscConstants.ResTableHeaderSize);
    WriteUInt32Le(ms, (uint)ArscConstants.ResTableHeaderSize);
    WriteUInt32Le(ms, declaredPackageCount);
    return ms.ToArray();
  }

  private static byte[] BuildArscWithStringPool(uint stringCount) {
    using var pool = new MemoryStream();
    WriteUInt16Le(pool, ArscConstants.ResStringPoolType);
    WriteUInt16Le(pool, 28);
    WriteUInt32Le(pool, 28);
    WriteUInt32Le(pool, stringCount);
    WriteUInt32Le(pool, 0);
    WriteUInt32Le(pool, 0);
    WriteUInt32Le(pool, 28);
    WriteUInt32Le(pool, 0);
    var poolBytes = pool.ToArray();

    using var ms = new MemoryStream();
    WriteUInt16Le(ms, ArscConstants.ResTableType);
    WriteUInt16Le(ms, ArscConstants.ResTableHeaderSize);
    WriteUInt32Le(ms, (uint)(ArscConstants.ResTableHeaderSize + poolBytes.Length));
    WriteUInt32Le(ms, 0);
    ms.Write(poolBytes, 0, poolBytes.Length);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Magic_IsResTable() {
    var d = new ArscFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x03, 0x00, 0x0C, 0x00 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesMinimal() {
    var data = BuildMinimalArsc();
    using var ms = new MemoryStream(data);
    var r = new ArscReader(ms);
    Assert.That(r.PackageCount, Is.EqualTo(0u));
    Assert.That(r.GlobalStringCount, Is.EqualTo(0u));
    Assert.That(r.Packages, Is.Empty);
    Assert.That(r.TotalTypeCount, Is.EqualTo(0));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("HappyPath")]
  public void Reader_ParsesStringPool() {
    var data = BuildArscWithStringPool(stringCount: 7);
    using var ms = new MemoryStream(data);
    var r = new ArscReader(ms);
    Assert.That(r.GlobalStringCount, Is.EqualTo(7u));
    Assert.That(r.ParseStatus, Is.EqualTo("full"));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var garbage = new byte[64];
    Array.Fill(garbage, (byte)0xCC);
    using var ms = new MemoryStream(garbage);
    Assert.Throws<InvalidDataException>(() => _ = new ArscReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadHeaderSize() {
    using var ms = new MemoryStream();
    WriteUInt16Le(ms, ArscConstants.ResTableType);
    WriteUInt16Le(ms, 2);
    WriteUInt32Le(ms, 12);
    WriteUInt32Le(ms, 0);
    ms.Position = 0;
    Assert.Throws<InvalidDataException>(() => _ = new ArscReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_PartialOnTruncated() {
    using var ms = new MemoryStream();
    WriteUInt16Le(ms, ArscConstants.ResTableType);
    WriteUInt16Le(ms, ArscConstants.ResTableHeaderSize);
    WriteUInt32Le(ms, 999);
    WriteUInt32Le(ms, 0);
    ms.Position = 0;
    var r = new ArscReader(ms);
    Assert.That(r.ParseStatus, Is.EqualTo("partial"));
  }

  [Test, Category("HappyPath")]
  public void List_ReturnsTwoEntries() {
    var data = BuildMinimalArsc();
    using var ms = new MemoryStream(data);
    var entries = new ArscFormatDescriptor().List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(2));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.arsc"));
    Assert.That(entries[1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_FullArsc_PreservesBytes() {
    var data = BuildMinimalArsc();
    var tmp = Path.Combine(Path.GetTempPath(), "arsc_full_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new ArscFormatDescriptor().Extract(ms, tmp, null, ["FULL.arsc"]);

      var outPath = Path.Combine(tmp, "FULL.arsc");
      Assert.That(File.Exists(outPath), Is.True);
      var written = File.ReadAllBytes(outPath);
      Assert.That(written, Is.EqualTo(data));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsPackageCount() {
    var data = BuildMinimalArsc(declaredPackageCount: 0);
    var tmp = Path.Combine(Path.GetTempPath(), "arsc_meta_test_" + Guid.NewGuid().ToString("N"));
    try {
      using var ms = new MemoryStream(data);
      new ArscFormatDescriptor().Extract(ms, tmp, null, ["metadata.ini"]);

      var iniPath = Path.Combine(tmp, "metadata.ini");
      Assert.That(File.Exists(iniPath), Is.True);
      var text = File.ReadAllText(iniPath, Encoding.UTF8);
      Assert.That(text, Does.Contain("[arsc]"));
      Assert.That(text, Does.Contain("package_count = 0"));
      Assert.That(text, Does.Contain("global_string_count = 0"));
      Assert.That(text, Does.Contain("type_count = 0"));
      Assert.That(text, Does.Contain("parse_status = full"));
    } finally {
      if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Capabilities_DoesNotIncludeCanCreate() {
    var d = new ArscFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo((FormatCapabilities)0));
    Assert.That(d, Is.Not.InstanceOf<IArchiveCreatable>());
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.SupportsMultipleEntries), Is.True);
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new ArscFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Arsc"));
    Assert.That(d.DisplayName, Is.EqualTo("Android Resources (ARSC)"));
    Assert.That(d.Category, Is.EqualTo(FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".arsc"));
    Assert.That(d.Extensions, Contains.Item(".arsc"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x03, 0x00, 0x0C, 0x00 }));
    Assert.That(d.MagicSignatures[0].Offset, Is.EqualTo(0));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("arsc"));
    Assert.That(d.Methods[0].DisplayName, Is.EqualTo("ARSC"));
    Assert.That(d.Family, Is.EqualTo(AlgorithmFamily.Archive));
  }
}
