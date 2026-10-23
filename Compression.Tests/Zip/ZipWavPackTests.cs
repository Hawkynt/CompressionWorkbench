using System.Buffers.Binary;
using Codec.WavPack;
using Compression.Core.Checksums;
using FileFormat.Zip;

namespace Compression.Tests.Zip;

/// <summary>ZIP method 97 (WinZip WavPack): a wrapped WavPack image of the WAV file.</summary>
[TestFixture]
public class ZipWavPackTests {

  [OneTimeSetUp]
  public void Init() => Compression.Lib.FormatRegistration.EnsureInitialized();

  /// <summary>A PCM WAV file with a LIST chunk after the data, so the wrapper has a trailer.</summary>
  internal static byte[] Wav(int channels, int bits, int frames, bool trailer = true) {
    var bytesPerSample = bits / 8;
    var data = new byte[frames * channels * bytesPerSample];
    var rng = new Random(bits * 31 + channels);
    for (var f = 0; f < frames; ++f)
      for (var c = 0; c < channels; ++c) {
        var v = (int)(Math.Sin((f + c * 7) * 0.05) * (1 << (bits - 2))) + rng.Next(-64, 64);
        var at = (f * channels + c) * bytesPerSample;
        switch (bytesPerSample) {
          case 1: data[at] = (byte)(v + 128); break;
          case 2: BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(at), (short)v); break;
          case 3: data[at] = (byte)v; data[at + 1] = (byte)(v >> 8); data[at + 2] = (byte)(v >> 16); break;
        }
      }
    var list = trailer ? "LIST\x0A\0\0\0INFOabcdef"u8.ToArray() : [];
    using var ms = new MemoryStream();
    void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
    void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); ms.Write(b); }
    ms.Write("RIFF"u8); U32((uint)(4 + 24 + 8 + data.Length + list.Length)); ms.Write("WAVE"u8);
    ms.Write("fmt "u8); U32(16); U16(1); U16((ushort)channels); U32(44100);
    U32((uint)(44100 * channels * bytesPerSample)); U16((ushort)(channels * bytesPerSample)); U16((ushort)bits);
    ms.Write("data"u8); U32((uint)data.Length); ms.Write(data);
    ms.Write(list);
    return ms.ToArray();
  }

  public static IEnumerable<TestCaseData> Wavs() {
    yield return new TestCaseData(Wav(2, 16, 20_000)).SetName("Stereo16");
    yield return new TestCaseData(Wav(1, 8, 10_000)).SetName("Mono8");
    yield return new TestCaseData(Wav(2, 24, 5_000, trailer: false)).SetName("Stereo24_NoTrailer");
    yield return new TestCaseData(Wav(1, 16, 0)).SetName("Empty16");
  }

  [TestCaseSource(nameof(Wavs)), Category("RoundTrip")]
  public void PackUnpack_RebuildsTheExactFile(byte[] wav)
    => Assert.That(WavPackWrapper.Unpack(WavPackWrapper.Pack(wav)), Is.EqualTo(wav));

  [TestCaseSource(nameof(Wavs)), Category("RoundTrip")]
  public void ZipMethod97Entry_IsExtracted(byte[] wav) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true)) {
      writer.AddRawEntry("track.wav", WavPackWrapper.Pack(wav), ZipCompressionMethod.WavPack, Crc32.Compute(wav), wav.Length);
      writer.Finish();
    }
    ms.Position = 0;
    using var reader = new ZipReader(ms);
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(wav));
  }

  [Test, Category("Exception")]
  public void Unpack_StreamWithoutWrapper_IsRefused() {
    using var plain = new MemoryStream();
    WavPackCodec.Compress(new MemoryStream(new byte[400]), plain, 1, 44100, 16);
    Assert.Throws<InvalidDataException>(() => WavPackWrapper.Unpack(plain.ToArray()));
  }

  [Test, Category("Exception")]
  public void Pack_NonWav_IsRefused()
    => Assert.Throws<NotSupportedException>(() => WavPackWrapper.Pack("not a wave file at all"u8));
}

/// <summary>
/// The reference WavPack tools: <c>wvunpack</c> must rebuild the WAV from what we write,
/// and we must rebuild it from what <c>wavpack</c> writes — also inside a method-97 ZIP,
/// which The Unarchiver (unar) must extract too.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
public class ZipWavPackInteropTests {
  private string _dir = null!;

  [OneTimeSetUp]
  public void Init() => Compression.Lib.FormatRegistration.EnsureInitialized();

