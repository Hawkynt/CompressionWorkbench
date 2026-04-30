namespace Compression.Tests.Awb;

[TestFixture]
public class AwbTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleEntry() {
    var data = "monster hunter audio cue"u8.ToArray();
    using var ms = new MemoryStream();
    using (var w = new FileFormat.Awb.AwbWriter(ms, leaveOpen: true))
      w.AddEntry(data);
    ms.Position = 0;

    using var r = new FileFormat.Awb.AwbReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].CueId, Is.EqualTo(0u));
    Assert.That(r.Entries[0].Name, Is.EqualTo("cue_00000.bin"));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleEntries() {
    var d10 = new byte[100]; Array.Fill(d10, (byte)0x10);
    var d20 = new byte[ 50]; Array.Fill(d20, (byte)0x20);
    var d30 = new byte[ 33]; Array.Fill(d30, (byte)0x30);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Awb.AwbWriter(ms, leaveOpen: true)) {
      w.AddEntry(10, d10);
      w.AddEntry(20, d20);
      w.AddEntry(30, d30);
    }
    ms.Position = 0;

    using var r = new FileFormat.Awb.AwbReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(3));
    Assert.That(r.Entries[0].CueId, Is.EqualTo(10u));
    Assert.That(r.Entries[1].CueId, Is.EqualTo(20u));
    Assert.That(r.Entries[2].CueId, Is.EqualTo(30u));
    Assert.That(r.Entries[0].Name, Is.EqualTo("cue_00010.bin"));
    Assert.That(r.Entries[1].Name, Is.EqualTo("cue_00020.bin"));
    Assert.That(r.Entries[2].Name, Is.EqualTo("cue_00030.bin"));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(d10));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(d20));
    Assert.That(r.Extract(r.Entries[2]), Is.EqualTo(d30));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_AlignmentRespected() {
    // Two entries — the second's offset must land on an alignment boundary.
    var first  = new byte[37];   // length not divisible by 0x20 to force padding
    var second = new byte[64];
    Array.Fill(first,  (byte)0xA1);
    Array.Fill(second, (byte)0xB2);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Awb.AwbWriter(ms, leaveOpen: true)) {
      w.AddEntry(first);
      w.AddEntry(second);
    }
    ms.Position = 0;

    using var r = new FileFormat.Awb.AwbReader(ms);
    Assert.That(r.Alignment, Is.EqualTo(0x20u));
    Assert.That(r.Entries, Has.Count.EqualTo(2));

    // Both data offsets must be on alignment boundaries.
    Assert.That(r.Entries[0].Offset % r.Alignment, Is.EqualTo(0));
    Assert.That(r.Entries[1].Offset % r.Alignment, Is.EqualTo(0));

    // And the second entry must start strictly after the first ends, padded up to the boundary.
    var firstEnd = r.Entries[0].Offset + r.Entries[0].Size;
    var expectedSecondStart = (firstEnd + r.Alignment - 1) & ~((long)r.Alignment - 1);
    Assert.That(r.Entries[1].Offset, Is.EqualTo(expectedSecondStart));

    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(first));
    Assert.That(r.Extract(r.Entries[1]), Is.EqualTo(second));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeEntry() {
    var data = new byte[64 * 1024];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 31 + 7);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Awb.AwbWriter(ms, leaveOpen: true))
      w.AddEntry(42, data);
    ms.Position = 0;

    using var r = new FileFormat.Awb.AwbReader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].CueId, Is.EqualTo(42u));
    Assert.That(r.Entries[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[64];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Awb.AwbReader(ms));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadOffsetSize() {
    // Hand-craft a header with an illegal OffsetSize=7.
    var header = new byte[16];
    "AFS2"u8.CopyTo(header.AsSpan(0, 4));
    header[4] = 0x01;          // version
    header[5] = 0x07;          // offset size — invalid (only 2 or 4 are spec)
    header[6] = 0x02;          // id size
    header[7] = 0x00;
    BitConverter.TryWriteBytes(header.AsSpan(8, 4), 0u);     // entryCount=0 (still triggers OffsetSize check first)
    BitConverter.TryWriteBytes(header.AsSpan(12, 4), 0x20u); // alignment

    using var ms = new MemoryStream(header);
    Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Awb.AwbReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Reader_HandlesOffsetSize2() {
    // Build a minimal AFS2 by hand with OffsetSize=2 (the compact Capcom variant).
    // Layout: header(16) + idTable(2*1=2) + offsetTable(2*2=4) + pad-to-0x20 + 8 bytes payload.
    const uint alignment = 0x20;
    var payload = new byte[8];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)(0xC0 + i);

    var tablesEnd = 16 + 2 + 4;                       // 22
    var dataStart = (tablesEnd + (int)alignment - 1) & ~((int)alignment - 1); // 32
    var dataEnd   = dataStart + payload.Length;       // 40

    var ms = new MemoryStream();
    var hdr = new byte[16];
    "AFS2"u8.CopyTo(hdr.AsSpan(0, 4));
    hdr[4] = 0x02;          // version 2 — uses 2-byte offsets
    hdr[5] = 0x02;          // offset size = 2
    hdr[6] = 0x02;          // id size = 2
    hdr[7] = 0x00;
    BitConverter.TryWriteBytes(hdr.AsSpan(8, 4), 1u);          // entryCount
    BitConverter.TryWriteBytes(hdr.AsSpan(12, 4), alignment);
    ms.Write(hdr);

    // Cue-ID table: one UInt16 LE = 7
    ms.Write(BitConverter.GetBytes((ushort)7));

    // Offset table: two UInt16 LE = [dataStart, dataEnd]
    ms.Write(BitConverter.GetBytes((ushort)dataStart));
    ms.Write(BitConverter.GetBytes((ushort)dataEnd));

    // Pad to dataStart
    while (ms.Position < dataStart)
      ms.WriteByte(0);

    ms.Write(payload);
    ms.Position = 0;

    using var r = new FileFormat.Awb.AwbReader(ms);
    Assert.That(r.OffsetSize, Is.EqualTo((byte)2));
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Entries[0].CueId, Is.EqualTo(7u));
    Assert.That(r.Entries[0].Name, Is.EqualTo("cue_00007.bin"));
    Assert.That(r.Entries[0].Offset, Is.EqualTo(dataStart));
    Assert.That(r.Entries[0].Size, Is.EqualTo(payload.Length));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Magic_IsAfs2() {
    var d = new FileFormat.Awb.AwbFormatDescriptor();
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo(new byte[] { 0x41, 0x46, 0x53, 0x32 }));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Awb.AwbFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Awb"));
    Assert.That(d.DisplayName, Is.EqualTo("CRI Audio Wave Bank"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".awb"));
    Assert.That(d.Extensions, Contains.Item(".awb"));
    Assert.That(d.Extensions, Contains.Item(".acb"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("afs2"));
  }
}
