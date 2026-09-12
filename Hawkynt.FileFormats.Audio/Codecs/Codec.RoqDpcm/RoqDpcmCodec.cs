#pragma warning disable CS1591
namespace Codec.RoqDpcm;

/// <summary>
/// RoQ DPCM, the audio coding inside id Software's RoQ video container (Quake III,
/// the <c>.roq</c> cinematics). It is a square-table differential PCM: each payload
/// byte is a signed delta whose magnitude is squared before being added to the running
/// predictor. The decode table is
/// <c>table[b] = (b &lt; 128) ? b*b : -((b - 128) * (b - 128))</c>, i.e. the low 7 bits
/// give the magnitude root and bit 7 the sign, so a one-byte code spans the full
/// ±16129 delta range with finer resolution near zero.
/// <para>
/// RoQ sound chunks carry the initial predictor in the chunk's 16-bit argument. Mono
/// chunks initialise the single predictor from the whole argument; stereo chunks split
/// it — the high byte seeds the left predictor (<c>arg &amp; 0xFF00</c>) and the low byte
/// the right (<c>(arg &amp; 0xFF) &lt;&lt; 8</c>) — and the payload bytes then alternate
/// L, R. Streams are 22050 Hz.
/// </para>
/// The encoder picks, for each sample, the table entry whose resulting predictor is
/// nearest the target, so a round-trip is near-lossless for slowly varying signals.
/// </summary>
public static class RoqDpcmCodec {

  /// <summary>Sample rate of RoQ audio chunks.</summary>
  public const int SampleRate = 22050;

  private static readonly short[] DeltaTable = BuildTable();

  private static short[] BuildTable() {
    var table = new short[256];
    for (var b = 0; b < 256; ++b)
      table[b] = b < 128 ? (short)(b * b) : (short)(-((b - 128) * (b - 128)));
    return table;
  }

  /// <summary>
  /// Decodes a RoQ sound-chunk payload into interleaved signed 16-bit PCM.
  /// <paramref name="initialArg"/> is the chunk's 16-bit argument carrying the initial
  /// predictor(s); <paramref name="stereo"/> selects the mono (id 0x1020) or stereo
  /// (id 0x1021) layout.
  /// </summary>
  public static short[] Decode(ReadOnlySpan<byte> payload, ushort initialArg, bool stereo) {
    if (!stereo) {
      var predictor = (short)initialArg;
      var mono = new short[payload.Length];
      for (var i = 0; i < payload.Length; ++i) {
        predictor = ClampAdd(predictor, DeltaTable[payload[i]]);
        mono[i] = predictor;
      }
      return mono;
    }

    var left = (short)(initialArg & 0xFF00);
    var right = (short)((initialArg & 0x00FF) << 8);
    var output = new short[payload.Length]; // already interleaved L,R,L,R…
    for (var i = 0; i + 1 < payload.Length; i += 2) {
      left = ClampAdd(left, DeltaTable[payload[i]]);
      right = ClampAdd(right, DeltaTable[payload[i + 1]]);
      output[i] = left;
      output[i + 1] = right;
    }
    return output;
  }

  /// <summary>
  /// Encodes interleaved signed 16-bit PCM into a RoQ sound-chunk payload, returning the
  /// payload bytes and the initial 16-bit argument to store in the chunk header. The
  /// initial predictor(s) are taken from the first sample(s) and quantised to the byte
  /// granularity the header can hold; subsequent bytes are chosen greedily.
  /// </summary>
  public static (byte[] Payload, ushort InitialArg) Encode(ReadOnlySpan<short> pcm, bool stereo) {
    if (!stereo) {
      // Mono: the argument seeds the predictor directly (16-bit).
      var predictor = pcm.Length > 0 ? pcm[0] : (short)0;
      var initialArg = (ushort)predictor;
      var payload = new byte[pcm.Length];
      var running = predictor;
      // First sample is reproduced exactly via the seeded predictor, encoded as delta 0.
      for (var i = 0; i < pcm.Length; ++i) {
        var code = NearestCode(running, pcm[i]);
        running = ClampAdd(running, DeltaTable[code]);
        payload[i] = code;
      }
      return (payload, initialArg);
    }

    // Stereo: the header only carries 8 bits of precision per channel (high byte).
    var leftSeed = pcm.Length > 0 ? (short)(pcm[0] & 0xFF00) : (short)0;
    var rightSeed = pcm.Length > 1 ? (short)(pcm[1] & 0xFF00) : (short)0;
    var initial = (ushort)(((leftSeed >> 8) & 0xFF) << 8 | ((rightSeed >> 8) & 0xFF));
    var outLen = pcm.Length & ~1;
    var stereoPayload = new byte[outLen];
    var l = leftSeed;
    var r = rightSeed;
    for (var i = 0; i + 1 < pcm.Length; i += 2) {
      var lc = NearestCode(l, pcm[i]);
      l = ClampAdd(l, DeltaTable[lc]);
      stereoPayload[i] = lc;
      var rc = NearestCode(r, pcm[i + 1]);
      r = ClampAdd(r, DeltaTable[rc]);
      stereoPayload[i + 1] = rc;
    }
    return (stereoPayload, initial);
  }

  // Picks the code whose decoded delta brings the predictor closest to the target.
  private static byte NearestCode(short predictor, short target) {
    var bestCode = 0;
    var bestErr = int.MaxValue;
    for (var b = 0; b < 256; ++b) {
      var candidate = ClampAdd(predictor, DeltaTable[b]);
      var err = Math.Abs(candidate - target);
      if (err >= bestErr)
        continue;
      bestErr = err;
      bestCode = b;
      if (err == 0) break;
    }
    return (byte)bestCode;
  }

  private static short ClampAdd(short predictor, short delta) {
    var v = predictor + delta;
    return (short)(v < -32768 ? -32768 : v > 32767 ? 32767 : v);
  }
}
