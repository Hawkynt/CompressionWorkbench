#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Opl;
using Codec.Pcm;
using Codec.Sn76489;
using Codec.Ym2151;
using Codec.Ym2203;
using Codec.Ym2413;
using Codec.Ym2608;
using Codec.Ym2612;
using Compression.Registry;

namespace FileFormat.Vgm;

/// <summary>
/// Executes a VGM command stream over the SN76489 PSG and YM2612 OPN2 synthesis cores and
/// surfaces the rendered stereo tune as per-channel <c>LEFT.wav</c>/<c>RIGHT.wav</c> entries.
/// <para>The command subset handled covers everything a PSG/YM2612 tune needs:
/// <c>0x4F</c> GG-stereo, <c>0x50</c> PSG write, <c>0x51 aa dd</c> YM2413 (OPLL) write,
/// <c>0x52</c>/<c>0x53</c> YM2612 port-0/1, the OPL FM family
/// (<c>0x5A</c> YM3812, <c>0x5B</c> YM3526, <c>0x5C</c> Y8950, <c>0x5E</c>/<c>0x5F</c> YMF262 OPL3),
/// <c>0x61 nn nn</c> wait-n, <c>0x62</c> wait 735 (1/60 s), <c>0x63</c> wait 882 (1/50 s),
/// <c>0x66</c> end, <c>0x67</c> data block (stored for DAC streaming), <c>0x70-0x7F</c> short
/// waits (n+1 samples), <c>0x80-0x8F</c> YM2612 DAC write + wait via the data-block pointer,
/// and <c>0xE0</c> seek the DAC pointer. The stream is rendered once (no loop expansion) and
/// capped at <see cref="MaxSeconds"/>.</para>
/// </summary>
internal static class VgmRenderer {

  private const int SampleRate = 44100;
  private const int MaxSeconds = 600;
  private const long MaxSamples = (long)SampleRate * MaxSeconds;

  // Chip-clock header fields we can synthesise.
  private const int Sn76489Offset = 0x0C;
  private const int Ym2413Offset = 0x10;
  private const int Ym2612Offset = 0x2C;
  private const int Ym2151Offset = 0x30;
  private const int Ym2203Offset = 0x44;
  private const int Ym2608Offset = 0x48;
  // The Yamaha OPL FM family (VGM spec 1.51+ header offsets).
  private const int Ym3812Offset = 0x50;   // OPL2
  private const int Ym3526Offset = 0x54;   // OPL
  private const int Y8950Offset = 0x58;    // OPL + ADPCM (MSX-Audio)
  private const int Ymf262Offset = 0x5C;   // OPL3

  // Every chip-clock field other than the ones we render; any nonzero entry blocks rendering.
  private static readonly (int Offset, string Label)[] UnsupportedClocks = [
    (0x38, "SegaPCM"), (0x40, "RF5C68"), (0x4C, "YM2610"), (0x60, "YMF278B"),
    (0x64, "YMF271"), (0x68, "YMZ280B"), (0x6C, "RF5C164"), (0x70, "PWM"),
    (0x74, "AY8910"),
  ];

