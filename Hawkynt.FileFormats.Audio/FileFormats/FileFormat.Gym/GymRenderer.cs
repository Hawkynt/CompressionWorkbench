#pragma warning disable CS1591
using Codec.Pcm;
using Codec.Sn76489;
using Codec.Ym2612;
using Compression.Registry;

namespace FileFormat.Gym;

/// <summary>
/// Executes a Genesis GYM register log over the YM2612 + SN76489 cores and surfaces the
/// rendered stereo tune as <c>LEFT.wav</c>/<c>RIGHT.wav</c>.
/// <para>The command grammar is: <c>0x00</c> wait one 1/60 s frame (735 samples at 44100 Hz),
/// <c>0x01 aa dd</c> YM2612 port-0 write, <c>0x02 aa dd</c> YM2612 port-1 write, <c>0x03 dd</c>
/// PSG write. The render is capped at <see cref="MaxSeconds"/>.</para>
/// </summary>
internal static class GymRenderer {

  private const int SampleRate = 44100;
  private const int MaxSeconds = 600;
  private const long MaxSamples = (long)SampleRate * MaxSeconds;
  private const int SamplesPerFrame = 735;     // 44100 / 60

  // Genesis chip clocks (NTSC Mega Drive).
  private const double Ym2612Clock = 7670454.0;
  private const double PsgClock = 3579545.0;

  public static void AddRenderedChannels(byte[] log, List<AudioPseudoArchive.Entry> entries) {
    try {
      var pcm = Render(log);
      if (pcm.Length == 0)
        return;
      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels: 2, SampleRate, bitsPerSample: 16))
        entries.Add(new($"{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Synthesis failure leaves the metadata + log view intact.
    }
  }

  private static byte[] Render(byte[] log) {
    var ym = new Ym2612Codec(Ym2612Clock);
    var psg = new Sn76489Codec(PsgClock);
    var ymStep = ym.NativeSampleRate / SampleRate;

    var output = new List<short>();
    double ymAccumulator = 0;
    short ymLeft = 0, ymRight = 0;
    var psgBuffer = new short[2];

    long rendered = 0;
    var pos = 0;

    void EmitFrame() {
      for (var i = 0; i < SamplesPerFrame && rendered < MaxSamples; ++i, ++rendered) {
        ymAccumulator += ymStep;
        while (ymAccumulator >= 1.0) {
          ymAccumulator -= 1.0;
          ym.RenderSample(out ymLeft, out ymRight);
        }
        psg.RenderSamples(psgBuffer, 1);
        output.Add(Clamp16(ymLeft + psgBuffer[0]));
        output.Add(Clamp16(ymRight + psgBuffer[1]));
      }
    }

    while (pos < log.Length && rendered < MaxSamples) {
      var cmd = log[pos++];
      switch (cmd) {
        case 0x00: // wait one frame
          EmitFrame();
          break;
        case 0x01: // YM2612 port 0 write
          if (pos + 1 < log.Length) {
            ym.Write(0, log[pos], log[pos + 1]);
            pos += 2;
          } else {
            pos = log.Length;
          }
          break;
        case 0x02: // YM2612 port 1 write
          if (pos + 1 < log.Length) {
            ym.Write(1, log[pos], log[pos + 1]);
            pos += 2;
          } else {
            pos = log.Length;
          }
          break;
        case 0x03: // PSG write
          if (pos < log.Length)
            psg.Write(log[pos++]);
          else
            pos = log.Length;
          break;
        default:
          // Unknown byte: ignore (GYM has no other defined commands).
          break;
      }
    }

    var bytes = new byte[output.Count * 2];
    for (var i = 0; i < output.Count; ++i)
      System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), output[i]);
    return bytes;
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
