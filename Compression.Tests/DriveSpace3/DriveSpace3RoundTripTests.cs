using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.DriveSpace3;

namespace Compression.Tests.DriveSpace3;

[TestFixture]
public class DriveSpace3RoundTripTests {

  [Test, Category("RoundTrip")]
  public void Stored_RoundTrip() {
    // Forced stored runs — every cluster must come back byte-for-byte.
    var data = new byte[4096];
    new Random(2026).NextBytes(data);

    var w = new DriveSpace3Writer { EnableCompression = false };
    w.AddFile("STORED.BIN", data);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(data));

    // No MDFAT entry should carry the compressed flag when EnableCompression=false.
    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var entries = (int)(mdfatLen * 512 / 4);
    for (var i = 0; i < entries; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + i * 4)));
      Assert.That((entry >> 28) & 0xFu, Is.Not.EqualTo(2u),
        $"cluster {i} must not be compressed when EnableCompression=false");
    }
  }

  [Test, Category("RoundTrip")]
  public void MsLzh_RoundTrip() {
    // Default (ms-lzh) — compressible payload must come back byte-for-byte.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "Microsoft DriveSpace 3 was shipped with Windows 95 Plus! Pack in 1995. ", 200)));

    var w = new DriveSpace3Writer();
    w.AddFile("PROSE.TXT", text);
    var cvf = w.Build();

    using var ms = new MemoryStream(cvf);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Entries, Has.Count.EqualTo(1));
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(text));
  }

  [Test, Category("RoundTrip")]
  public void MsLzh_SmallerThanStored() {
    // Highly redundant input — at least one cluster MUST be emitted compressed
    // (MDFAT flag = 2), otherwise the compression hook is not actually being
    // exercised.
    var text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(
      "AAAAAAAAAAAAAAAA", 2000)));

    var w = new DriveSpace3Writer();
    w.AddFile("REDUNDANT.TXT", text);
    var cvf = w.Build();

    var mdfatStart = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(44));
    var mdfatLen = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan(48));
    var entries = (int)(mdfatLen * 512 / 4);
    var sawCompressed = false;
    for (var i = 0; i < entries; i++) {
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(cvf.AsSpan((int)(mdfatStart * 512 + i * 4)));
      if (((entry >> 28) & 0xFu) == 2u) { sawCompressed = true; break; }
    }
    Assert.That(sawCompressed, Is.True,
      "Highly redundant content must produce at least one MDFAT entry with flags=2 (MS LZH compressed).");

    using var ms = new MemoryStream(cvf);
    using var r = new DriveSpace3Reader(ms);
    Assert.That(r.Extract(r.Entries[0]), Is.EqualTo(text));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsBadMagic() {
    var img = new byte[1024];
    img[0] = 0xEB; img[1] = 0x3C; img[2] = 0x90;
    Encoding.ASCII.GetBytes("BADMAG7").CopyTo(img.AsSpan(3));
    Assert.Throws<InvalidDataException>(
      () => _ = new DriveSpace3Reader(new MemoryStream(img)));
  }

  [Test, Category("Sad")]
  public void Reader_RejectsTooSmallImage() {
    Assert.Throws<InvalidDataException>(
      () => _ = new DriveSpace3Reader(new MemoryStream(new byte[100])));
  }

  [Test, Category("EdgeCase")]
  public void OpenEntry_ExtractClamps_AtSize() {
    // The Extract() pipeline must stop at entry.Size — never leak slack past
    // the declared file length.
    var data = "exactly this many bytes"u8.ToArray();
    var w = new DriveSpace3Writer();
    w.AddFile("BOUND.TXT", data);
    var cvf = w.Build();
    using var ms = new MemoryStream(cvf);
    using var r = new DriveSpace3Reader(ms);
    var bytes = r.Extract(r.Entries[0]);
    Assert.That(bytes.Length, Is.EqualTo(data.Length),
      "Extract must return exactly entry.Size bytes — no slack leak.");
    Assert.That(bytes, Is.EqualTo(data));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_Stored_RoundTrip() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "DriveSpace3Test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var content = new byte[3000];
      new Random(7).NextBytes(content);
      var inputPath = Path.Combine(tmpDir, "INPUT.BIN");
      File.WriteAllBytes(inputPath, content);

      var desc = new DriveSpace3FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms,
        [new ArchiveInputInfo(inputPath, "INPUT.BIN", false)],
        new FormatCreateOptions { MethodName = "stored" });

      ms.Position = 0;
      var entries = desc.List(ms, null);
      Assert.That(entries, Has.Count.EqualTo(1));
      Assert.That(entries[0].Name, Is.EqualTo("INPUT.BIN"));

      ms.Position = 0;
      desc.Extract(ms, tmpDir + "/out", password: null, files: null);
      var roundTripped = File.ReadAllBytes(Path.Combine(tmpDir + "/out", "INPUT.BIN"));
      Assert.That(roundTripped, Is.EqualTo(content));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_Default_RoundTripsViaInterface() {
    var tmpDir = Path.Combine(Path.GetTempPath(), "DriveSpace3Test_" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmpDir);
    try {
      var content = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("DriveSpace3! ", 500)));
      var inputPath = Path.Combine(tmpDir, "DOC.TXT");
      File.WriteAllBytes(inputPath, content);

      var desc = new DriveSpace3FormatDescriptor();
      using var ms = new MemoryStream();
      desc.Create(ms,
        [new ArchiveInputInfo(inputPath, "DOC.TXT", false)],
        new FormatCreateOptions());

      ms.Position = 0;
      desc.Extract(ms, tmpDir + "/out", password: null, files: null);
      var roundTripped = File.ReadAllBytes(Path.Combine(tmpDir + "/out", "DOC.TXT"));
      Assert.That(roundTripped, Is.EqualTo(content));
    } finally {
      Directory.Delete(tmpDir, recursive: true);
    }
  }
}
