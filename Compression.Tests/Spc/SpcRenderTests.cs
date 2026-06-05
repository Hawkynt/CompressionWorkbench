#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Codec.Brr;
using Codec.Spc700;
using FileFormat.Spc;

namespace Compression.Tests.Spc;

/// <summary>
/// End-to-end rendering tests: a synthetic SPC is assembled in memory (an idle SPC700 program
/// plus a DSP pre-set that keys on one voice playing a BRR-encoded sine), rendered to stereo,
/// and checked for the expected fundamental (via autocorrelation), VOL panning, ID666 length
/// honouring, and graceful degradation on garbage.
/// </summary>
[TestFixture]
public class SpcRenderTests {

  private const int FileSize = 0x10180;
  private const int AramOffset = 0x100;
  private const int DspOffset = 0x10100;
  private const int SongLengthOffset = 0xA9;

  // Pitch 0x0800 = half a sample per tick → a sine period of N source samples renders at 2N
  // output samples. We choose the BRR sample period so the rendered fundamental is predictable.
  private const int SamplePeriod = 32; // source samples per sine cycle

  private static byte[] BuildRenderableSpc(byte volL = 0x7F, byte volR = 0x7F, int? lengthSeconds = null) {
    var spc = new byte[FileSize];
    "SNES-SPC700 Sound File Data v0.30"u8.CopyTo(spc);
    spc[0x21] = 0x1A; spc[0x22] = 0x1A; spc[0x23] = 0x1A;

    // CPU registers: PC at $0200, idle loop "BRA -2" (0x2F 0xFE) so the CPU never disturbs ARAM.
    BinaryPrimitives.WriteUInt16LittleEndian(spc.AsSpan(0x25), 0x0200);
    spc[0x2B] = 0xFF; // SP

    // ID666 song length (3 ASCII digits) when requested.
    if (lengthSeconds is { } secs)
      Encoding.ASCII.GetBytes(secs.ToString("D3")).CopyTo(spc.AsSpan(SongLengthOffset));

    var aram = spc.AsSpan(AramOffset, 0x10000);
    aram[0x0200] = 0x2F; aram[0x0201] = 0xFE; // BRA -2 idle loop

    // BRR sample: several cycles of a sine, looped. BRR's recursive filters make a generic
    // loop boundary drift over many iterations (as on real hardware), so the fundamental is
    // analysed within the first loop where the tone is pristine.
    var pcm = new short[SamplePeriod * 8];
    for (var i = 0; i < pcm.Length; ++i)
      pcm[i] = (short)(Math.Sin(2 * Math.PI * i / SamplePeriod) * 16000);
    var brr = BrrCodec.Encode(pcm);
    // Set the loop flag on the final (end-flagged) block so the voice loops instead of keying
    // off, giving a steady tone to analyse.
    brr[^BrrCodec.BlockSize] |= 0x02;
    const int sampleAddr = 0x1000;
    brr.CopyTo(aram[sampleAddr..]);

    // Sample directory at page 2.
    const int dir = 0x02;
    var dirBase = dir * 0x100;
    BinaryPrimitives.WriteUInt16LittleEndian(aram[dirBase..], sampleAddr);
    BinaryPrimitives.WriteUInt16LittleEndian(aram[(dirBase + 2)..], sampleAddr); // loop = start

    // DSP register pre-set.
    var dsp = spc.AsSpan(DspOffset, 128);
    dsp[0x00] = volL;  // voice 0 VOL L
    dsp[0x01] = volR;  // voice 0 VOL R
    dsp[0x02] = 0x00;  // pitch low
    dsp[0x03] = 0x08;  // pitch high → 0x0800 (half-rate)
    dsp[0x04] = 0x00;  // SRCN
    dsp[0x05] = 0x00;  // ADSR1: ADSR off → use GAIN
    dsp[0x07] = 0x7F;  // GAIN: direct max
    dsp[0x5D] = dir;   // DIR
    dsp[0x0C] = 0x7F;  // MVOL L
    dsp[0x1C] = 0x7F;  // MVOL R
    dsp[0x6C] = 0x20;  // FLG: echo writes disabled (bit5), no reset/mute
    dsp[0x4C] = 0x01;  // KON voice 0

    return spc;
  }

