using System.Text;
using FileFormat.WwiseBnk;

namespace Compression.Tests.WwiseBnk;

/// <summary>
/// Given a Wwise SoundBank, When the descriptor lists/extracts it, Then it
/// surfaces per-section raw blobs (sections/BKHD.bin, sections/HIRC.bin, …) and
/// per-WEM samples alongside the verbatim FULL.bnk — and extracts in-memory.
/// </summary>
[TestFixture]
public class WwiseBnkPseudoArchiveTests {

  private static void WriteU32(Stream s, uint v) {
    Span<byte> b = stackalloc byte[4];
    b[0] = (byte)v; b[1] = (byte)(v >> 8); b[2] = (byte)(v >> 16); b[3] = (byte)(v >> 24);
    s.Write(b);
  }

  private static void WriteChunk(Stream s, string tag, byte[] data) {
    s.Write(Encoding.ASCII.GetBytes(tag));
    WriteU32(s, (uint)data.Length);
    s.Write(data);
  }

  private static byte[] BuildBnk(out uint wemId, out byte[] wemData, out byte[] bkhdBody, out byte[] hircBody) {
    wemId = 0x11112222u;
    wemData = "FakeWEMBytes"u8.ToArray();

    var bkhd = new MemoryStream();
    WriteU32(bkhd, 140);          // version
    WriteU32(bkhd, 0xABCDEF12);   // bankId
    WriteU32(bkhd, 0); WriteU32(bkhd, 0);
    bkhdBody = bkhd.ToArray();

    var didx = new MemoryStream();
    WriteU32(didx, wemId);
    WriteU32(didx, 0);
    WriteU32(didx, (uint)wemData.Length);

    var hirc = new MemoryStream();
    WriteU32(hirc, 1);            // object count
    hirc.WriteByte(0x02);         // sound SFX
    WriteU32(hirc, 12);
    WriteU32(hirc, 0x99887766);
    hirc.Write(new byte[8]);
    hircBody = hirc.ToArray();

    var ms = new MemoryStream();
    WriteChunk(ms, "BKHD", bkhdBody);
    WriteChunk(ms, "DIDX", didx.ToArray());
    WriteChunk(ms, "DATA", wemData);
    WriteChunk(ms, "HIRC", hircBody);
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void List_ExposesPerSectionBlobsAndWems() {
    var bytes = BuildBnk(out var wemId, out _, out _, out _);
    using var ms = new MemoryStream(bytes);
    var names = new WwiseBnkFormatDescriptor().List(ms, null).Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("FULL.bnk"));
    Assert.That(names, Does.Contain("metadata.ini"));
    Assert.That(names, Does.Contain("sections/BKHD.bin"));
    Assert.That(names, Does.Contain("sections/DIDX.bin"));
    Assert.That(names, Does.Contain("sections/DATA.bin"));
    Assert.That(names, Does.Contain("sections/HIRC.bin"));
    Assert.That(names, Does.Contain($"wems/{wemId}.wem"));
  }

  [Test, Category("HappyPath")]
  public void Extract_WritesSectionBlobs_BkhdAndHircByteIdentical() {
    var bytes = BuildBnk(out _, out _, out var bkhdBody, out var hircBody);
    var dir = Path.Combine(Path.GetTempPath(), $"bnk-pa-{Guid.NewGuid():N}");
    try {
      using var ms = new MemoryStream(bytes);
      new WwiseBnkFormatDescriptor().Extract(ms, dir, null, null);

      Assert.That(File.ReadAllBytes(Path.Combine(dir, "FULL.bnk")), Is.EqualTo(bytes));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "sections", "BKHD.bin")), Is.EqualTo(bkhdBody));
      Assert.That(File.ReadAllBytes(Path.Combine(dir, "sections", "HIRC.bin")), Is.EqualTo(hircBody));
    } finally {
      if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
  }

  [Test, Category("HappyPath")]
  public void ExtractEntry_InMemory_ReturnsSectionAndFull() {
    var bytes = BuildBnk(out _, out _, out var bkhdBody, out _);
    var d = new WwiseBnkFormatDescriptor();

    using var ms = new MemoryStream(bytes);
    using var section = new MemoryStream();
    d.ExtractEntry(ms, "sections/BKHD.bin", section, null);
    Assert.That(section.ToArray(), Is.EqualTo(bkhdBody));

    using var ms2 = new MemoryStream(bytes);
    using var full = new MemoryStream();
    d.ExtractEntry(ms2, "FULL.bnk", full, null);
    Assert.That(full.ToArray(), Is.EqualTo(bytes));
  }

  [Test, Category("EdgeCase")]
  public void Malformed_DoesNotThrow_ListReturnsFull() {
    // BKHD header truncated mid-chunk — reader should still surface FULL.bnk.
    var bogus = "BKHD"u8.ToArray();
    using var ms = new MemoryStream(bogus);
    List<Compression.Registry.ArchiveEntryInfo> entries = null!;
    Assert.DoesNotThrow(() => entries = new WwiseBnkFormatDescriptor().List(ms, null));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.bnk"));
  }
}
