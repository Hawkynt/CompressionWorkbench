#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Xwb;

namespace Compression.Tests.Xwb;

[TestFixture]
public class XwbTests {

  // ── synthetic v43 bank builder ──────────────────────────────────────────────
  //
  // Layout produced (matching XwbReader's v43+ expectations):
  //   "WBND" | u32 version=43 | u32 headerVersion=1
  //   5 × (u32 off, u32 len)   → BANKDATA, ENTRYMETADATA, SEEKTABLES, ENTRYNAMES, ENTRYWAVEDATA
  //   BANKDATA (0x60)          → flags, entryCount, char[64] name, metaSize=24, nameSize=64, align
  //   ENTRYMETADATA            → entryCount × 24-byte entries
  //   ENTRYNAMES               → entryCount × 64-byte names
  //   ENTRYWAVEDATA            → concatenated coded waves
  private sealed record Wave(string Name, int Tag, int Channels, int SampleRate, int Bits, int AlignIndex, byte[] Coded);

  private static byte[] BuildBank(string bankName, params Wave[] waves) {
    const int metaSize = 24;
    const int nameSize = 64;
    const int bankDataLen = 0x60;

    var metaLen = waves.Length * metaSize;
    var namesLen = waves.Length * nameSize;
    var waveLen = waves.Sum(w => w.Coded.Length);

    var segTable = 12;
    var bankDataOff = segTable + 5 * 8;
    var metaOff = bankDataOff + bankDataLen;
    var namesOff = metaOff + metaLen;
    var waveOff = namesOff + namesLen;
    var total = waveOff + waveLen;

    var buf = new byte[total];
    var s = buf.AsSpan();
    "WBND"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 43);
    BinaryPrimitives.WriteUInt32LittleEndian(s[8..], 1);

