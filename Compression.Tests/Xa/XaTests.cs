#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Pcm;
using Codec.XaAdpcm;
using Compression.Registry;
using FileFormat.Xa;

namespace Compression.Tests.Xa;

[TestFixture]
public class XaTests {

  private const int RawSectorSize = 2352;
  private const int HeaderSize = 16;
  private const int SubHeaderSize = 8;
  private const byte SubModeAudio = 0x04;
  private const byte SubModeEof = 0x80;
  private static readonly byte[] SyncPattern =
    [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

  // Wraps raw 2352-byte sectors in a RIFF/CDXA shell.
  private static byte[] WrapCdxa(byte[] sectors) {
    const int fmtSize = 16;
    var payload = 4 + (8 + fmtSize) + (8 + sectors.Length);
    var blob = new byte[12 + 8 + fmtSize + 8 + sectors.Length];
    var s = blob.AsSpan();
    "RIFF"u8.CopyTo(s);
    BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)payload);
    "CDXA"u8.CopyTo(s[8..]);
    "fmt "u8.CopyTo(s[12..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[16..], fmtSize);
    "data"u8.CopyTo(s[36..]);
    BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)sectors.Length);
    sectors.CopyTo(s[44..]);
    return blob;
  }

  // Builds one synced 2352-byte audio sector for the given stream + payload.
  private static byte[] BuildSector(int file, int channel, bool stereo, bool fourBit, bool eof, byte[] groups,
      int sampleRate = 37800) {
    var sector = new byte[RawSectorSize];
    SyncPattern.CopyTo(sector.AsSpan());
    sector[15] = 0x02; // mode 2

    byte coding = 0;
    if (stereo) coding |= 0x01;
    if (sampleRate <= 18900) coding |= 0x04; // bits 2-3: 1 = 18900 Hz
    if (!fourBit) coding |= 0x10;            // bits 4-5: 1 = 8-bit

    var submode = (byte)(SubModeAudio | (eof ? SubModeEof : 0));
    var sub = HeaderSize;
    sector[sub + 0] = (byte)file;
    sector[sub + 1] = (byte)channel;
    sector[sub + 2] = submode;
    sector[sub + 3] = coding;
    sector[sub + 6] = submode;
    sector[sub + 7] = coding;

    var dataOff = sub + SubHeaderSize;
    Array.Copy(groups, 0, sector, dataOff, Math.Min(groups.Length, RawSectorSize - dataOff));
    return sector;
  }

  // Mono groups that exactly fill one 2352-byte sector's 2304-byte data area (18 groups).
  private const int MonoGroupsPerSector = 18;
  private const int MonoSamplesPerSector = 28 * 8 * MonoGroupsPerSector; // 4032

  // A synthetic mono XA stream produced by the encoder, filling one whole sector, wrapped as RIFF/CDXA.
  private static byte[] SyntheticMonoXa(out short[] sourcePcm, int sampleRate = 37800) {
    const int count = MonoSamplesPerSector;
    sourcePcm = new short[count];
    for (var i = 0; i < count; ++i)
      sourcePcm[i] = (short)(Math.Sin(i * 2 * Math.PI / 48) * 9000);
    var adpcm = XaAdpcmCodec.Encode(sourcePcm, stereo: false);
    var sector = BuildSector(0, 0, stereo: false, fourBit: true, eof: true, adpcm, sampleRate);
    return WrapCdxa(sector);
  }

  // ──────────── List / extract ────────────

  [Test]
  public void Descriptor_List_Mono_SurfacesFullMonoAndMetadata() {
    var xa = SyntheticMonoXa(out _);
    using var ms = new MemoryStream(xa);
    var entries = new XaFormatDescriptor().List(ms, null);

    Assert.That(entries.Any(e => e.Name == "FULL.xa"), Is.True);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "metadata.ini"), Is.True);
    Assert.That(entries.First(e => e.Name == "FULL.xa").Kind, Is.EqualTo("Container"));
    Assert.That(entries.First(e => e.Name == "MONO.wav").Kind, Is.EqualTo("Channel"));
  }

  [Test]
  public void Descriptor_MonoWav_CarriesCodingRateAndDecodes() {
    const int rate = 18900;
    var xa = SyntheticMonoXa(out var pcm, rate);
    using var ms = new MemoryStream(xa);
    using var output = new MemoryStream();
    new XaFormatDescriptor().ExtractEntry(ms, "MONO.wav", output, null);
    var wav = output.ToArray();

    Assert.That(wav.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)), Is.EqualTo(1)); // mono
    Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)), Is.EqualTo((uint)rate));

    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(pcm.Length * 2)));
  }

  [Test]
  public void Descriptor_RawSectors_NoRiffShell_Decodes() {
    const int count = 28 * 8;
    var pcm = new short[count];
    for (var i = 0; i < count; ++i) pcm[i] = (short)(Math.Sin(i / 5.0) * 8000);
    var adpcm = XaAdpcmCodec.Encode(pcm, stereo: false);
    var sector = BuildSector(0, 0, stereo: false, fourBit: true, eof: true, adpcm);

    using var ms = new MemoryStream(sector); // bare 2352 sectors, leading sync
    var entries = new XaFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);
  }

  // ──────────── Multi-stream + non-audio handling ────────────

  [Test]
  public void Descriptor_MultiStream_PicksFirstStream_NotesOthers() {
    var pcm = new short[28 * 8];
    for (var i = 0; i < pcm.Length; ++i) pcm[i] = (short)(i * 3);
    var adpcm = XaAdpcmCodec.Encode(pcm, stereo: false);

    var s0 = BuildSector(0, 0, stereo: false, fourBit: true, eof: false, adpcm); // stream 0:0
    var s1 = BuildSector(0, 1, stereo: false, fourBit: true, eof: true, adpcm);  // stream 0:1
    var combined = new byte[s0.Length + s1.Length];
    s0.CopyTo(combined, 0);
    s1.CopyTo(combined, s0.Length);

    using var ms = new MemoryStream(WrapCdxa(combined));
    using var meta = new MemoryStream();
    new XaFormatDescriptor().ExtractEntry(ms, "metadata.ini", meta, null);
    var ini = Encoding.UTF8.GetString(meta.ToArray());

    Assert.That(ini, Does.Contain("channel=0"));
    Assert.That(ini, Does.Contain("other_streams=0:1"));
  }

  [Test]
  public void Descriptor_NonAudioSectors_AreSkipped() {
    var pcm = new short[28 * 8];
    for (var i = 0; i < pcm.Length; ++i) pcm[i] = (short)(Math.Sin(i / 4.0) * 7000);
    var adpcm = XaAdpcmCodec.Encode(pcm, stereo: false);

    // A data (non-audio) sector first: submode without the AUDIO bit.
    var dataSector = new byte[RawSectorSize];
    SyncPattern.CopyTo(dataSector.AsSpan());
    dataSector[15] = 0x02;
    dataSector[HeaderSize + 2] = 0x08; // submode DATA, not AUDIO
    var audio = BuildSector(0, 0, stereo: false, fourBit: true, eof: true, adpcm);

    var combined = new byte[dataSector.Length + audio.Length];
    dataSector.CopyTo(combined, 0);
    audio.CopyTo(combined, dataSector.Length);

    using var ms = new MemoryStream(WrapCdxa(combined));
    var entries = new XaFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var meta = new MemoryStream();
    using var ms2 = new MemoryStream(WrapCdxa(combined));
    new XaFormatDescriptor().ExtractEntry(ms2, "metadata.ini", meta, null);
    Assert.That(Encoding.UTF8.GetString(meta.ToArray()), Does.Contain("audio_sectors=1"));
  }

  // ──────────── Create / round-trip ────────────

  [Test]
  public void Descriptor_Create_FromMonoWav_ProducesReadableRiffCdxa() {
    const int count = MonoSamplesPerSector; // fills one whole sector → exact round-trip count
    var raw = new byte[count * 2];
    for (var i = 0; i < count; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(i * 2), (short)(Math.Sin(i / 7.0) * 6000));
    var wav = PcmCodec.ToWavBlob(raw, channels: 1, sampleRate: 37800, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("MONO.wav", wav) };
    using var created = new MemoryStream();
    new XaFormatDescriptor().Create(created, inputs, new FormatCreateOptions());
    var xa = created.ToArray();

    Assert.That(xa.AsSpan(0, 4).ToArray(), Is.EqualTo("RIFF"u8.ToArray()));
    Assert.That(xa.AsSpan(8, 4).ToArray(), Is.EqualTo("CDXA"u8.ToArray()));

    // Re-open and confirm the mono channel round-trips with the right sample count.
    using var reopen = new MemoryStream(xa);
    var entries = new XaFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "MONO.wav"), Is.True);

    using var monoOut = new MemoryStream();
    using var reopen2 = new MemoryStream(xa);
    new XaFormatDescriptor().ExtractEntry(reopen2, "MONO.wav", monoOut, null);
    var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(monoOut.ToArray().AsSpan(40));
    Assert.That(dataSize, Is.EqualTo((uint)(count * 2)));
  }

  [Test]
  public void Descriptor_Create_FromStereoWavs_RoundTripsTwoChannels() {
    const int frames = 28 * 4 * 2; // whole stereo groups
    var left = new byte[frames * 2];
    var right = new byte[frames * 2];
    for (var i = 0; i < frames; ++i) {
      BinaryPrimitives.WriteInt16LittleEndian(left.AsSpan(i * 2), (short)(Math.Sin(i / 6.0) * 8000));
      BinaryPrimitives.WriteInt16LittleEndian(right.AsSpan(i * 2), (short)(Math.Cos(i / 9.0) * 7000));
    }
    var leftWav = PcmCodec.ToWavBlob(left, channels: 1, sampleRate: 37800, bitsPerSample: 16);
    var rightWav = PcmCodec.ToWavBlob(right, channels: 1, sampleRate: 37800, bitsPerSample: 16);

    var inputs = new List<ArchiveInputInfo> {
      ArchiveInputInfo.InMemory("RIGHT.wav", rightWav), // supplied out of order on purpose
      ArchiveInputInfo.InMemory("LEFT.wav", leftWav),
    };
    using var created = new MemoryStream();
    new XaFormatDescriptor().Create(created, inputs, new FormatCreateOptions());

    using var reopen = new MemoryStream(created.ToArray());
    var entries = new XaFormatDescriptor().List(reopen, null);
    Assert.That(entries.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(entries.Any(e => e.Name == "RIGHT.wav"), Is.True);

    // The LEFT channel must come back close to the source (lossy ADPCM tolerance).
    using var leftOut = new MemoryStream();
    using var reopen2 = new MemoryStream(created.ToArray());
    new XaFormatDescriptor().ExtractEntry(reopen2, "LEFT.wav", leftOut, null);
    var decoded = leftOut.ToArray();
    var firstSample = BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(44));
    Assert.That(Math.Abs(firstSample - BinaryPrimitives.ReadInt16LittleEndian(left)), Is.LessThan(2000));
  }

  [Test]
  public void Descriptor_Create_PassthroughFullXa() {
    var original = SyntheticMonoXa(out _);
    var inputs = new List<ArchiveInputInfo> { ArchiveInputInfo.InMemory("FULL.xa", original) };
    using var output = new MemoryStream();
    new XaFormatDescriptor().Create(output, inputs, new FormatCreateOptions());
    Assert.That(output.ToArray(), Is.EqualTo(original));
  }
}