  public static void AddRenderedChannels(
      byte[] blob, uint version, uint totalSamples, int dataOffset, int commandsEnd,
      List<AudioPseudoArchive.Entry> entries) {
    try {
      var snClock = ReadClock(blob, Sn76489Offset);
      var ymClock = ReadClock(blob, Ym2612Offset);
      var opllClock = ReadClock(blob, Ym2413Offset);
      var opmClock = ReadClock(blob, Ym2151Offset);
      var opnClock = ReadClock(blob, Ym2203Offset);
      var opnaClock = ReadClock(blob, Ym2608Offset);
      var opl2Clock = ReadClock(blob, Ym3812Offset);   // YM3812
      var oplClock = ReadClock(blob, Ym3526Offset);    // YM3526
      var y8950Clock = ReadClock(blob, Y8950Offset);   // Y8950
      var opl3Clock = ReadClock(blob, Ymf262Offset);   // YMF262

      // A blocking chip → keep the metadata-only view but record why.
      var blocker = FindUnsupportedChip(blob, version);
      if (blocker != null) {
        entries.Add(new("render.txt", "Tag",
          System.Text.Encoding.UTF8.GetBytes(
            $"rendering skipped: unsupported chip clock present ({blocker})\n")));
        return;
      }

      var anyOpl = opl2Clock != 0 || oplClock != 0 || y8950Clock != 0 || opl3Clock != 0;
      if (snClock == 0 && ymClock == 0 && opllClock == 0 && opmClock == 0 && opnClock == 0 && opnaClock == 0 && !anyOpl)
        return; // nothing this core can voice

      var pcm = Render(blob, dataOffset, commandsEnd, totalSamples,
        snClock, ymClock, opllClock, opmClock, opnClock, opnaClock,
        opl2Clock, oplClock, y8950Clock, opl3Clock, out var gatedNote);
      if (pcm.Length == 0)
        return;

      if (gatedNote != null)
        entries.Add(new("render.txt", "Tag", System.Text.Encoding.UTF8.GetBytes(gatedNote + "\n")));

      // Record which chips actually drove the rendered mix.
      var chips = new List<string>(10);
      if (snClock != 0) chips.Add("SN76489");
      if (ymClock != 0) chips.Add("YM2612");
      if (opllClock != 0) chips.Add("YM2413");
      if (opmClock != 0) chips.Add("YM2151");
      if (opnClock != 0) chips.Add("YM2203");
      if (opnaClock != 0) chips.Add("YM2608");
      if (oplClock != 0) chips.Add("YM3526");
      if (opl2Clock != 0) chips.Add("YM3812");
      if (y8950Clock != 0) chips.Add("Y8950");
      if (opl3Clock != 0) chips.Add("YMF262");
      entries.Add(new("rendered.ini", "Tag",
        System.Text.Encoding.UTF8.GetBytes($"rendered_chips={string.Join(",", chips)}\n")));

      foreach (var (name, wav) in PcmCodec.SplitInterleavedPcm(pcm, channels: 2, SampleRate, bitsPerSample: 16))
        entries.Add(new($"{name}.wav", "Channel", wav, Method: "pcm"));
    } catch {
      // Any parsing/synthesis failure leaves the metadata-only view intact.
    }
  }

  private static string? FindUnsupportedChip(byte[] blob, uint version) {
    foreach (var (offset, label) in UnsupportedClocks)
      if (ReadClock(blob, offset) != 0)
        return label;
    return null;
  }

