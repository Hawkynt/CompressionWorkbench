using System.Text;
using Compression.Registry;
using FileFormat.Json;
using FileFormat.MessagePack;
using FileFormat.Nrbf;
using FileFormat.Pickle;
using FileFormat.Reg;
using FileFormat.Storable;
using FileFormat.Xml;

namespace Compression.Tests.StructuredPseudoArchives;

[TestFixture]
public sealed class StructuredPseudoArchiveTests {
  [Test]
  public void Json_ProjectsObjectsArraysAndScalars() {
    var descriptor = new JsonFormatDescriptor();
    using var stream = new MemoryStream("{\"users\":[{\"name\":\"Ada\",\"active\":true}],\"count\":1}"u8.ToArray());
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "users"), Is.True);
      Assert.That(entries.Any(e => e.Name == "users/[000000]/name" && e.Kind == "string"), Is.True);
      Assert.That(entries.Any(e => e.Name == "users/[000000]/active" && e.Kind == "boolean"), Is.True);
      Assert.That(entries.Any(e => e.Name == "count" && e.Kind == "number"), Is.True);
    });
  }

  [Test]
  public void Json_EscapesWindowsDeviceNames() {
    var descriptor = new JsonFormatDescriptor();
    using var stream = new MemoryStream("{\"CON\":1,\"LPT1.txt\":2}"u8.ToArray());
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "%43ON"), Is.True);
      Assert.That(entries.Any(e => e.Name == "%4CPT1.txt"), Is.True);
    });
  }

  [Test]
  public void Xml_ProjectsAttributesAndRepeatedElements() {
    var descriptor = new XmlFormatDescriptor();
    using var stream = new MemoryStream("<root id=\"7\"><item>A</item><item>B</item></root>"u8.ToArray());
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "root"), Is.True);
      Assert.That(entries.Any(e => e.Name == "root/%40id"), Is.True);
      Assert.That(entries.Any(e => e.Name == "root/item~000000"), Is.True);
      Assert.That(entries.Any(e => e.Name == "root/item~000001"), Is.True);
    });
  }

  [Test]
  public void Xml_RejectsDtds() {
    var descriptor = new XmlFormatDescriptor();
    using var stream = new MemoryStream("<!DOCTYPE root [<!ENTITY x \"boom\">]><root>&x;</root>"u8.ToArray());
    Assert.That(() => descriptor.List(stream, null), Throws.TypeOf<InvalidDataException>());
  }

  [Test]
  public void MessagePack_ParsesSpecTypeFamilies() {
    var descriptor = new MessagePackFormatDescriptor();
    byte[] bytes = [0x82, 0xa1, (byte)'a', 0x01, 0xa1, (byte)'b', 0x92, 0xc3, 0xc0];
    using var stream = new MemoryStream(bytes);
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "a" && e.Kind == "positive-fixint"), Is.True);
      Assert.That(entries.Any(e => e.IsDirectory && e.Name == "b"), Is.True);
      Assert.That(entries.Any(e => e.Name == "b/[000000]" && e.Kind == "bool"), Is.True);
      Assert.That(entries.Any(e => e.Name == "b/[000001]" && e.Kind == "nil"), Is.True);
    });
  }

  [Test]
  public void Reg_ProjectsKeysAndTypedValues() {
    var descriptor = new RegFormatDescriptor();
    var text = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\Acme]\r\n\"Name\"=\"Widget\"\r\n\"Flags\"=dword:0000002a\r\n\"Blob\"=hex:00,ff,10\r\n";
    using var stream = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray());
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name.EndsWith("/Name", StringComparison.Ordinal) && e.Kind == "REG_SZ"), Is.True);
      Assert.That(entries.Any(e => e.Name.EndsWith("/Flags", StringComparison.Ordinal) && e.Kind == "REG_DWORD"), Is.True);
      Assert.That(entries.Any(e => e.Name.EndsWith("/Blob", StringComparison.Ordinal) && e.Kind == "REG_BINARY"), Is.True);
    });
  }

  [Test]
  public void Pickle_ReduceIsDataNotExecution() {
    var descriptor = new PickleFormatDescriptor();
    byte[] pickle = [
      0x80, 0x02,
      (byte)'c', (byte)'o', (byte)'s', (byte)'\n', (byte)'s', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'\n',
      (byte)'X', 0x04, 0x00, 0x00, 0x00, (byte)'c', (byte)'a', (byte)'l', (byte)'c',
      0x85, (byte)'R', (byte)'.',
    ];
    using var stream = new MemoryStream(pickle);
    var entries = descriptor.List(stream, null);
    Assert.That(entries.Any(e => e.Name == "%24callable" && e.Kind == "pickle-global"), Is.True);
  }

  [Test]
  public void Pickle_AppendTargetsTheListBelowTheValue() {
    var descriptor = new PickleFormatDescriptor();
    byte[] pickle = [(byte)']', (byte)'I', (byte)'1', (byte)'\n', (byte)'a', (byte)'.'];
    using var stream = new MemoryStream(pickle);
    var entries = descriptor.List(stream, null);
    Assert.That(entries.Any(e => e.Name == "[000000]" && e.Kind == "int"), Is.True);
  }

  [Test]
  public void Storable_ParsesPerlNetworkOrderVector() {
    var descriptor = new StorableFormatDescriptor();
    var bytes = Convert.FromHexString("050B03000000020882000000016208810000000161");
    using var stream = new MemoryStream(bytes);
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "a" && e.Kind == "integer"), Is.True);
      Assert.That(entries.Any(e => e.Name == "b" && e.Kind == "integer"), Is.True);
    });
  }

  [Test]
  public void Storable_CreateMatchesNstoreOracleForNestedBinaryFile() {
    var descriptor = new StorableFormatDescriptor();
    byte[] payload = [0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a];
    using var output = new MemoryStream();
    descriptor.Create(output, [ArchiveInputInfo.InMemory("dir/file.bin", payload)], new FormatCreateOptions());
    var expected = Convert.FromHexString(
      "70737430050B03000000010403000000010A0600017F80FF0A0000000866696C652E62696E00000003646972");
    Assert.That(output.ToArray(), Is.EqualTo(expected));
  }

  [Test]
  public void Nrbf_ParsesBinaryObjectStringWithoutBinaryFormatter() {
    var descriptor = new NrbfFormatDescriptor();
    byte[] streamBytes = [
      0x00, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x06, 0x01, 0x00, 0x00, 0x00, 0x05, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
      0x0b,
    ];
    using var stream = new MemoryStream(streamBytes);
    var entries = descriptor.List(stream, null);
    Assert.Multiple(() => {
      Assert.That(entries.Any(e => e.Name == "%24root" && e.Kind == "object-reference"), Is.True);
      Assert.That(entries.Any(e => e.Name == "objects/%401" && e.Kind == "string"), Is.True);
    });
  }

  [Test]
  public void Nrbf_DecimalPrimitiveUsesLengthPrefixedString() {
    var descriptor = new NrbfFormatDescriptor();
    byte[] streamBytes = [
      0x00, 0x01, 0x00, 0x00, 0x00, 0xff, 0xff, 0xff, 0xff, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
      0x0c, 0x01, 0x00, 0x00, 0x00, 0x01, (byte)'A',
      0x05, 0x01, 0x00, 0x00, 0x00, 0x01, (byte)'T', 0x01, 0x00, 0x00, 0x00, 0x01, (byte)'D',
      0x00, 0x05, 0x01, 0x00, 0x00, 0x00,
      0x04, (byte)'1', (byte)'2', (byte)'.', (byte)'5',
      0x0b,
    ];
    using var stream = new MemoryStream(streamBytes);
    var entries = descriptor.List(stream, null);
    Assert.That(entries.Any(e => e.Name == "objects/%401/D" && e.Kind == "decimal"), Is.True);
  }

  [TestCaseSource(nameof(CreatableDescriptors))]
  public void CreatableStructuredFormats_RoundTripArbitraryBytes(object descriptorObject) {
    var creator = (IArchiveCreatable)descriptorObject;
    var archive = (IArchiveFormatOperations)descriptorObject;
    var direct = (IArchiveInMemoryExtract)descriptorObject;
    byte[] expected = [0x00, 0x01, 0x7f, 0x80, 0xff, 0x0a];
    using var encoded = new MemoryStream();
    creator.Create(encoded, [ArchiveInputInfo.InMemory("dir/file.bin", expected)], new FormatCreateOptions());
    encoded.Position = 0;
    var entries = archive.List(encoded, null);
    var file = entries.Single(e => !e.IsDirectory && Path.GetFileName(e.Name).Equals("file.bin", StringComparison.Ordinal));
    encoded.Position = 0;
    using var decoded = new MemoryStream();
    direct.ExtractEntry(encoded, file.Name, decoded, null);
    Assert.That(decoded.ToArray(), Is.EqualTo(expected));
  }

  private static IEnumerable<object> CreatableDescriptors() {
    yield return new JsonFormatDescriptor();
    yield return new XmlFormatDescriptor();
    yield return new MessagePackFormatDescriptor();
    yield return new RegFormatDescriptor();
    yield return new PickleFormatDescriptor();
    yield return new StorableFormatDescriptor();
  }
}
