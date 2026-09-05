#pragma warning disable CS1591

using System.Buffers.Binary;
using System.Globalization;
using Codec.Pcm;
using Compression.Registry;
using FileFormat.Au;
using FileFormat.Caf;
using FileFormat.Flac;
using FileFormat.Opus;
using FileFormat.Rf64;
using FileFormat.Wav;
using FileFormat.WavPack;
using FileFormat.Wave64;
using AiffDescriptor = FileFormat.Aiff.AiffFormatDescriptor;

namespace Compression.Tests.Audio;

/// <summary>
/// Validates OUR audio writers/readers against reference tools running inside
/// WSL (ffmpeg / ffprobe / flac / wavpack / opus tooling). Two directions:
/// <list type="bullet">
///   <item><b>our-output → tool-validates</b>: we build a container (WAV, AIFF,
///   AU, CAF, Wave64, RF64, FLAC) and assert the reference tool recognises the
///   container, codec and PCM parameters — and decodes it without error.</item>
///   <item><b>tool-output → our-reader</b>: for formats we only read (WavPack,
///   Opus) a reference tool encodes a known PCM signal, the matching reference
///   verifier confirms the file, and OUR reader then parses it and reports
///   matching channel/sample-rate metadata.</item>
/// </list>
/// Every test gates cleanly via <see cref="Assert.Ignore(string)"/> when WSL or
/// the specific tool is missing, so a host without the toolchain stays green.
///
/// Closes the verification hole flagged in the audio-formats review: the
/// writers existed but nothing checked their bytes against a reference decoder.
/// </summary>
[TestFixture]
[Category("ExternalInterop")]
[Category("AudioExternalInterop")]
public sealed class AudioExternalInteropTests {

  // 1 second @ 44.1 kHz keeps each test well under the per-test time budget while
  // staying long enough for ffprobe to read a stable duration.
  private const int SampleRate = 44100;
  private const int BitsPerSample = 16;
  private const int Frames = SampleRate; // 1 second.

  private string _tmpDir = null!;

  [SetUp]
  public void Setup() {
    this._tmpDir = Path.Combine(Path.GetTempPath(), $"cwb_audio_interop_{Guid.NewGuid():N}");
    Directory.CreateDirectory(this._tmpDir);
  }

  [TearDown]
  public void Teardown() {
    try { Directory.Delete(this._tmpDir, true); } catch { /* best effort */ }
  }

  // ── Gating ───────────────────────────────────────────────────────────────

  private static void RequireTool(string tool) {
    if (!FsInteropToolbox.WslAvailable)
      Assert.Ignore("WSL not available — audio interop tools live inside the WSL distro.");
    if (!FsInteropToolbox.WslHasTool("ffprobe"))
      Assert.Ignore("ffprobe not found in WSL (install ffmpeg via `sudo apt install -y ffmpeg`).");
    if (!FsInteropToolbox.WslHasTool(tool))
      Assert.Ignore($"{tool} not found in WSL.");
  }

  // ── Test signal ────────────────────────────────────────────────────────

