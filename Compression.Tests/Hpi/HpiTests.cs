namespace Compression.Tests.Hpi;

[TestFixture]
public class HpiTests {

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_SingleFileSmall() {
    var data = new byte[100];
    for (var i = 0; i < data.Length; ++i)
      data[i] = (byte)(i * 7 + 13);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hpi.HpiWriter(ms, leaveOpen: true))
      w.AddFile("test.txt", data);
    ms.Position = 0;

    var r = new FileFormat.Hpi.HpiReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(files[0].Name, Is.EqualTo("test.txt"));
    Assert.That(files[0].Size, Is.EqualTo(data.Length));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_MultipleFilesNested() {
    var dataA = new byte[300];
    var dataB = new byte[500];
    var dataC = new byte[700];
    Array.Fill(dataA, (byte)0x11);
    Array.Fill(dataB, (byte)0x22);
    Array.Fill(dataC, (byte)0x33);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hpi.HpiWriter(ms, leaveOpen: true)) {
      w.AddFile("a.txt", dataA);
      w.AddFile("sub/b.txt", dataB);
      w.AddFile("sub/sub2/c.txt", dataC);
    }
    ms.Position = 0;

    var r = new FileFormat.Hpi.HpiReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name);

    Assert.That(files.Keys, Is.EquivalentTo(new[] { "a.txt", "sub/b.txt", "sub/sub2/c.txt" }));
    Assert.That(r.Extract(files["a.txt"]), Is.EqualTo(dataA));
    Assert.That(r.Extract(files["sub/b.txt"]), Is.EqualTo(dataB));
    Assert.That(r.Extract(files["sub/sub2/c.txt"]), Is.EqualTo(dataC));

    // SupportsDirectories: directories must be in the entry list as well.
    var dirs = r.Entries.Where(e => e.IsDirectory).Select(e => e.Name).ToList();
    Assert.That(dirs, Contains.Item("sub"));
    Assert.That(dirs, Contains.Item("sub/sub2"));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_LargeFile_MultiChunk() {
    // 200 KB > 3 chunks at 64 KB each (last chunk is partial — exercises the chunk-boundary code).
    var rng = new Random(0xC0FFEE);
    var data = new byte[200 * 1024];
    rng.NextBytes(data);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hpi.HpiWriter(ms, leaveOpen: true))
      w.AddFile("big.bin", data);
    ms.Position = 0;

    var r = new FileFormat.Hpi.HpiReader(ms);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files, Has.Count.EqualTo(1));
    Assert.That(r.Extract(files[0]), Is.EqualTo(data));
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void RoundTrip_HighlyCompressible() {
    var data = new byte[100 * 1024];
    Array.Fill(data, (byte)0xCC);

    using var ms = new MemoryStream();
    using (var w = new FileFormat.Hpi.HpiWriter(ms, leaveOpen: true))
      w.AddFile("flat.bin", data);

    // Compressed archive (header + dir + chunks of 100 KB of constant bytes) should be tiny.
    Assert.That(ms.Length, Is.LessThan(1024), "Highly-compressible payload should fit in <1KB after framing.");

    ms.Position = 0;
    var r = new FileFormat.Hpi.HpiReader(ms);
    Assert.That(r.Extract(r.Entries.First(e => !e.IsDirectory)), Is.EqualTo(data));
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsEncrypted() {
    // Synthesize a 20-byte header with HeaderKey != 0; nothing else has to be valid because the gate
    // fires immediately after the header read.
    var hdr = new byte[20];
    BitConverter.TryWriteBytes(hdr.AsSpan(0, 4), 0x49504148u); // "HAPI"
    BitConverter.TryWriteBytes(hdr.AsSpan(4, 4), 0x00010000u);
    BitConverter.TryWriteBytes(hdr.AsSpan(8, 4), 0u);
    BitConverter.TryWriteBytes(hdr.AsSpan(12, 4), 0x12345678u); // headerKey
    BitConverter.TryWriteBytes(hdr.AsSpan(16, 4), 20u);
    using var ms = new MemoryStream(hdr);
    var ex = Assert.Throws<NotSupportedException>(() => _ = new FileFormat.Hpi.HpiReader(ms));
    Assert.That(ex!.Message, Does.Contain("Encrypted").IgnoreCase.Or.Contain("HeaderKey").IgnoreCase);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsLz77Chunk() {
    // Build a minimal-but-valid HAPI containing one file whose data block declares zlib at the file
    // level but whose first (and only) SQSH chunk advertises LZ77 — exactly the rejection we want.
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    // Header (placeholder — patched after we know the directory offset).
    bw.Write(0x49504148u); // magic
    bw.Write(0x00010000u); // version
    bw.Write(0u);          // dirSize (placeholder)
    bw.Write(0u);          // headerKey = 0 (unencrypted)
    bw.Write(0u);          // dirStart (placeholder)

    // File data block: file size = 16, compression flag = zlib (2). One SQSH chunk with compression = LZ77.
    var dataOff = (uint)ms.Position;
    bw.Write(16u);                                  // file size
    bw.Write((uint)FileFormat.Hpi.HpiConstants.CompressionZlib);

    var fakePayload = new byte[10];
    bw.Write(FileFormat.Hpi.HpiConstants.ChunkMagic);
    bw.Write((byte)2);                              // marker
    bw.Write(FileFormat.Hpi.HpiConstants.CompressionLz77);
    bw.Write((byte)0);                              // not encrypted at chunk level
    bw.Write((uint)fakePayload.Length);             // compressed size
    bw.Write(16u);                                  // decompressed size
    bw.Write(0u);                                   // checksum (ignored)
    bw.Write(fakePayload);

    // Single-entry directory tree: one file "x".
    var dirStart = (uint)ms.Position;
    var entryListOff = dirStart + 8u;
    var nameOff = entryListOff + 9u;

    bw.Write(1u);                                   // entryCount
    bw.Write(entryListOff);                         // entryListOffset

    bw.Write(nameOff);                              // nameOffset
    bw.Write(dataOff);                              // dataOffset
    bw.Write((byte)0);                              // isDirectory = false

    bw.Write((byte)'x');
    bw.Write((byte)0);

    var dirEnd = (uint)ms.Position;

    // Patch directory offsets in the header.
    ms.Position = 8;
    bw.Write(dirEnd - dirStart);                    // dirSize
    ms.Position = 16;
    bw.Write(dirStart);                             // dirStart

    ms.Position = 0;
    var r = new FileFormat.Hpi.HpiReader(ms);
    var file = r.Entries.First(e => !e.IsDirectory);
    var ex = Assert.Throws<NotSupportedException>(() => r.Extract(file));
    Assert.That(ex!.Message, Does.Contain("LZ77").IgnoreCase);
  }

  [Test, Category("ErrorHandling")]
  public void Reader_RejectsBadMagic() {
    var buf = new byte[20];
    Array.Fill(buf, (byte)0xFF);
    using var ms = new MemoryStream(buf);
    Assert.Throws<InvalidDataException>(() => _ = new FileFormat.Hpi.HpiReader(ms));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_Properties() {
    var d = new FileFormat.Hpi.HpiFormatDescriptor();
    Assert.That(d.Id, Is.EqualTo("Hpi"));
    Assert.That(d.DisplayName, Is.EqualTo("Total Annihilation HPI"));
    Assert.That(d.Category, Is.EqualTo(Compression.Registry.FormatCategory.Archive));
    Assert.That(d.DefaultExtension, Is.EqualTo(".hpi"));
    Assert.That(d.Extensions, Contains.Item(".hpi"));
    Assert.That(d.Extensions, Contains.Item(".ufo"));
    Assert.That(d.Extensions, Contains.Item(".ccx"));
    Assert.That(d.Extensions, Contains.Item(".gp3"));
    Assert.That(d.MagicSignatures, Has.Count.EqualTo(1));
    Assert.That(d.MagicSignatures[0].Bytes, Is.EqualTo("HAPI"u8.ToArray()));
    Assert.That(d.Methods, Has.Count.EqualTo(1));
    Assert.That(d.Methods[0].Name, Is.EqualTo("hpi-zlib"));
    Assert.That(d.Family, Is.EqualTo(Compression.Registry.AlgorithmFamily.Archive));
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.SupportsDirectories), Is.True);
    Assert.That(d.Capabilities.HasFlag(Compression.Registry.FormatCapabilities.CanCreate), Is.True);
  }
}
