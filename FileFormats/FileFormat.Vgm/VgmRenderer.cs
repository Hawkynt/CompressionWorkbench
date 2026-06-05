#pragma warning disable CS1591
using System.Buffers.Binary;
using Codec.Pcm;
using Codec.Sn76489;
using Codec.Ym2612;
using Compression.Registry;

namespace FileFormat.Vgm;

/// <summary>
/// Executes a VGM command stream over the SN76489 PSG and YM2612 OPN2 synthesis cores and
/// surfaces the rendered stereo tune as per-channel <c>LEFT.wav</c>/<c>RIGHT.wav</c> entries.
/// <para>The command subset handled covers everything a PSG/YM2612 tune needs:
/// <c>0x4F</c> GG-stereo, <c>0x50</c> PSG write, <c>0x52</c>/<c>0x53</c> YM2612 port-0/1,
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
  private const int Ym2612Offset = 0x2C;

  // Every chip-clock field other than the two we render; any nonzero entry blocks rendering.
  private static readonly (int Offset, string Label)[] UnsupportedClocks = [
    (0x10, "YM2413"), (0x30, "YM2151"), (0x38, "SegaPCM"), (0x40, "RF5C68"),
    (0x44, "YM2203"), (0x48, "YM2608"), (0x4C, "YM2610"), (0x50, "YM3812"),
    (0x54, "YM3526"), (0x58, "Y8950"), (0x5C, "YMF262"), (0x60, "YMF278B"),
    (0x64, "YMF271"), (0x68, "YMZ280B"), (0x6C, "RF5C164"), (0x70, "PWM"),
    (0x74, "AY8910"),
  ];

  public static void AddRenderedChannels(
      byte[] blob, uint version, uint totalSamples, int dataOffset, int commandsEnd,
      List<AudioPseudoArchive.Entry> entries) {
    try {
      var snClock = ReadClock(blob, Sn76489Offset);
      var ymClock = ReadClock(blob, Ym2612Offset);

      // A blocking chip → keep the metadata-only view but record why.
      var blocker = FindUnsupportedChip(blob, version);
      if (blocker != null) {
        entries.Add(new("render.txt", "Tag",
          System.Text.Encoding.UTF8.GetBytes(
            $"rendering skipped: unsupported chip clock present ({blocker})\n")));
        return;
      }

      if (snClock == 0 && ymClock == 0)
        return; // nothing this core can voice

      var pcm = Render(blob, dataOffset, commandsEnd, totalSamples, snClock, ymClock);
      if (pcm.Length == 0)
        return;

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
      byte[] blob, int dataOffset, int commandsEnd, uint totalSamples, uint snClock, uint ymClock) {
    var targetSamples = totalSamples == 0
      ? MaxSamples
      : Math.Min(totalSamples, MaxSamples);

    var psg = snClock != 0 ? new Sn76489Codec(snClock) : null;
    var ym = ymClock != 0 ? new Ym2612Codec(ymClock) : null;

    // YM2612 native rate → 44100 resample accumulator (simple ratio stepping).
    var ymStep = ym != null ? ym.NativeSampleRate / SampleRate : 0.0;

    var output = new List<short>(capacity: (int)Math.Min(targetSamples, 1 << 20) * 2);

    // PSG renders into a small reusable buffer (a Span can't be captured by the local helper).
    var psgBuffer = new short[2];

    // YM resampler state.
    double ymAccumulator = 0;
    short ymLeft = 0, ymRight = 0;

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
        case 0x52: // YM2612 port 0
        case 0x53: // YM2612 port 1
          if (pos + 1 < commandsEnd) {
            var addr = blob[pos++];
            var val = blob[pos++];
            ym?.Write(cmd == 0x52 ? 0 : 1, addr, val);
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