  private static byte[] Render(
      byte[] blob, int dataOffset, int commandsEnd, uint totalSamples,
      uint snClock, uint ymClock, uint opllClock, uint opmClock, uint opnClock, uint opnaClock,
      uint opl2Clock, uint oplClock, uint y8950Clock, uint opl3Clock,
      out string? gatedNote) {
    gatedNote = null;
    var targetSamples = totalSamples == 0
      ? MaxSamples
      : Math.Min(totalSamples, MaxSamples);

    var psg = snClock != 0 ? new Sn76489Codec(snClock) : null;
    var ym = ymClock != 0 ? new Ym2612Codec(ymClock) : null;
    var opll = opllClock != 0 ? new Ym2413Codec(opllClock) : null;
    var opm = opmClock != 0 ? new Ym2151Codec(opmClock) : null;
    var opn = opnClock != 0 ? new Ym2203Codec(opnClock) : null;
    var opna = opnaClock != 0 ? new Ym2608Codec(opnaClock) : null;

    // The OPL FM family: YM3526 (OPL), YM3812 (OPL2), Y8950 (OPL+ADPCM), YMF262 (OPL3). The VGM
    // commands map one-to-one to a chip instance (0x5B/0x5A/0x5C/0x5E+0x5F respectively).
    var opl = oplClock != 0 ? new OplCodec(OplCodec.Chip.Opl, oplClock) : null;
    var opl2 = opl2Clock != 0 ? new OplCodec(OplCodec.Chip.Opl2, opl2Clock) : null;
    var y8950 = y8950Clock != 0 ? new OplCodec(OplCodec.Chip.Y8950, y8950Clock) : null;
    var opl3 = opl3Clock != 0 ? new OplCodec(OplCodec.Chip.Opl3, opl3Clock) : null;

    // YM2612 native rate → 44100 resample accumulator (simple ratio stepping).
    var ymStep = ym != null ? ym.NativeSampleRate / SampleRate : 0.0;
    // YM2413 (OPLL) native rate (clock / 72) → 44100 resample accumulator.
    var opllStep = opll != null ? opll.NativeSampleRate / SampleRate : 0.0;
    // YM2151 (OPM) native rate (clock / 64) → 44100 resample accumulator.
    var opmStep = opm != null ? opm.NativeSampleRate / SampleRate : 0.0;
    // YM2203 / YM2608 FM native rates → 44100 resample accumulators.
    var opnStep = opn != null ? opn.FmSampleRate / SampleRate : 0.0;
    var opnaStep = opna != null ? opna.FmSampleRate / SampleRate : 0.0;
    // OPL family native rate (clock / 72) → 44100 resample accumulators (per chip instance).
    var oplStep = opl != null ? opl.NativeSampleRate / SampleRate : 0.0;
    var opl2Step = opl2 != null ? opl2.NativeSampleRate / SampleRate : 0.0;
    var y8950Step = y8950 != null ? y8950.NativeSampleRate / SampleRate : 0.0;
    var opl3Step = opl3 != null ? opl3.NativeSampleRate / SampleRate : 0.0;

    var output = new List<short>(capacity: (int)Math.Min(targetSamples, 1 << 20) * 2);

    // PSG renders into a small reusable buffer (a Span can't be captured by the local helper).
    var psgBuffer = new short[2];

    // YM resampler state.
    double ymAccumulator = 0;
    short ymLeft = 0, ymRight = 0;

    // OPLL resampler state (mono).
    double opllAccumulator = 0;
    short opllMono = 0;

    // OPM resampler state (stereo).
    double opmAccumulator = 0;
    short opmLeft = 0, opmRight = 0;

    // OPN (YM2203) resampler state: FM is mono, SSG is rendered at 44100 directly.
    double opnAccumulator = 0;
    short opnFm = 0;
    var opnSsg = new short[1];

    // OPNA (YM2608) resampler state: FM stereo, SSG rendered at 44100 directly.
    double opnaAccumulator = 0;
    short opnaFmLeft = 0, opnaFmRight = 0;
    var opnaSsg = new short[1];

    // OPL family resampler state (each chip is stereo: OPL3 pans, others mono→both sides).
    double oplAccumulator = 0, opl2Accumulator = 0, y8950Accumulator = 0, opl3Accumulator = 0;
    short oplL = 0, oplR = 0, opl2L = 0, opl2R = 0;
    short y8950L = 0, y8950R = 0, opl3L = 0, opl3R = 0;

    // DAC data blocks (type 0 PCM) concatenated, addressed by a running pointer (0xE0 seeks).
    var dataBlock = new List<byte>();
    var dacPointer = 0;

    long rendered = 0;
    var pos = dataOffset;

    void EmitSamples(long count) {
      for (long i = 0; i < count && rendered < targetSamples; ++i, ++rendered) {
        var l = 0;
        var r = 0;
        if (ym != null) {
          // Advance the YM native clock until we cross one output sample.
          ymAccumulator += ymStep;
          while (ymAccumulator >= 1.0) {
            ymAccumulator -= 1.0;
            ym.RenderSample(out ymLeft, out ymRight);
          }
          l += ymLeft;
          r += ymRight;
        }
        if (psg != null) {
          psg.RenderSamples(psgBuffer, 1);
          l += psgBuffer[0];
          r += psgBuffer[1];
        }
        if (opll != null) {
          opllAccumulator += opllStep;
          while (opllAccumulator >= 1.0) {
            opllAccumulator -= 1.0;
            opllMono = opll.RenderSample();
          }
          // OPLL is mono; feed both stereo sides at half level.
          l += opllMono / 2;
          r += opllMono / 2;
        }
        if (opm != null) {
          opmAccumulator += opmStep;
          while (opmAccumulator >= 1.0) {
            opmAccumulator -= 1.0;
            opm.RenderSample(out opmLeft, out opmRight);
          }
          l += opmLeft;
          r += opmRight;
        }
        if (opn != null) {
          // YM2203 FM (mono, resampled) + SSG (mono at 44100) → both stereo sides.
          opnAccumulator += opnStep;
          while (opnAccumulator >= 1.0) {
            opnAccumulator -= 1.0;
            opnFm = opn.RenderFmSample();
          }
          opn.RenderSsgSamples(opnSsg, 1);
          var mono = opnFm + opnSsg[0];
          l += mono;
          r += mono;
        }
        if (opna != null) {
          // YM2608 FM (stereo, resampled) + SSG (mono at 44100, both sides).
          opnaAccumulator += opnaStep;
          while (opnaAccumulator >= 1.0) {
            opnaAccumulator -= 1.0;
            opna.RenderFmSample(out opnaFmLeft, out opnaFmRight);
          }
          opna.RenderSsgSamples(opnaSsg, 1);
          l += opnaFmLeft + opnaSsg[0];
          r += opnaFmRight + opnaSsg[0];
        }
        if (opl != null) {
          oplAccumulator += oplStep;
          while (oplAccumulator >= 1.0) { oplAccumulator -= 1.0; opl.RenderSample(out oplL, out oplR); }
          l += oplL; r += oplR;
        }
        if (opl2 != null) {
          opl2Accumulator += opl2Step;
          while (opl2Accumulator >= 1.0) { opl2Accumulator -= 1.0; opl2.RenderSample(out opl2L, out opl2R); }
          l += opl2L; r += opl2R;
        }
        if (y8950 != null) {
          y8950Accumulator += y8950Step;
          while (y8950Accumulator >= 1.0) { y8950Accumulator -= 1.0; y8950.RenderSample(out y8950L, out y8950R); }
          l += y8950L; r += y8950R;
        }
        if (opl3 != null) {
          opl3Accumulator += opl3Step;
          while (opl3Accumulator >= 1.0) { opl3Accumulator -= 1.0; opl3.RenderSample(out opl3L, out opl3R); }
          l += opl3L; r += opl3R;
        }
        output.Add(Clamp16(l));
        output.Add(Clamp16(r));
      }
    }

    while (pos < commandsEnd && rendered < targetSamples) {
      var cmd = blob[pos++];
      switch (cmd) {
        case 0x4F: // GG stereo
          if (pos < commandsEnd) psg?.WriteStereo(blob[pos++]);
          break;
        case 0x50: // PSG write
          if (pos < commandsEnd) psg?.Write(blob[pos++]);
          break;
        case 0x51: // YM2413 (OPLL) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opll?.WriteRegister(addr, val);
          }
          break;
        case 0x52: // YM2612 port 0
        case 0x53: // YM2612 port 1
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            ym?.Write(cmd == 0x52 ? 0 : 1, addr, val);
          }
          break;
        case 0x54: // YM2151 (OPM) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opm?.WriteRegister(addr, val);
          }
          break;
        case 0x55: // YM2203 (OPN) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opn?.Write(addr, val);
          }
          break;
        case 0x56: // YM2608 (OPNA) port 0 write: aa dd
        case 0x57: // YM2608 (OPNA) port 1 write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opna?.Write(cmd == 0x56 ? 0 : 1, addr, val);
          }
          break;
        case 0x5A: // YM3812 (OPL2) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opl2?.WriteRegister(0, addr, val);
          }
          break;
        case 0x5B: // YM3526 (OPL) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opl?.WriteRegister(0, addr, val);
          }
          break;
        case 0x5C: // Y8950 (OPL + ADPCM) write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            y8950?.WriteRegister(0, addr, val);
          }
          break;
        case 0x5E: // YMF262 (OPL3) port 0 write: aa dd
        case 0x5F: // YMF262 (OPL3) port 1 write: aa dd
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            opl3?.WriteRegister(cmd == 0x5E ? 0 : 1, addr, val);
          }
          break;
        case 0x61: // wait n (16-bit little-endian)
          if (pos + 1 < commandsEnd) {
            var n = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
            pos += 2;
            EmitSamples(n);
          }
          break;
        case 0x62: // wait 1/60 s
          EmitSamples(735);
          break;
        case 0x63: // wait 1/50 s
          EmitSamples(882);
          break;
        case 0x66: // end of stream
          pos = commandsEnd;
          break;
        case 0x67: // data block: 0x66 tt ss ss ss ss <data>
          pos = ReadDataBlock(blob, pos, commandsEnd, dataBlock);
          break;
        case 0xE0: // seek DAC data pointer (32-bit)
          if (pos + 3 < commandsEnd) {
            dacPointer = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos, 4));
            pos += 4;
          }
          break;
        default:
          if (cmd is >= 0x70 and <= 0x7F) {
            // Short wait: (n & 0x0F) + 1 samples.
            EmitSamples((cmd & 0x0F) + 1);
          } else if (cmd is >= 0x80 and <= 0x8F) {
            // DAC write from the data block + wait (n & 0x0F) samples.
            if (ym != null && dacPointer < dataBlock.Count) {
              ym.Write(0, 0x2A, dataBlock[dacPointer]);
              ++dacPointer;
            }
            EmitSamples(cmd & 0x0F);
          } else {
            // Unknown command: skip its operand bytes conservatively (most are 0/1/2 bytes).
            pos += OperandLength(cmd);
          }
          break;
      }
    }

    // The YM2608 rhythm (ADPCM-A) ROM and the ADPCM-B delta-T sample are not modelled; if the
    // stream drove either, surface a note so the gap is explicit rather than silent.
    if (opna != null && (opna.RhythmRequested || opna.AdpcmBRequested)) {
      var parts = new List<string>(2);
      if (opna.RhythmRequested) parts.Add("rhythm (ADPCM-A)");
      if (opna.AdpcmBRequested) parts.Add("ADPCM-B delta-T");
      gatedNote = $"YM2608 {string.Join(" and ", parts)} not rendered (no internal/streamed sample ROM); FM+SSG only.";
    }

    return ToBytes(output);
  }

  // 0x67 0x66 tt ssssssss <data>: returns the position after the embedded block.
  private static int ReadDataBlock(byte[] blob, int pos, int end, List<byte> dataBlock) {
    if (pos >= end || blob[pos] != 0x66)
      return pos;
    ++pos; // 0x66 compatibility byte
    if (pos >= end) return pos;
    var type = blob[pos++];
    if (pos + 3 >= end) return end;
    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pos, 4));
    pos += 4;
    var available = Math.Min(size, end - pos);
    // Type 0x00 = uncompressed PCM stream used by the 0x8x DAC writes.
    if (type == 0x00)
      for (var i = 0; i < available; ++i)
        dataBlock.Add(blob[pos + i]);
    return pos + available;
  }

  // Conservative operand length for VGM commands we don't act on (keeps the cursor aligned).
  private static int OperandLength(byte cmd) => cmd switch {
    >= 0x30 and <= 0x3F => 1,
    >= 0x40 and <= 0x4E => 2,
    0x51 or 0x54 or 0x55 or 0x56 or 0x57 or 0x58 or 0x59 or 0x5A or 0x5B or 0x5C or 0x5D or 0x5E or 0x5F => 2,
    >= 0xA0 and <= 0xBF => 2,
    >= 0xC0 and <= 0xDF => 3,
    >= 0xE1 and <= 0xFF => 4,
    _ => 0,
  };

  private static uint ReadClock(byte[] blob, int offset)
    => offset + 4 <= blob.Length
      ? BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset, 4)) & 0x3FFFFFFF
      : 0u;

  private static byte[] ToBytes(List<short> samples) {
    var bytes = new byte[samples.Count * 2];
    for (var i = 0; i < samples.Count; ++i)
      BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
    return bytes;
  }

  private static short Clamp16(int value) =>
    value > 32767 ? (short)32767 : value < -32768 ? (short)-32768 : (short)value;
}
