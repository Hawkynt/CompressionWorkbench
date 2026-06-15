#pragma warning disable CS1591
using System.Buffers.Binary;
using FileFormat.MacSnd;

namespace Compression.Tests.Audio;

[TestFixture]
public class MacSndTests {

  // Builds a format-1 'snd ' resource with a single bufferCmd pointing at a SoundHeader.
  private static byte[] MakeFormat1(byte encode, int rate, byte[] body, Action<byte[], int>? patchHeader = null) {
    // Header: format(1) | nDataFormats(1) | id(5) | initOption(u32) | nCommands(1)
    //         | bufferCmd(0x8051) param1(0) param2(offsetOfSoundHeader)
    const int headerLen = 2 + 2 + 6 + 2 + 8;
    var soundHeaderOff = headerLen;
    var file = new byte[soundHeaderOff + body.Length];
    var s = file.AsSpan();
    BinaryPrimitives.WriteUInt16BigEndian(s, 1);          // format
    BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1);     // nDataFormats
    BinaryPrimitives.WriteUInt16BigEndian(s[4..], 5);     // dataFormat id (sampledSynth)
    BinaryPrimitives.WriteUInt32BigEndian(s[6..], 0);     // initOption
    BinaryPrimitives.WriteUInt16BigEndian(s[10..], 1);    // nCommands
    BinaryPrimitives.WriteUInt16BigEndian(s[12..], 0x8051); // bufferCmd
    BinaryPrimitives.WriteUInt16BigEndian(s[14..], 0);    // param1
    BinaryPrimitives.WriteUInt32BigEndian(s[16..], (uint)soundHeaderOff); // param2
    body.CopyTo(s[soundHeaderOff..]);
    patchHeader?.Invoke(file, soundHeaderOff);
    return file;
  }

  // 22-byte SoundHeader common prefix + appended payload.
  private static byte[] StandardSoundHeader(int rate, byte encode, byte[] payload) {
    var hdr = new byte[22 + payload.Length];
    var s = hdr.AsSpan();
    // samplePtr(0) at 0
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], (uint)payload.Length); // length
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], (uint)(rate << 16));   // 16.16 fixed rate
    s[20] = encode;
    s[21] = 60; // baseFrequency
    payload.CopyTo(s[22..]);
    return hdr;
  }

  [Test]
  public void Standard8Bit_SurfacesFullAndMono() {
    var hdr = StandardSoundHeader(22050, MacSndReader.StandardHeader, new byte[] { 128, 200, 50, 128 });
    var blob = MakeFormat1(MacSndReader.StandardHeader, 22050, hdr);

    var entries = new MacSndFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "FULL.snd" && e.Kind == "Container"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav" && e.Kind == "Channel"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
  }

  [Test]
  public void Standard8Bit_DecodesUnsignedPcmAtHeaderRate() {
    var payload = new byte[] { 128, 200, 50, 128 };
    var hdr = StandardSoundHeader(11025, MacSndReader.StandardHeader, payload);
    var blob = MakeFormat1(MacSndReader.StandardHeader, 11025, hdr);

    using var output = new MemoryStream();
    new MacSndFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo(11025u));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(8));
    Assert.That(wav.AsSpan(44).ToArray(), Is.EqualTo(payload));
  }

  [Test]
  public void ReaderParsesFixedRate() {
    var hdr = StandardSoundHeader(44100, MacSndReader.StandardHeader, new byte[] { 1, 2, 3 });
    var blob = MakeFormat1(MacSndReader.StandardHeader, 44100, hdr);
    var parsed = new MacSndReader().Read(blob);
    Assert.That(parsed.SampleRate, Is.EqualTo(44100));
    Assert.That(parsed.Encode, Is.EqualTo(MacSndReader.StandardHeader));
    Assert.That(parsed.NumChannels, Is.EqualTo(1));
  }

  [Test]
  public void Extended16Bit_DecodesBigEndianToLittleEndian() {
    // Extended header: 22-byte prefix, then numFrames@+22, sampleSize@+48, data@+64.
    var samplesBe = new byte[8]; // 4 × 16-bit BE
    BinaryPrimitives.WriteInt16BigEndian(samplesBe, 0);
    BinaryPrimitives.WriteInt16BigEndian(samplesBe.AsSpan(2), 256);
    BinaryPrimitives.WriteInt16BigEndian(samplesBe.AsSpan(4), -256);
    BinaryPrimitives.WriteInt16BigEndian(samplesBe.AsSpan(6), 4096);

    var hdr = new byte[64 + samplesBe.Length];
    var s = hdr.AsSpan();
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], 1);              // numChannels (mono)
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], 22050u << 16);   // 16.16 rate
    s[20] = MacSndReader.ExtendedHeader;
    s[21] = 60;
    BinaryPrimitives.WriteUInt32BigEndian(s[22..], 4);             // numFrames
    BinaryPrimitives.WriteUInt16BigEndian(s[48..], 16);            // sampleSize
    samplesBe.CopyTo(s[64..]);

    var blob = MakeFormat1(MacSndReader.ExtendedHeader, 22050, hdr);

    using var output = new MemoryStream();
    new MacSndFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    var pcm = wav.AsSpan(44);
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm), Is.EqualTo(0));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[2..]), Is.EqualTo(256));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[4..]), Is.EqualTo(-256));
    Assert.That(BinaryPrimitives.ReadInt16LittleEndian(pcm[6..]), Is.EqualTo(4096));
  }

  [Test]
  public void CompressedMace3_DecodesViaMaceToSixteenBit() {
    // Compressed header: numChannels@+4, numFrames@+22, compressionID@+56(i16),
    // sampleSize@+62, data@+64.
    var maceData = new byte[] { 0x00, 0x00, 0xFF, 0x80 };
    var hdr = new byte[64 + maceData.Length];
    var s = hdr.AsSpan();
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], 1);            // numChannels
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], 8000u << 16);  // rate
    s[20] = MacSndReader.CompressedHeader;
    s[21] = 60;
    BinaryPrimitives.WriteUInt32BigEndian(s[22..], 6);           // numFrames
    BinaryPrimitives.WriteInt16BigEndian(s[56..], MacSndReader.CompressionMace3);
    BinaryPrimitives.WriteUInt16BigEndian(s[62..], 16);          // sampleSize
    maceData.CopyTo(s[64..]);

    var blob = MakeFormat1(MacSndReader.CompressedHeader, 8000, hdr);

    var entries = new MacSndFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var output = new MemoryStream();
    new MacSndFormatDescriptor().ExtractEntry(new MemoryStream(blob), "MONO.wav", output, null);
    var wav = output.ToArray();
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)), Is.EqualTo(16));
    // 4 bytes MACE3 → 12 samples → 24 PCM bytes.
    Assert.That(wav.Length - 44, Is.EqualTo(24));
  }

  [Test]
  public void CompressedUnsupported_SurfacesFullOnly() {
    var hdr = new byte[64 + 8];
    var s = hdr.AsSpan();
    BinaryPrimitives.WriteUInt32BigEndian(s[4..], 1);
    BinaryPrimitives.WriteUInt32BigEndian(s[8..], 8000u << 16);
    s[20] = MacSndReader.CompressedHeader;
    BinaryPrimitives.WriteInt16BigEndian(s[56..], 99); // unknown compression
    BinaryPrimitives.WriteUInt16BigEndian(s[62..], 16);
    var blob = MakeFormat1(MacSndReader.CompressedHeader, 8000, hdr);

    var entries = new MacSndFormatDescriptor().List(new MemoryStream(blob), null);
    Assert.That(entries.Any(e => e.Name == "FULL.snd"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.False);
    using var metaStream = new MemoryStream();
    new MacSndFormatDescriptor().ExtractEntry(new MemoryStream(blob), "metadata.ini", metaStream, null);
    var meta = System.Text.Encoding.UTF8.GetString(metaStream.ToArray());
    Assert.That(meta, Does.Contain("compression_id=99"));
  }

  [Test]
  public void Format2_MinimalResource_Decodes() {
    // format(2) | refCount | nCommands(1) | bufferCmd param2=offset
    var payload = new byte[] { 128, 64, 192 };
    var hdr = StandardSoundHeader(8000, MacSndReader.StandardHeader, payload);
    const int soundHeaderOff = 6 + 8;
    var file = new byte[soundHeaderOff + hdr.Length];
    var s = file.AsSpan();
    BinaryPrimitives.WriteUInt16BigEndian(s, 2);          // format 2
    BinaryPrimitives.WriteUInt16BigEndian(s[2..], 1);     // refCount
    BinaryPrimitives.WriteUInt16BigEndian(s[4..], 1);     // nCommands
    BinaryPrimitives.WriteUInt16BigEndian(s[6..], 0x8051);
    BinaryPrimitives.WriteUInt32BigEndian(s[10..], soundHeaderOff);
    hdr.CopyTo(s[soundHeaderOff..]);

    var parsed = new MacSndReader().Read(file);
    Assert.That(parsed.Format, Is.EqualTo(2));
    Assert.That(parsed.SampleData, Is.EqualTo(payload));
  }
}
