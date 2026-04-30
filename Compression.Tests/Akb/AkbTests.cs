using System.Buffers.Binary;
using System.Text;

namespace Compression.Tests.Akb;

[TestFixture]
public class AkbTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleEntry() {
    var data = "voice clip payload"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Akb.AkbWriter(ms, leaveOpen: true))
      w.AddEntry("voice.bin", data);
    ms.Position = 0;

    using var r = new FileFormat.Akb.AkbReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].Name, Is.EqualTo("entry_000.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleEntries() {
    var d1 = new byte[64];
    var d2 = new byte[200];
    var d3 = new byte[37];
    Array.Fill(d1, (byte)0x11);
    Array.Fill(d2, (byte)0x22);
    Array.Fill(d3, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Akb.AkbWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", d1, sampleCount: 100);
      w.AddEntry("b.bin", d2, sampleCount: 200, flags: 1);
      w.AddEntry("c.bin", d3);
    }
    ms.Position = 0;

    using var r = new FileFormat.Akb.AkbReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].Name, Is.EqualTo("entry_000.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("entry_001.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("entry_002.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d1));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d2));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d3));
    Assert.That(r.Entries[0].SampleCount, Is.EqualTo(100u));
    Assert.That(r.Entries[1].SampleCount, Is.EqualTo(200u));
    Assert.That(r.Entries[1].Flags, Is.EqualTo(1u));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_HeaderFields() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Akb.AkbWriter(ms, leaveOpen: true)) {
      w.SampleRate = 48000;
      w.ChannelMode = 2;
      w.LoopStart = 1024;
      w.LoopEnd = 8192;
      w.AddEntry("x.bin", "hello"u8.ToArray());
    }
    ms.Position = 0;

    using var r = new FileFormat.Akb.AkbReader(ms);
    Assert.That(r.SampleRate, Is.EqualTo(48000u));
    Assert.That(r.ChannelMode, Is.EqualTo((byte)2));
    Assert.That(r.LoopStart, Is.EqualTo(1024u));
    Assert.That(r.LoopEnd, Is.EqualTo(8192u));
    Assert.That(r.VersionByte, Is.EqualTo((byte)2));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Akb.AkbReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadVersion() {
    // Synthesize a 40-byte header with valid AKB1 magic but VersionByte=99.
    var buf = new byte[40];
    "AKB1"u8.CopyTo(buf);
    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4, 2), 40); // HeaderSize
    buf[6] = 99; // VersionByte — unsupported
    buf[7] = 1; // ChannelMode
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8, 4), 44100u); // SampleRate
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(20, 4), 40u); // ContentOffset
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(24, 4), 0u);  // ContentSize
    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(28, 4), 0u);  // EntryCount

    using var ms = new MemoryStream(buf);
    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Akb.AkbReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsAkb1() {
    var d = new FileFormat.Akb.AkbFormatDescriptor();
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x41, 0x4B, 0x42, 0x31 }));
  }

  [Test, Category("HappyPath")]
  public void List_IncludesMetadataEntry() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Akb.AkbWriter(ms, leaveOpen: true)) {
      w.AddEntry("a.bin", new byte[10]);
      w.AddEntry("b.bin", new byte[20]);
    }
    ms.Position = 0;

    var d = new FileFormat.Akb.AkbFormatDescriptor();
    var entries = d.List(ms, null);
    Assert.That(entries, Has.Count.EqualTo(3));
    Assert.That(entries[^1].Name, Is.EqualTo("metadata.ini"));
  }

  [Test, Category("HappyPath")]
  public void Extract_Metadata_ContainsSampleRate() {
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Akb.AkbWriter(ms, leaveOpen: true)) {
      w.SampleRate = 32000;
      w.AddEntry("a.bin", new byte[8]);
    }
    ms.Position = 0;

    var tempDir = Path.Combine(Path.GetTempPath(), "akb-test-" + Guid.NewGuid().ToString("N"));
    try {
      var d = new FileFormat.Akb.AkbFormatDescriptor();
      d.Extract(ms, tempDir, null, null);

      var metaPath = Path.Combine(tempDir, "metadata.ini");
      Assert.That(File.Exists(metaPath), Is.True);
      var meta = Encoding.UTF8.GetString(File.ReadAllBytes(metaPath));
      Assert.That(meta, Does.Contain("sample_rate = 32000"));
    } finally {
      if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Akb.AkbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Akb"));
    Assert.That(d.DisplayName, Is.EqualTo("Square Enix AKB"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.Extensions, Contains.Item(".akb"));
    Assert.That(d.DefaultExtension, Is.EqualTo(".akb"));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("akb-v2"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanList), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanExtract), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanTest), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsMultipleEntries), Is.True);
  }
}
