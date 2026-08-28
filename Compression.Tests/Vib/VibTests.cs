using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Compression.Core.Streams;
using Compression.Registry;
using FileFormat.Ar;
using FileFormat.Gzip;
using FileFormat.Tar;
using FileFormat.Vib;

namespace Compression.Tests.Vib;

[TestFixture]
public class VibTests {

  private static readonly byte[] BinPayload = Encoding.ASCII.GetBytes("#!/bin/sh\necho hello vib\n");
  private const string DescriptorXml =
    "<vib version=\"5.0\"><name>cwb-test</name><version>1.0.0</version></vib>";

  // Build a realistic legacy/minimal VIB: an ar archive of descriptor.xml,
  // sig.pkcs7 and a gzip-compressed tar payload holding two files.
  private static byte[] BuildSyntheticVib() {
    byte[] tarBytes;
    using (var tarMs = new MemoryStream()) {
      using (var tw = new TarWriter(tarMs, leaveOpen: true)) {
        tw.AddEntry(new TarEntry { Name = "etc/vmware/config", Size = 5 }, "conf\n"u8.ToArray());
        tw.AddEntry(new TarEntry { Name = "bin/hello.sh", Size = BinPayload.Length }, BinPayload);
        tw.Finish();
      }
      tarBytes = tarMs.ToArray();
    }

    byte[] vgzBytes;
    using (var gzMs = new MemoryStream()) {
      using (var gz = new GzipStream(gzMs, CompressionStreamMode.Compress, leaveOpen: true))
        gz.Write(tarBytes, 0, tarBytes.Length);
      vgzBytes = gzMs.ToArray();
    }

    using var arMs = new MemoryStream();
    using (var aw = new ArWriter(arMs, leaveOpen: true)) {
      aw.Write([
        new ArEntry { Name = "descriptor.xml", Data = Encoding.UTF8.GetBytes(DescriptorXml) },
        new ArEntry { Name = "sig.pkcs7", Data = [0x30, 0x82, 0x00, 0x00] },
        new ArEntry { Name = "cwb-test", Data = vgzBytes },
      ]);
    }
    return arMs.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new VibFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Id, Is.EqualTo("Vib"));
      Assert.That(d.Extensions, Contains.Item(".vib"));
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanExtract), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
      Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False,
        "CommunitySupported creation must not imply that signed existing VIBs can be modified safely.");
      Assert.That(d.Methods.Select(m => m.Name), Contains.Item("tgz"));
      Assert.That(d, Is.InstanceOf<IFormatOptionsSchema>());
    });
  }

  [Test, Category("HappyPath")]
  public void List_SurfacesDescriptorSignatureAndPayloadTree() {
    var vib = BuildSyntheticVib();
    var d = new VibFormatDescriptor();
    using var ms = new MemoryStream(vib);
    var entries = d.List(ms, null);
    var names = entries.Select(e => e.Name).ToList();
    Assert.Multiple(() => {
      Assert.That(names, Contains.Item("descriptor.xml"));
      Assert.That(names, Contains.Item("sig.pkcs7"));
      Assert.That(names, Contains.Item("payload/etc/vmware/config"));
      Assert.That(names, Contains.Item("payload/bin/hello.sh"));
    });
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesDescriptorAndDecompressedPayload() {
    var vib = BuildSyntheticVib();
    var d = new VibFormatDescriptor();
    var dir = Path.Combine(Path.GetTempPath(), "vib_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      using var ms = new MemoryStream(vib);
      d.Extract(ms, dir, null, null);

      var xml = File.ReadAllText(Path.Combine(dir, "descriptor.xml"));
      Assert.That(xml, Does.Contain("cwb-test"));

      var script = File.ReadAllBytes(Path.Combine(dir, "payload", "bin", "hello.sh"));
      Assert.That(script, Is.EqualTo(BinPayload));

      Assert.That(File.Exists(Path.Combine(dir, "sig.pkcs7")), Is.True);
    } finally {
      Directory.Delete(dir, true);
    }
  }

  [Test, Category("Boundary")]
  public void Reader_DecompressesPayloadTree() {
    var vib = BuildSyntheticVib();
    using var ms = new MemoryStream(vib);
    using var r = new VibReader(ms);
    Assert.Multiple(() => {
      Assert.That(r.DescriptorXml, Is.Not.Null);
      Assert.That(r.Signature, Is.Not.Null);
      Assert.That(r.PayloadMemberName, Is.EqualTo("cwb-test"));
      Assert.That(r.ReadPayloadEntries().Count, Is.EqualTo(2));
    });
  }

  [Test, Category("HappyPath")]
  public void Create_CommunitySupportedTgz_RoundTripsAndCarriesEsxi8Checksums() {
    var d = new VibFormatDescriptor();
    var inputs = new ArchiveInputInfo[] {
      ArchiveInputInfo.InMemory("etc/vmware/config", "conf\n"u8),
      ArchiveInputInfo.InMemory("bin/hello.sh", BinPayload),
      new("empty", "empty", true),
      ArchiveInputInfo.InMemory("share/ü.txt", "unicode"u8),
    };
    var options = new FormatCreateOptions {
      MethodName = "tgz",
      FormatSpecific = new Dictionary<string, string> {
        ["Name"] = "cwb-test",
        ["Version"] = "2.0.0-0.0.1",
        ["Vendor"] = "Hawkynt",
        ["Summary"] = "VIB test",
        ["Description"] = "CompressionWorkbench VIB round-trip",
        ["ReleaseDate"] = "2026-08-28T12:34:56Z",
        ["FileMode"] = "0755",
        ["DirectoryMode"] = "0750",
      },
    };

    using var image = new MemoryStream();
    d.Create(image, inputs, options);
    var bytes = image.ToArray();

    using var stream = new MemoryStream(bytes);
    using var reader = new VibReader(stream);
    var descriptorBytes = reader.DescriptorXml;
    var signature = reader.Signature;
    var payload = reader.PayloadRaw;
    Assert.That(descriptorBytes, Is.Not.Null);
    Assert.That(signature, Is.Not.Null.And.Empty,
      "CommunitySupported VIBs require the signature member to exist but remain empty.");
    Assert.That(payload, Is.Not.Null);
    Assert.That(payload![..2], Is.EqualTo(new byte[] { 0x1F, 0x8B }));

    var tar = reader.DecompressPayload();
    var root = XElement.Parse(Encoding.UTF8.GetString(descriptorBytes!));
    var payloadElement = root.Descendants("payload").Single();
    var checksums = payloadElement.Elements("checksum").ToArray();
    var fileList = root.Element("file-list")!.Elements("file").Select(e => e.Value).ToArray();

    Assert.Multiple(() => {
      Assert.That(root.Element("name")!.Value, Is.EqualTo("cwb-test"));
      Assert.That(root.Element("version")!.Value, Is.EqualTo("2.0.0-0.0.1"));
      Assert.That(root.Element("vendor")!.Value, Is.EqualTo("Hawkynt"));
      Assert.That(root.Element("acceptance-level")!.Value, Is.EqualTo("community"));
      Assert.That(root.Element("release-date")!.Value, Is.EqualTo("2026-08-28T12:34:56"));
      Assert.That(payloadElement.Attribute("name")!.Value, Is.EqualTo(VibWriter.PayloadName));
      Assert.That(payloadElement.Attribute("type")!.Value, Is.EqualTo("tgz"));
      Assert.That(long.Parse(payloadElement.Attribute("size")!.Value), Is.EqualTo(payload.LongLength));
      Assert.That(fileList, Is.EqualTo(new[] { "bin/hello.sh", "etc/vmware/config", "share/ü.txt" }));
      Assert.That(checksums, Has.Length.EqualTo(3));
      Assert.That(checksums.Single(e => e.Attribute("checksum-type")!.Value == "sha-256" && e.Attribute("verify-process") is null).Value,
        Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(payload))));
      Assert.That(checksums.Single(e => e.Attribute("checksum-type")!.Value == "sha-256" && (string?)e.Attribute("verify-process") == "gunzip").Value,
        Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(tar))));
      Assert.That(checksums.Single(e => e.Attribute("checksum-type")!.Value == "sha-1").Value,
        Is.EqualTo(Convert.ToHexStringLower(SHA1.HashData(tar))));
    });

    var payloadEntries = reader.ReadPayloadEntries();
    Assert.Multiple(() => {
      Assert.That(payloadEntries.Any(e => e.Path.TrimEnd('/') == "empty" && e.IsDirectory), Is.True);
      Assert.That(payloadEntries.Single(e => e.Path == "bin/hello.sh").Data, Is.EqualTo(BinPayload));
      Assert.That(payloadEntries.Single(e => e.Path == "share/ü.txt").Data, Is.EqualTo("unicode"u8.ToArray()));
    });
  }

  [Test, Category("HappyPath")]
  public void Create_FromExtractedShape_DropsSyntheticMetadataAndPayloadPrefix() {
    var d = new VibFormatDescriptor();
    var inputs = new ArchiveInputInfo[] {
      ArchiveInputInfo.InMemory("descriptor.xml", "old descriptor"u8),
      ArchiveInputInfo.InMemory("sig.pkcs7", new byte[] { 1, 2, 3 }),
      ArchiveInputInfo.InMemory("payload/etc/a.conf", "a"u8),
      ArchiveInputInfo.InMemory("payload/bin/tool", "b"u8),
    };

    using var image = new MemoryStream();
    d.Create(image, inputs, new FormatCreateOptions());
    image.Position = 0;
    using var r = new VibReader(image);
    var names = r.ReadPayloadEntries().Select(e => e.Path).ToArray();

    Assert.Multiple(() => {
      Assert.That(names, Is.EqualTo(new[] { "bin/tool", "etc/a.conf" }));
      Assert.That(names, Does.Not.Contain("descriptor.xml"));
      Assert.That(names, Does.Not.Contain("sig.pkcs7"));
      Assert.That(names.Any(n => n.StartsWith("payload/", StringComparison.Ordinal)), Is.False);
      Assert.That(r.Signature, Is.Not.Null.And.Empty);
    });
  }

  [Test, Category("Boundary")]
  public void Create_IsDeterministicForSameInputsAndOptions() {
    var d = new VibFormatDescriptor();
    var inputs = new[] { ArchiveInputInfo.InMemory("opt/test.txt", "same"u8) };

    static byte[] Create(VibFormatDescriptor descriptor, IReadOnlyList<ArchiveInputInfo> source) {
      using var ms = new MemoryStream();
      descriptor.Create(ms, source, new FormatCreateOptions());
      return ms.ToArray();
    }

    Assert.That(Create(d, inputs), Is.EqualTo(Create(d, inputs)));
  }

  [Test, Category("Exceptional")]
  public void Reader_RejectsDescriptorChecksumMismatch() {
    var d = new VibFormatDescriptor();
    using var source = new MemoryStream();
    d.Create(source, [ArchiveInputInfo.InMemory("opt/test", "checksum"u8)], new FormatCreateOptions());
    source.Position = 0;

    byte[] payload;
    byte[] tamperedDescriptor;
    using (var r = new VibReader(source)) {
      payload = r.PayloadRaw!;
      var root = XElement.Parse(Encoding.UTF8.GetString(r.DescriptorXml!));
      var compressedHash = root.Descendants("checksum")
        .Single(e => e.Attribute("checksum-type")!.Value == "sha-256" && e.Attribute("verify-process") is null);
      compressedHash.Value = new string('0', 64);
      tamperedDescriptor = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
    }

    using var tampered = new MemoryStream();
    using (var ar = new ArWriter(tampered, leaveOpen: true))
      ar.Write([
        new ArEntry { Name = "descriptor.xml", Data = tamperedDescriptor },
        new ArEntry { Name = "sig.pkcs7", Data = [] },
        new ArEntry { Name = VibWriter.PayloadName, Data = payload },
      ]);
    tampered.Position = 0;
    using var invalid = new VibReader(tampered);
    Assert.Throws<InvalidDataException>(() => invalid.DecompressPayload());
  }

  [Test, Category("Exceptional")]
  public void Create_RejectsUnsafePathsAndUnsupportedEncryption() {
    var d = new VibFormatDescriptor();
    using var unsafeImage = new MemoryStream();
    Assert.Throws<ArgumentException>(() =>
      d.Create(unsafeImage, [ArchiveInputInfo.InMemory("../evil", "x"u8)], new FormatCreateOptions()));

    using var encryptedImage = new MemoryStream();
    Assert.Throws<NotSupportedException>(() =>
      d.Create(encryptedImage, [ArchiveInputInfo.InMemory("ok", "x"u8)], new FormatCreateOptions { Password = "secret" }));
  }

  [Test, Category("Exceptional")]
  public void NonTarPayload_DoesNotThrow() {
    using var arMs = new MemoryStream();
    using (var aw = new ArWriter(arMs, leaveOpen: true))
      aw.Write([
        new ArEntry { Name = "descriptor.xml", Data = "<vib/>"u8.ToArray() },
        new ArEntry { Name = "payload.bin", Data = [1, 2, 3, 4, 5, 6, 7, 8] },
      ]);
    var vib = arMs.ToArray();

    var d = new VibFormatDescriptor();
    using var ms = new MemoryStream(vib);
    List<ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = d.List(ms, null));
    Assert.That(entries.Any(e => e.Name == "descriptor.xml"), Is.True);
  }
}