  [Test]
  public void Render_ProducesFundamentalAtExpectedPeriod() {
    var spc = BuildRenderableSpc();
    var player = new SpcPlayer(spc);
    var (left, _) = player.RenderStereoChannels();
    var samples = ToShorts(left);

    // Analyse within the first loop (past the attack onset, before loop-boundary drift).
    var window = samples.Skip(100).Take(400).ToArray();
    var period = DominantPeriod(window, 8, 200);

    // Pitch 0x0800 = half-rate, so the rendered period is ~2× the source period (64 samples).
    Assert.That(period, Is.InRange(SamplePeriod * 2 - 8, SamplePeriod * 2 + 8),
      "the rendered tone matches the BRR sample fundamental scaled by the playback pitch");
  }

  [Test]
  public void Render_RespectsVolPanning() {
    // Hard-pan right: VOL L = 0, VOL R = max → left channel silent, right channel loud.
    var spc = BuildRenderableSpc(volL: 0x00, volR: 0x7F);
    var player = new SpcPlayer(spc);
    var (left, right) = player.RenderStereoChannels();

    var leftEnergy = Energy(ToShorts(left));
    var rightEnergy = Energy(ToShorts(right));
    Assert.That(rightEnergy, Is.GreaterThan(leftEnergy * 10),
      "panning to the right leaves the left channel near silent");
  }

  [Test]
  public void Render_HonoursId666SongLength() {
    var spc = BuildRenderableSpc(lengthSeconds: 5);
    var player = new SpcPlayer(spc);
    Assert.That(player.DurationSeconds, Is.EqualTo(5));
    Assert.That(player.DurationFromTag, Is.True);
    var (left, _) = player.RenderStereoChannels();
    Assert.That(left.Length, Is.EqualTo(5 * SpcPlayer.SampleRate * 2));
  }

  [Test]
  public void Render_DefaultsToThirtySecondsWithoutTag() {
    var spc = BuildRenderableSpc();
    var player = new SpcPlayer(spc);
    Assert.That(player.DurationSeconds, Is.EqualTo(30));
    Assert.That(player.DurationFromTag, Is.False);
  }

  [Test]
  public void Descriptor_SurfacesLeftAndRightChannels() {
    var spc = BuildRenderableSpc(lengthSeconds: 2);
    using var ms = new MemoryStream(spc);
    var entries = new SpcFormatDescriptor().List(ms, null);

    var channels = entries.Where(e => e.Kind == "Channel").ToList();
    Assert.That(channels.Any(e => e.Name == "LEFT.wav"), Is.True);
    Assert.That(channels.Any(e => e.Name == "RIGHT.wav"), Is.True);
  }

  [Test]
  public void Descriptor_MetadataRecordsRenderedDuration() {
    var spc = BuildRenderableSpc(lengthSeconds: 7);
    using var ms = new MemoryStream(spc);
    using var output = new MemoryStream();
    new SpcFormatDescriptor().ExtractEntry(ms, "metadata.ini", output, null);
    var ini = Encoding.UTF8.GetString(output.ToArray());
    Assert.That(ini, Does.Contain("rendered_seconds=7"));
    Assert.That(ini, Does.Contain("rendered_source=id666"));
  }

  [Test]
  public void ShortBlob_RenderDegradesGracefully() {
    using var ms = new MemoryStream(new byte[0x100]);
    var entries = new SpcFormatDescriptor().List(ms, null);
    Assert.That(entries.Any(e => e.Name == "FULL.spc"), Is.True);
    Assert.That(entries.Any(e => e.Kind == "Channel"), Is.False, "no render from a truncated blob");
  }

  // ── helpers ──

  private static short[] ToShorts(byte[] pcm) {
    var s = new short[pcm.Length / 2];
    for (var i = 0; i < s.Length; ++i)
      s[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
    return s;
  }

  private static double Energy(short[] s) {
    double e = 0;
    foreach (var v in s)
      e += (double)v * v;
    return e / Math.Max(1, s.Length);
  }

  /// <summary>Finds the lag (in samples) that maximises autocorrelation in [minLag,maxLag].</summary>
  private static int DominantPeriod(short[] s, int minLag, int maxLag) {
    var bestLag = minLag;
    var best = double.MinValue;
    for (var lag = minLag; lag <= maxLag && lag < s.Length; ++lag) {
      double sum = 0;
      for (var i = 0; i + lag < s.Length; ++i)
        sum += (double)s[i] * s[i + lag];
      if (sum > best) { best = sum; bestLag = lag; }
    }
    return bestLag;
  }
}