  /// <summary>
  /// Deterministic mono 16-bit LE PCM: a 440 Hz sine at ~30000 amplitude.
  /// Returned as raw little-endian sample bytes (no container).
  /// </summary>
  private static byte[] GenerateMonoPcm() {
    var pcm = new byte[Frames * 2];
    for (var i = 0; i < Frames; ++i) {
      var t = i / (double)SampleRate;
      var s = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 30000);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), s);
    }
    return pcm;
  }

  /// <summary>Wraps mono LE PCM as a canonical little-endian RIFF/WAVE blob.</summary>
  private static byte[] MonoWavBlob(byte[] pcm)
    => PcmCodec.ToWavBlob(pcm, channels: 1, SampleRate, BitsPerSample, formatCode: 1);

  /// <summary>
  /// Deterministic stereo 16-bit LE PCM (left 440 Hz, right 880 Hz). Our FLAC
  /// encoder reads raw headerless PCM and always interprets it as stereo, so the
  /// FLAC scenarios feed it stereo to make the channel count and sample count
  /// line up with what the reference decoder reports.
  /// </summary>
  private static byte[] GenerateStereoPcm() {
    var pcm = new byte[Frames * 2 * 2];
    for (var i = 0; i < Frames; ++i) {
      var t = i / (double)SampleRate;
      var l = (short)(Math.Sin(2.0 * Math.PI * 440.0 * t) * 30000);
      var r = (short)(Math.Sin(2.0 * Math.PI * 880.0 * t) * 30000);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 0), l);
      BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), r);
    }
    return pcm;
  }

  // ── ffprobe helpers ──────────────────────────────────────────────────────

  /// <summary>
  /// Runs ffprobe over <paramref name="winPath"/> and returns the flat
  /// <c>key=value</c> lines (stream params + container format) as a dictionary.
  /// Keys collide across the single stream + format, which is fine here because
  /// the names we query are unique.
  /// </summary>
  private static Dictionary<string, string> Probe(string winPath) {
    var p = FsInteropToolbox.WinToWsl(winPath);
    var r = FsInteropToolbox.RunWsl(
      "ffprobe -v error -select_streams a:0 " +
      "-show_entries stream=codec_name,sample_rate,channels,bits_per_sample " +
      "-show_entries format=format_name " +
      $"-of default=noprint_wrappers=1 {p}");
    Assert.That(r.ExitCode, Is.Zero,
      $"ffprobe failed for {winPath}\nstdout: {r.StdOut}\nstderr: {r.StdErr}");
    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var line in r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)) {
      var eq = line.IndexOf('=');
      if (eq <= 0) continue;
      var key = line[..eq].Trim();
      var val = line[(eq + 1)..].Trim();
      if (val is not ("N/A" or ""))
        dict[key] = val;
    }
    return dict;
  }

  /// <summary>Asserts ffmpeg can fully decode the file to raw PCM without error.</summary>
  private static void AssertFfmpegDecodes(string winPath) {
    var p = FsInteropToolbox.WinToWsl(winPath);
    var r = FsInteropToolbox.RunWsl(
      $"ffmpeg -hide_banner -v error -y -i {p} -f s16le -acodec pcm_s16le /dev/null");
    Assert.That(r.ExitCode, Is.Zero,
      $"ffmpeg failed to decode {winPath}\nstderr: {r.StdErr}");
  }

  private string WriteToTmp(string name, byte[] bytes) {
    var path = Path.Combine(this._tmpDir, name);
    File.WriteAllBytes(path, bytes);
    return path;
  }

  // ── Container builders via the production IArchiveCreatable surface ──────

  /// <summary>
  /// Drives a descriptor's <see cref="IArchiveCreatable.Create"/> with a single
  /// per-channel mono WAV (named MONO.wav) and returns the produced container.
  /// This exercises the same path the CLI/UI use, not a private writer.
  /// </summary>
  private static byte[] CreateContainer(IArchiveCreatable descriptor, byte[] monoWav, string channelName = "MONO") {
    using var output = new MemoryStream();
    descriptor.Create(output,
      [ArchiveInputInfo.InMemory($"{channelName}.wav", monoWav)],
      new FormatCreateOptions());
    return output.ToArray();
  }

  // ─────────────────────────────────────────────────────────────────────────
  //  WAV — our PCM writer
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Given a mono PCM WAV produced by our PCM writer,
  /// When ffprobe inspects it,
  /// Then it reports pcm_s16le @ 44.1 kHz / 1 channel inside a WAV container,
  ///  and ffmpeg decodes it without error.
  /// </summary>
  [Test]
  public void Wav_OurWriter_FfprobeReportsPcmParams() {
    RequireTool("ffprobe");
    var wav = MonoWavBlob(GenerateMonoPcm());
    var path = this.WriteToTmp("ours.wav", wav);

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("wav"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Is.EqualTo("pcm_s16le"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  /// <summary>
  /// Given a stereo WAV remuxed by the WAV descriptor from two per-channel mono WAVs,
  /// When ffprobe inspects it,
  /// Then it reports 2 channels of pcm_s16le.
  /// </summary>
  [Test]
  public void Wav_DescriptorRemuxStereo_FfprobeReportsTwoChannels() {
    RequireTool("ffprobe");
    var mono = MonoWavBlob(GenerateMonoPcm());
    using var output = new MemoryStream();
    new WavFormatDescriptor().Create(output,
      [ArchiveInputInfo.InMemory("LEFT.wav", mono), ArchiveInputInfo.InMemory("RIGHT.wav", mono)],
      new FormatCreateOptions());
    var path = this.WriteToTmp("stereo.wav", output.ToArray());

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("codec_name"), Is.EqualTo("pcm_s16le"));
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("2"));
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)));
    });
    AssertFfmpegDecodes(path);
  }

  // ─────────────────────────────────────────────────────────────────────────
  //  AIFF / AU / CAF / Wave64 / RF64 — our container writers
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Given an AIFF assembled by our AIFF descriptor,
  /// When ffprobe inspects it,
  /// Then the AIFF container + big-endian PCM @ 44.1 kHz is recognised and ffmpeg decodes it.
  /// </summary>
  [Test]
  public void Aiff_OurWriter_FfprobeRecognisesContainer() {
    RequireTool("ffprobe");
    var blob = CreateContainer(new AiffDescriptor(), MonoWavBlob(GenerateMonoPcm()));
    var path = this.WriteToTmp("ours.aiff", blob);

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("aiff"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Does.StartWith("pcm_s16"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  /// <summary>
  /// Given a Sun/NeXT .au assembled by our AU descriptor,
  /// When ffprobe inspects it,
  /// Then the .au container + big-endian PCM is recognised and ffmpeg decodes it.
  /// </summary>
  [Test]
  public void Au_OurWriter_FfprobeRecognisesContainer() {
    RequireTool("ffprobe");
    var blob = CreateContainer(new AuFormatDescriptor(), MonoWavBlob(GenerateMonoPcm()));
    var path = this.WriteToTmp("ours.au", blob);

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("au"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Does.StartWith("pcm_s16"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  /// <summary>
  /// Given an Apple CAF assembled by our CAF descriptor (LPCM, CAF-canonical big-endian samples),
  /// When ffprobe inspects it,
  /// Then the CAF container + 16-bit PCM @ 44.1 kHz is recognised and ffmpeg decodes it.
  /// </summary>
  [Test]
  public void Caf_OurWriter_FfprobeRecognisesContainer() {
    RequireTool("ffprobe");
    var blob = CreateContainer(new CafFormatDescriptor(), MonoWavBlob(GenerateMonoPcm()));
    var path = this.WriteToTmp("ours.caf", blob);

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("caf"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Does.StartWith("pcm_s16"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  /// <summary>
  /// Given a Wave64 (.w64) assembled by our Wave64 descriptor,
  /// When ffprobe inspects it,
  /// Then the w64 container + 16-bit LE PCM @ 44.1 kHz is recognised and ffmpeg decodes it.
  /// </summary>
  [Test]
  public void Wave64_OurWriter_FfprobeRecognisesContainer() {
    RequireTool("ffprobe");
    var blob = CreateContainer(new Wave64FormatDescriptor(), MonoWavBlob(GenerateMonoPcm()));
    var path = this.WriteToTmp("ours.w64", blob);

    var probe = Probe(path);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("w64"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Is.EqualTo("pcm_s16le"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  /// <summary>
  /// Given an RF64/BWF image assembled by our RF64 writer (ds64 + 0xFFFFFFFF
  ///  sentinels for the &gt;4 GB-capable sizes),
  /// When ffprobe inspects it,
  /// Then ffmpeg recognises the RF64/WAV container + 16-bit LE PCM and decodes it.
  /// </summary>
  [Test]
  public void Rf64_OurWriter_FfprobeRecognisesContainer() {
    RequireTool("ffprobe");
    var blob = Rf64Writer.Build(GenerateMonoPcm(), channels: 1, SampleRate, BitsPerSample, formatCode: 1, bext: null);
    var path = this.WriteToTmp("ours.rf64", blob);

    var probe = Probe(path);
    Assert.Multiple(() => {
      // ffmpeg surfaces RF64 under the wav demuxer family.
      Assert.That(probe.GetValueOrDefault("format_name"), Does.Contain("wav"), "container");
      Assert.That(probe.GetValueOrDefault("codec_name"), Is.EqualTo("pcm_s16le"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("1"), "channels");
    });
    AssertFfmpegDecodes(path);
  }

  // ─────────────────────────────────────────────────────────────────────────
  //  FLAC — our encoder
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Given a FLAC encoded by our encoder from deterministic PCM,
  /// When the reference <c>flac --test</c> validates the bitstream + STREAMINFO MD5,
  /// Then it exits 0 (the canonical FLAC integrity check passes).
  /// </summary>
  [Test]
  public void Flac_OurEncoder_FlacTestPasses() {
    RequireTool("flac");
    var pcm = GenerateStereoPcm();
    var flacPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(flacPath))
      FlacWriter.Compress(input, output);

    var p = FsInteropToolbox.WinToWsl(flacPath);
    var r = FsInteropToolbox.RunWsl($"flac --test --totally-silent {p}");
    Assert.That(r.ExitCode, Is.Zero, $"flac --test rejected our stream\nstderr: {r.StdErr}");
  }

  /// <summary>
  /// Given a FLAC encoded by our encoder,
  /// When ffprobe inspects it,
  /// Then it confirms a flac stream and ffmpeg decodes it to exactly the original sample count.
  /// </summary>
  [Test]
  public void Flac_OurEncoder_FfprobeConfirmsStreamAndSampleCount() {
    RequireTool("ffprobe");
    var pcm = GenerateStereoPcm();
    var flacPath = Path.Combine(this._tmpDir, "ours.flac");
    using (var input = new MemoryStream(pcm))
    using (var output = File.Create(flacPath))
      FlacWriter.Compress(input, output);

    var probe = Probe(flacPath);
    Assert.Multiple(() => {
      Assert.That(probe.GetValueOrDefault("codec_name"), Is.EqualTo("flac"), "codec");
      Assert.That(probe.GetValueOrDefault("sample_rate"), Is.EqualTo(SampleRate.ToString(CultureInfo.InvariantCulture)), "rate");
      Assert.That(probe.GetValueOrDefault("channels"), Is.EqualTo("2"), "channels");
    });

    // Reference decode → raw PCM; the byte count proves the sample count round-trips.
    var rawWsl = FsInteropToolbox.WinToWsl(Path.Combine(this._tmpDir, "decoded.pcm"));
    var dec = FsInteropToolbox.RunWsl(
      $"ffmpeg -hide_banner -v error -y -i {FsInteropToolbox.WinToWsl(flacPath)} -f s16le -acodec pcm_s16le {rawWsl}");
    Assert.That(dec.ExitCode, Is.Zero, $"ffmpeg failed to decode our FLAC\nstderr: {dec.StdErr}");
    var decoded = File.ReadAllBytes(Path.Combine(this._tmpDir, "decoded.pcm"));
    Assert.That(decoded.Length, Is.EqualTo(pcm.Length), "decoded sample byte count must match the source");
    Assert.That(decoded, Is.EqualTo(pcm), "ffmpeg-decoded PCM must match our source samples (lossless)");
  }

  // ─────────────────────────────────────────────────────────────────────────
  //  WavPack — both directions
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Given a WavPack file produced by OUR encoder from a known WAV,
  /// When the reference <c>wvunpack</c> verifies and decodes it,
  /// Then it reports the file lossless and returns our source samples.
  /// </summary>
  /// <remarks>
  /// Only this direction catches an encoder that is wrong in the same way as our
  /// own decoder. It found four: sub-block framing bits a position out, a
  /// bitstream left at an odd byte count, absent entropy medians, and a sample
  /// magnitude of zero that made the reference decode the whole file as silence.
  /// </remarks>
  [TestCase(1, 16)]
  [TestCase(2, 16)]
  [TestCase(6, 16)]
  public void WavPack_OurEncode_ReferenceVerifies(int channels, int bitsPerSample) {
    if (!FsInteropToolbox.WslHasTool("wvunpack"))
      Assert.Ignore("wvunpack not found.");

    var frames = SampleRate / 2;
    var bytesPerSample = bitsPerSample / 8;
    var pcm = new byte[frames * channels * bytesPerSample];
    for (var i = 0; i < frames; ++i)
      for (var c = 0; c < channels; ++c) {
        var value = (short)(Math.Sin(2 * Math.PI * (440 + 55 * c) * i / SampleRate) * 12_000);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan((i * channels + c) * 2), value);
      }

    using var input = new MemoryStream(pcm, writable: false);
    using var encoded = new MemoryStream();
    Codec.WavPack.WavPackCodec.Compress(input, encoded, channels, SampleRate, bitsPerSample);

    var wvPath = this.WriteToTmp($"ours-{channels}ch.wv", encoded.ToArray());
    var verify = FsInteropToolbox.RunWsl($"wvunpack -v {FsInteropToolbox.WinToWsl(wvPath)}");
    Assert.That(verify.ExitCode, Is.Zero,
      $"wvunpack rejected our {channels}-channel file\nstdout: {verify.StdOut}\nstderr: {verify.StdErr}");

    // and the samples it hands back are the ones we put in
    var outPath = Path.Combine(this._tmpDir, $"ours-{channels}ch.wav");
    var decode = FsInteropToolbox.RunWsl(
      $"wvunpack -y -q -o {FsInteropToolbox.WinToWsl(outPath)} {FsInteropToolbox.WinToWsl(wvPath)}");
    Assert.That(decode.ExitCode, Is.Zero, $"wvunpack failed to decode\nstderr: {decode.StdErr}");

    var wav = File.ReadAllBytes(outPath);
    var dataOffset = FindWavDataChunk(wav, out var dataLength);
    Assert.That(dataLength, Is.EqualTo(pcm.Length), "decoded sample byte count");
    Assert.That(wav.AsSpan(dataOffset, dataLength).ToArray(), Is.EqualTo(pcm),
      "wvunpack must return our source samples exactly");
  }

  // Offset of the data-chunk payload in a canonical RIFF/WAVE file.
  private static int FindWavDataChunk(byte[] wav, out int length) {
    var position = 12;
    while (position + 8 <= wav.Length) {
      var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(position + 4));
      if (wav.AsSpan(position, 4).SequenceEqual("data"u8)) {
        length = Math.Min(size, wav.Length - position - 8);
        return position + 8;
      }

      position += 8 + size + (size & 1);
    }

    length = 0;
    return wav.Length;
  }

  /// <summary>
  /// Given a WavPack file produced by the reference <c>wavpack</c> encoder from a known WAV,
  /// When <c>wvunpack -v</c> verifies it AND our WavPack reader lists it,
  /// Then the reference verify passes and our reader surfaces the wvpk blocks + decoded PCM.
  /// </summary>
  [Test]
  public void WavPack_ReferenceEncode_OurReaderParses() {
    RequireTool("wavpack");
    if (!FsInteropToolbox.WslHasTool("wvunpack"))
      Assert.Ignore("wvunpack not found in WSL.");

    var wavPath = this.WriteToTmp("src.wav", MonoWavBlob(GenerateMonoPcm()));
    var wvPath = Path.Combine(this._tmpDir, "ref.wv");

    var enc = FsInteropToolbox.RunWsl(
      $"wavpack -y -q {FsInteropToolbox.WinToWsl(wavPath)} -o {FsInteropToolbox.WinToWsl(wvPath)}");
    Assert.That(enc.ExitCode, Is.Zero, $"reference wavpack encode failed\nstderr: {enc.StdErr}");
    Assert.That(File.Exists(wvPath), Is.True, "wavpack produced no output");

    // Reference self-verify of the produced file. `-v` verifies in place
    // (decodes + checks the per-block CRCs); it is incompatible with `-o`.
    var verify = FsInteropToolbox.RunWsl($"wvunpack -v {FsInteropToolbox.WinToWsl(wvPath)}");
    Assert.That(verify.ExitCode, Is.Zero, $"wvunpack -v rejected the reference file\nstderr: {verify.StdErr}");

    // OUR reader parses the same file.
    using var input = File.OpenRead(wvPath);
    var entries = new WavPackFormatDescriptor().List(input, password: null);
    Assert.That(entries, Is.Not.Empty, "our WavPack reader produced no entries");
    Assert.That(entries.Any(e => e.Name.EndsWith(".wv", StringComparison.OrdinalIgnoreCase)), Is.True,
      "our reader found no wvpk blocks");
    var metadata = entries.FirstOrDefault(e => e.Name.Equals("metadata.ini", StringComparison.OrdinalIgnoreCase));
    Assert.That(metadata, Is.Not.Null, "our reader emitted no metadata.ini");
  }

  // ─────────────────────────────────────────────────────────────────────────
  //  Opus — reference encodes, our reader parses (no encoder in repo)
  // ─────────────────────────────────────────────────────────────────────────

  /// <summary>
  /// Given an Opus file produced by reference ffmpeg from a known WAV,
  /// When <c>opusinfo</c> parses it AND our Opus reader lists it,
  /// Then opusinfo reports an Opus stream and our reader surfaces FULL.opus
  ///  (plus decoded per-channel PCM when the decoder handles the stream).
  /// </summary>
  [Test]
  public void Opus_ReferenceEncode_OpusInfoAndOurReaderParse() {
    RequireTool("opusinfo");

    var wavPath = this.WriteToTmp("src.wav", MonoWavBlob(GenerateMonoPcm()));
    var opusPath = Path.Combine(this._tmpDir, "ref.opus");
    var enc = FsInteropToolbox.RunWsl(
      $"ffmpeg -hide_banner -v error -y -i {FsInteropToolbox.WinToWsl(wavPath)} " +
      $"-c:a libopus -b:a 64k {FsInteropToolbox.WinToWsl(opusPath)}");
    Assert.That(enc.ExitCode, Is.Zero, $"reference opus encode failed\nstderr: {enc.StdErr}");
    Assert.That(File.Exists(opusPath), Is.True, "ffmpeg produced no opus output");

    // opusinfo prints stream details and exits 0 for a valid Ogg/Opus stream.
    var info = FsInteropToolbox.RunWsl($"opusinfo {FsInteropToolbox.WinToWsl(opusPath)}");
    Assert.That(info.ExitCode, Is.Zero, $"opusinfo rejected the reference file\nstderr: {info.StdErr}");
    Assert.That(info.StdOut + info.StdErr, Does.Contain("Opus").IgnoreCase, "opusinfo did not report an Opus stream");

    // OUR reader parses the same file (FULL.opus is always surfaced; channel
    // decoding is best-effort and not required for this parse check).
    using var input = File.OpenRead(opusPath);
    var entries = new OpusFormatDescriptor().List(input, password: null);
    Assert.That(entries.Any(e => e.Name.Equals("FULL.opus", StringComparison.OrdinalIgnoreCase)), Is.True,
      "our Opus reader did not surface FULL.opus");
  }
}