  [SetUp]
  public void Setup() {
    if (!FsInteropToolbox.WslAvailable || !FsInteropToolbox.WslHasTool("wavpack") || !FsInteropToolbox.WslHasTool("wvunpack"))
      Assert.Ignore("wavpack not installed. Run: sudo apt install -y wavpack");
    this._dir = Path.Combine(Path.GetTempPath(), $"cwb_zipwv_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._dir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._dir, true); } catch { /* best effort */ }
  }

  [Test]
  public void OurWrappedStream_IsUnpackedByWvunpack() {
    var wav = ZipWavPackTests.Wav(2, 16, 30_000);
    var wv = Path.Combine(this._dir, "ours.wv");
    var outWav = Path.Combine(this._dir, "out.wav");
    File.WriteAllBytes(wv, WavPackWrapper.Pack(wav));
    var r = FsInteropToolbox.RunWsl($"wvunpack -q -y {FsInteropToolbox.WinToWsl(wv)} -o {FsInteropToolbox.WinToWsl(outWav)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    Assert.That(File.ReadAllBytes(outWav), Is.EqualTo(wav));
  }

  [Test]
  public void WavpackStream_InAMethod97Zip_IsExtractedByUs() {
    var wav = ZipWavPackTests.Wav(2, 16, 30_000);
    var inWav = Path.Combine(this._dir, "in.wav");
    var wv = Path.Combine(this._dir, "theirs.wv");
    File.WriteAllBytes(inWav, wav);
    var r = FsInteropToolbox.RunWsl($"wavpack -q -y {FsInteropToolbox.WinToWsl(inWav)} -o {FsInteropToolbox.WinToWsl(wv)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    var theirs = File.ReadAllBytes(wv);
    Assert.That(WavPackWrapper.Unpack(theirs), Is.EqualTo(wav), "wrapped stream");

    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true)) {
      writer.AddRawEntry("in.wav", theirs, ZipCompressionMethod.WavPack, Crc32.Compute(wav), wav.Length);
      writer.Finish();
    }
    ms.Position = 0;
    using var reader = new ZipReader(ms);
    Assert.That(reader.ExtractEntry(reader.Entries[0]), Is.EqualTo(wav), "method 97 entry");
  }

  [Test]
  public void OurMethod97Zip_IsExtractedByUnar() {
    if (!FsInteropToolbox.WslHasTool("unar")) Assert.Ignore("unar not installed. Run: sudo apt install -y unar");
    var wav = ZipWavPackTests.Wav(2, 16, 30_000);
    var zip = Path.Combine(this._dir, "ours.zip");
    using (var fs = File.Create(zip))
    using (var writer = new ZipWriter(fs)) {
      writer.AddRawEntry("track.wav", WavPackWrapper.Pack(wav), ZipCompressionMethod.WavPack, Crc32.Compute(wav), wav.Length);
      writer.Finish();
    }
    // Control: a zip around wavpack's own stream tells whether this unar build has WavPack at all.
    var inWav = Path.Combine(this._dir, "control.wav");
    var controlWv = Path.Combine(this._dir, "control.wv");
    File.WriteAllBytes(inWav, wav);
    FsInteropToolbox.RunWsl($"wavpack -q -y {FsInteropToolbox.WinToWsl(inWav)} -o {FsInteropToolbox.WinToWsl(controlWv)}");
    var controlZip = Path.Combine(this._dir, "control.zip");
    using (var fs = File.Create(controlZip))
    using (var writer = new ZipWriter(fs)) {
      writer.AddRawEntry("control.wav", File.ReadAllBytes(controlWv), ZipCompressionMethod.WavPack, Crc32.Compute(wav), wav.Length);
      writer.Finish();
    }
    var control = FsInteropToolbox.RunWsl($"unar -q -f -o {FsInteropToolbox.WinToWsl(Path.Combine(this._dir, "c"))} {FsInteropToolbox.WinToWsl(controlZip)}");
    if (control.ExitCode != 0 && (control.StdOut + control.StdErr).Contains("not fully supported", StringComparison.OrdinalIgnoreCase))
      Assert.Ignore("This unar build has no WavPack support: it refuses wavpack's own stream as well.");

    var outDir = Path.Combine(this._dir, "x");
    var r = FsInteropToolbox.RunWsl($"unar -q -f -o {FsInteropToolbox.WinToWsl(outDir)} {FsInteropToolbox.WinToWsl(zip)}");
    Assert.That(r.ExitCode, Is.EqualTo(0), r.StdOut + r.StdErr);
    var extracted = FsInteropToolbox.FindFile(outDir, "track.wav");
    Assert.That(extracted, Is.Not.Null);
    Assert.That(File.ReadAllBytes(extracted!), Is.EqualTo(wav));
  }
}
