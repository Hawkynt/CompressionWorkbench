#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Sf2;

namespace Compression.Tests.Sf2;

[TestFixture]
public class Sf2Tests {

  // ── minimal hand-crafted sfbk ─────────────────────────────────────────────
  //
  // Two samples in the smpl block, each followed by the mandatory 46-point zero gap.
  // shdr: sample A "Kick" @ 22050 Hz mono, sample B "Snare" @ 44100 Hz mono, a ROM
  // sample "RomFx" (skipped) and the terminal "EOS" sentinel.

  private const int GapPoints = 46;
  private static readonly short[] SampleA = [10, 20, 30, 40, 50];     // 5 points
  private static readonly short[] SampleB = [-100, -200, -300];        // 3 points

  private static byte[] BuildSfbk() {
    // smpl block: A | 46 zeros | B | 46 zeros
    var pointsA = SampleA.Length;
    var pointsB = SampleB.Length;
    var startA = 0;
    var endA = startA + pointsA;
    var startB = endA + GapPoints;
    var endB = startB + pointsB;
    var totalPoints = endB + GapPoints;

    var smpl = new byte[totalPoints * 2];
    for (var i = 0; i < pointsA; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(smpl.AsSpan((startA + i) * 2), SampleA[i]);
    for (var i = 0; i < pointsB; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(smpl.AsSpan((startB + i) * 2), SampleB[i]);

    // ── INFO LIST ──
    var info = new MemoryStream();
    WriteChunk(info, "ifil", Ifil(2, 1));
    WriteChunk(info, "isng", ZeroTerm("EMU8000"));
    WriteChunk(info, "INAM", ZeroTerm("TestBank"));
    WriteChunk(info, "IENG", ZeroTerm("Tester"));
    var infoList = MakeList("INFO", info.ToArray());

    // ── sdta LIST ──
    var sdta = new MemoryStream();
    WriteChunk(sdta, "smpl", smpl);
    var sdtaList = MakeList("sdta", sdta.ToArray());

    // ── pdta LIST ──
    var pdta = new MemoryStream();
    // phdr: one preset + terminal sentinel (38 bytes each)
    var phdr = new MemoryStream();
    WritePhdr(phdr, "Piano", preset: 0, bank: 0);
    WritePhdr(phdr, "EOP", preset: 0, bank: 0);
    WriteChunk(pdta, "phdr", phdr.ToArray());
    // shdr: A, B, ROM, EOS
    var shdr = new MemoryStream();
    WriteShdr(shdr, "Kick", (uint)startA, (uint)endA, 0, 0, 22050, 60, 0, 0, sampleType: 1);
    WriteShdr(shdr, "Snare", (uint)startB, (uint)endB, 0, 0, 44100, 60, 0, 0, sampleType: 1);
    WriteShdr(shdr, "RomFx", 0, 1, 0, 0, 8000, 60, 0, 0, sampleType: 0x8001); // ROM mono
    WriteShdr(shdr, "EOS", 0, 0, 0, 0, 0, 0, 0, 0, sampleType: 0);
    WriteChunk(pdta, "shdr", shdr.ToArray());
    var pdtaList = MakeList("pdta", pdta.ToArray());

    // ── sfbk RIFF ──
    var body = new MemoryStream();
    body.Write("sfbk"u8);
    body.Write(infoList);
    body.Write(sdtaList);
    body.Write(pdtaList);
    var bodyBytes = body.ToArray();

    var riff = new MemoryStream();
    riff.Write("RIFF"u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)bodyBytes.Length);
    riff.Write(size);
    riff.Write(bodyBytes);
    return riff.ToArray();
  }

  private static byte[] Ifil(ushort major, ushort minor) {
    var b = new byte[4];
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0), major);
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), minor);
    return b;
  }

  private static byte[] ZeroTerm(string s) {
    var raw = Encoding.ASCII.GetBytes(s);
    var b = new byte[raw.Length + 1]; // null terminator
    raw.CopyTo(b, 0);
    if (b.Length % 2 != 0) { // chunk bodies are word-aligned; pad handled by WriteChunk
      // leave; WriteChunk pads
    }
    return b;
  }

  private static void WritePhdr(Stream s, string name, ushort preset, ushort bank) {
    var rec = new byte[38];
    var n = Encoding.ASCII.GetBytes(name);
    Array.Copy(n, rec, Math.Min(n.Length, 20));
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(20), preset);
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(22), bank);
    s.Write(rec);
  }

  private static void WriteShdr(Stream s, string name, uint start, uint end, uint loopStart, uint loopEnd,
      uint sampleRate, byte originalPitch, sbyte pitchCorrection, ushort sampleLink, ushort sampleType) {
    var rec = new byte[46];
    var n = Encoding.ASCII.GetBytes(name);
    Array.Copy(n, rec, Math.Min(n.Length, 20));
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(20), start);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(24), end);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(28), loopStart);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(32), loopEnd);
    BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(36), sampleRate);
    rec[40] = originalPitch;
    rec[41] = (byte)pitchCorrection;
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(42), sampleLink);
    BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(44), sampleType);
    s.Write(rec);
  }

  private static void WriteChunk(Stream s, string id, byte[] body) {
    s.Write(Encoding.ASCII.GetBytes(id));
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
    s.Write(size);
    s.Write(body);
    if (body.Length % 2 != 0) s.WriteByte(0); // word alignment pad
  }

  // LIST chunk: "LIST" | size | <listType + body>
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

  // ──────────────────────────────────────────────────────────────────────────

  [Test]
  public void List_SurfacesFullContainerAndSampleWavs() {
    var blob = BuildSfbk();
    using var ms = new MemoryStream(blob);
    var entries = new Sf2FormatDescriptor().List(ms, null);

    var full = entries.Single(e => e.Name == "FULL.sf2");
    Assert.That(full.Kind, Is.EqualTo("Container"));

    var samples = entries.Where(e => e.Kind == "Sample").ToList();
    Assert.That(samples.Count, Is.EqualTo(2), "two playable samples; ROM + EOS skipped");
    Assert.That(samples.Any(e => e.Name == "samples/000_Kick.wav"), Is.True);
    Assert.That(samples.Any(e => e.Name == "samples/001_Snare.wav"), Is.True);
  }

  [Test]
  public void ExtractedSample_IsMonoWavAtOwnRateWithExactPcm() {
    var blob = BuildSfbk();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new Sf2FormatDescriptor().ExtractEntry(ms, "samples/001_Snare.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(wav.AsSpan(8, 4).ToArray(), Is.EqualTo("WAVE"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1), "mono");
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(44100u), "own rate");
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16), "16-bit");

    var dataSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo(SampleB.Length * 2));
    for (var i = 0; i < SampleB.Length; ++i)
      Assert.That(BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + i * 2)), Is.EqualTo(SampleB[i]));
  }

  [Test]
  public void List_IncludesMetadataIniAndInfoTags() {
    var blob = BuildSfbk();
    using var ms = new MemoryStream(blob);
    var entries = new Sf2FormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata/INAM.txt" && e.Kind == "Tag"), Is.True);

    using var iniIn = new MemoryStream(blob);
    using var iniOut = new MemoryStream();
    new Sf2FormatDescriptor().ExtractEntry(iniIn, "metadata.ini", iniOut, null);
    var ini = Encoding.UTF8.GetString(iniOut.ToArray());
    Assert.That(ini, Does.Contain("bank_name=TestBank"));
    Assert.That(ini, Does.Contain("version=2.1"));
    Assert.That(ini, Does.Contain("preset_count=1"));
    Assert.That(ini, Does.Contain("sample_count=2"));
  }

  [Test]
  public void ExtractedInfoTag_HasStringValue() {
    var blob = BuildSfbk();
    using var ms = new MemoryStream(blob);
    using var output = new MemoryStream();
    new Sf2FormatDescriptor().ExtractEntry(ms, "metadata/INAM.txt", output, null);
    Assert.That(Encoding.ASCII.GetString(output.ToArray()).TrimEnd('\0'), Is.EqualTo("TestBank"));
  }
}
