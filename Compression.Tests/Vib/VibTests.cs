using System.Text;
using Compression.Core.Streams;
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

  // Build a realistic VIB: an ar archive of descriptor.xml, sig.pkcs7 and a
  // gzip-compressed tar payload holding two files.
  private static byte[] BuildSyntheticVib() {
    // 1) Inner tar with two payload files.
    byte[] tarBytes;
    using (var tarMs = new MemoryStream()) {
      using (var tw = new TarWriter(tarMs, leaveOpen: true)) {
        tw.AddEntry(new TarEntry { Name = "etc/vmware/config", Size = 5 }, "conf\n"u8.ToArray());
        tw.AddEntry(new TarEntry { Name = "bin/hello.sh", Size = BinPayload.Length }, BinPayload);
        tw.Finish();
      }
      tarBytes = tarMs.ToArray();
    }

    // 2) gzip the tar -> the .vgz payload.
    byte[] vgzBytes;
    using (var gzMs = new MemoryStream()) {
      using (var gz = new GzipStream(gzMs, CompressionStreamMode.Compress, leaveOpen: true))
        gz.Write(tarBytes, 0, tarBytes.Length);
      vgzBytes = gzMs.ToArray();
    }

    // 3) ar archive: descriptor.xml, sig.pkcs7, payload member.
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
      Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
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

  [Test, Category("Exceptional")]
  public void NonTarPayload_DoesNotThrow() {
    // ar archive whose payload is neither gzip/xz nor a valid tar.
    using var arMs = new MemoryStream();
    using (var aw = new ArWriter(arMs, leaveOpen: true))
      aw.Write([
        new ArEntry { Name = "descriptor.xml", Data = "<vib/>"u8.ToArray() },
        new ArEntry { Name = "payload.bin", Data = [1, 2, 3, 4, 5, 6, 7, 8] },
      ]);
    var vib = arMs.ToArray();

    var d = new VibFormatDescriptor();
    using var ms = new MemoryStream(vib);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = d.List(ms, null));
    Assert.That(entries.Any(e => e.Name == "descriptor.xml"), Is.True);
  }
}