    int[] segOffsets = [bankDataOff, metaOff, 0, namesOff, waveOff];
    int[] segLengths = [bankDataLen, metaLen, 0, namesLen, waveLen];
    for (var seg = 0; seg < 5; ++seg) {
      BinaryPrimitives.WriteUInt32LittleEndian(s[(segTable + seg * 8)..], (uint)segOffsets[seg]);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(segTable + seg * 8 + 4)..], (uint)segLengths[seg]);
    }

    // BANKDATA
    BinaryPrimitives.WriteUInt32LittleEndian(s[bankDataOff..], 0);                 // flags
    BinaryPrimitives.WriteUInt32LittleEndian(s[(bankDataOff + 4)..], (uint)waves.Length);
    Encoding.ASCII.GetBytes(bankName).CopyTo(s[(bankDataOff + 8)..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(bankDataOff + 72)..], metaSize);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(bankDataOff + 76)..], nameSize);
    BinaryPrimitives.WriteUInt32LittleEndian(s[(bankDataOff + 80)..], 4);          // alignment

    var playOffset = 0;
    for (var i = 0; i < waves.Length; ++i) {
      var w = waves[i];
      var o = metaOff + i * metaSize;

      uint format = (uint)(w.Tag & 0x3);
      format |= (uint)(w.Channels & 0x7) << 2;
      format |= (uint)(w.SampleRate & 0x3FFFF) << 5;
      format |= (uint)(w.AlignIndex & 0xFF) << 23;
      if (w.Bits == 16) format |= 1u << 31;

      BinaryPrimitives.WriteUInt32LittleEndian(s[o..], 0);                         // flagsAndDuration
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 4)..], format);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 8)..], (uint)playOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 12)..], (uint)w.Coded.Length);
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 16)..], 0);                  // loopStart
      BinaryPrimitives.WriteUInt32LittleEndian(s[(o + 20)..], 0);                  // loopLength

      Encoding.ASCII.GetBytes(w.Name).CopyTo(s[(namesOff + i * nameSize)..]);
      w.Coded.CopyTo(s[(waveOff + playOffset)..]);
      playOffset += w.Coded.Length;
    }

    return buf;
  }

  private static byte[] Pcm16Bytes(short[] samples) {
    var b = new byte[samples.Length * 2];
    for (var i = 0; i < samples.Length; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), samples[i]);
    return b;
  }

  // Build a single valid mono MS-ADPCM block for the given alignIndex.
  // blockAlign = (alignIndex + 22) * channels (channels = 1 here).
  private static byte[] MonoAdpcmBlock(int alignIndex) {
    var blockAlign = (alignIndex + 22);
    var block = new byte[blockAlign];
    block[0] = 0;                                              // predictor selector 0
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(1), 16); // delta
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(3), 100); // sample1
    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(5), 50);  // sample2
    for (var i = 7; i < blockAlign; ++i) block[i] = 0x10;     // arbitrary nibbles
    return block;
  }

  [Test]
  public void Reader_DecodesPcmAndAdpcmEntries() {
    var pcm = new short[] { 0, 1000, -1000, 2000, -2000, 32767, -32768 };
    const int alignIndex = 10;
    var blob = BuildBank("TestBank",
      new Wave("tone_pcm", Tag: 0, Channels: 1, SampleRate: 22050, Bits: 16, AlignIndex: 0, Coded: Pcm16Bytes(pcm)),
      new Wave("tone_adpcm", Tag: 2, Channels: 1, SampleRate: 16000, Bits: 16, AlignIndex: alignIndex, Coded: MonoAdpcmBlock(alignIndex)));

    var parsed = new XwbReader().Read(blob);
    Assert.That(parsed.Version, Is.EqualTo(43));
    Assert.That(parsed.Bank.BankName, Is.EqualTo("TestBank"));
    Assert.That(parsed.Entries.Count, Is.EqualTo(2));

    var p = parsed.Entries[0];
    Assert.That(p.FormatTag, Is.EqualTo(0));
    Assert.That(p.SampleRate, Is.EqualTo(22050));
    Assert.That(p.Decodable, Is.True);
    Assert.That(p.Pcm, Is.EqualTo(pcm));

    var a = parsed.Entries[1];
    Assert.That(a.FormatTag, Is.EqualTo(2));
    Assert.That(a.SampleRate, Is.EqualTo(16000));
    Assert.That(a.BlockAlign, Is.EqualTo(alignIndex + 22));
    Assert.That(a.Decodable, Is.True);
    Assert.That(a.Pcm!.Length, Is.GreaterThan(0));
  }

  [Test]
  public void Descriptor_SurfacesFullMetadataAndSamples() {
    var pcm = new short[] { 0, 500, -500, 1000 };
    var blob = BuildBank("Bank2",
      new Wave("hello", Tag: 0, Channels: 1, SampleRate: 8000, Bits: 16, AlignIndex: 0, Coded: Pcm16Bytes(pcm)));

    using var ms = new MemoryStream(blob);
    var entries = new XwbFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.xwb" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini" && e.Kind == "Tag"), Is.True);
    Assert.That(entries.Any(e => e.Name == "samples/000_hello.wav" && e.Kind == "Sample"), Is.True);
  }

  [Test]
  public void Descriptor_SkipsXmaEntry_WithMetadataNote() {
    var pcm = new short[] { 0, 100, -100 };
    var blob = BuildBank("Bank3",
      new Wave("good", Tag: 0, Channels: 1, SampleRate: 8000, Bits: 16, AlignIndex: 0, Coded: Pcm16Bytes(pcm)),
      new Wave("xma", Tag: 1, Channels: 2, SampleRate: 44100, Bits: 16, AlignIndex: 4, Coded: new byte[64]));

    using var ms = new MemoryStream(blob);
    var entries = new XwbFormatDescriptor().List(ms, null);

    // Only the PCM entry produces a WAV.
    Assert.That(entries.Count(e => e.Kind == "Sample"), Is.EqualTo(1));

    using var meta = new MemoryStream();
    using var ms2 = new MemoryStream(blob);
    new XwbFormatDescriptor().ExtractEntry(ms2, "metadata.ini", meta, null);
    var ini = Encoding.UTF8.GetString(meta.ToArray());
    Assert.That(ini, Does.Contain("codec=XMA"));
    Assert.That(ini, Does.Contain("skipped"));
  }

  [Test]
  public void Descriptor_FullOnlyFallback_OnGarbage() {
    var blob = "WBND"u8.ToArray().Concat(new byte[8]).ToArray();
    using var ms = new MemoryStream(blob);
    var entries = new XwbFormatDescriptor().List(ms, null);
    Assert.That(entries.Count, Is.EqualTo(1));
    Assert.That(entries[0].Name, Is.EqualTo("FULL.xwb"));
  }
}
