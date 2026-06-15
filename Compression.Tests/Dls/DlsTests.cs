#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Dls;

namespace Compression.Tests.Dls;

[TestFixture]
public class DlsTests {

  private static readonly short[] WaveA = [11, 22, 33, 44];

  private static byte[] BuildDls() {
    // INFO at collection level.
    var info = new MemoryStream();
    WriteChunk(info, "INAM", ZeroTerm("TestColl"));
    var infoList = MakeList("INFO", info.ToArray());

    // One wave-pool entry: LIST "wave" { fmt, data, LIST INFO{INAM} }.
    var fmt = new byte[16];
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(0), 1);      // PCM
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(2), 1);      // mono
    BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(4), 22050);  // rate
    BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 22050 * 2); // byterate
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(12), 2);     // block align
    BinaryPrimitives.WriteUInt16LittleEndian(fmt.AsSpan(14), 16);    // bits

    var data = new byte[WaveA.Length * 2];
    for (var i = 0; i < WaveA.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2), WaveA[i]);

    var waveInfo = new MemoryStream();
    WriteChunk(waveInfo, "INAM", ZeroTerm("Bell"));
    var waveBody = new MemoryStream();
    WriteChunk(waveBody, "fmt ", fmt);
    WriteChunk(waveBody, "data", data);
    waveBody.Write(MakeList("INFO", waveInfo.ToArray()));
    var wave = MakeList("wave", waveBody.ToArray());

    var wvpl = MakeList("wvpl", wave);

    var body = new MemoryStream();
    body.Write("DLS "u8);
    body.Write(infoList);
    body.Write(wvpl);
    var bodyBytes = body.ToArray();

    var riff = new MemoryStream();
    riff.Write("RIFF"u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)bodyBytes.Length);
    riff.Write(size);
    riff.Write(bodyBytes);
    return riff.ToArray();
  }

  private static byte[] ZeroTerm(string s) {
    var raw = Encoding.ASCII.GetBytes(s);
    var b = new byte[raw.Length + 1];
    raw.CopyTo(b, 0);
    return b;
  }

  private static void WriteChunk(Stream s, string id, byte[] body) {
    s.Write(Encoding.ASCII.GetBytes(id));
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
    s.Write(size);
    s.Write(body);
    if (body.Length % 2 != 0) s.WriteByte(0);
  }

  private static byte[] MakeList(string listType, byte[] body) {
    var ms = new MemoryStream();
    ms.Write("LIST"u8);
    var inner = new byte[4 + body.Length];
    Encoding.ASCII.GetBytes(listType).CopyTo(inner, 0);
    body.CopyTo(inner, 4);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)inner.Length);
    ms.Write(size);
    ms.Write(inner);
    if (inner.Length % 2 != 0) ms.WriteByte(0);
    return ms.ToArray();
  }

  [Test]
  public void List_SurfacesFullAndWaveSamples() {
    var blob = BuildDls();
    using var ms = new MemoryStream(blob);
    var entries = new DlsFormatDescriptor().List(ms, null);

    Assert.That(entries.Single(e => e.Name == "FULL.dls").Kind, Is.EqualTo("Container"));
    var samples = entries.Where(e => e.Kind == "Sample").ToList();
    Assert.That(samples.Count, Is.EqualTo(1));
    Assert.That(samples[0].Name, Is.EqualTo("samples/000_Bell.wav"));
  }

  [Test]
  public void ExtractedWave_IsValidMonoRiffWithExactPcm() {
    var blob = BuildDls();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new DlsFormatDescriptor().ExtractEntry(ms, "samples/000_Bell.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(wav.AsSpan(8, 4).ToArray(), Is.EqualTo("WAVE"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(22050u));

    var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo(WaveA.Length * 2));
    for (var i = 0; i < WaveA.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)), Is.EqualTo(WaveA[i]));
  }

  [Test]
  public void List_IncludesInfoTags() {
    var blob = BuildDls();
    using var ms = new MemoryStream(blob);
    var entries = new DlsFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "metadata/INAM.txt" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }
}
