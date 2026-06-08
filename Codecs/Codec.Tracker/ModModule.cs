#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Tracker;

/// <summary>
/// Parses a ProTracker / SoundTracker MOD into the shared <see cref="TrackerSong"/>
/// model and renders it. Supports 4..32 channels via the M.K. / xCHN / xxCH magics,
/// 31-instrument modules, the standard 64-row pattern layout and signed 8-bit PCM
/// samples (converted to 16-bit). Channel detection mirrors the descriptor.
/// </summary>
/// <remarks>Layout per the FireLight "fmoddoc" MOD documentation.</remarks>
public static class ModModule {

  /// <summary>Standard PAL replay rate at C-2 (period 428) used to tag exported sample WAVs.</summary>
  public const int SampleC2Rate = 8287;

  /// <summary>
  /// Renders the MOD to interleaved stereo 16-bit PCM at <paramref name="outputRate"/>,
  /// returning the PCM plus its duration, or null if the blob is not a usable MOD.
  /// </summary>
  public static (byte[] Pcm, double Seconds)? Render(byte[] blob, int outputRate, double maxSeconds) {
    var song = TryParse(blob);
    if (song == null)
      return null;
    var player = new ModPlayer(song, outputRate);
    var pcm = player.Render(maxSeconds);
    var seconds = pcm.Length / 4.0 / outputRate;
    return (pcm, seconds);
  }

  /// <summary>Deterministic song length in seconds, or null if unparseable.</summary>
  public static double? EstimateSeconds(byte[] blob) {
    var song = TryParse(blob);
    return song == null ? null : SongLength.Estimate(song);
  }

  /// <summary>
  /// Returns the decoded mono samples (1-based instrument index → 16-bit PCM and rate),
  /// or null when unparseable. Index 0 is null (samples are 1-based in the format).
  /// </summary>
  public static IReadOnlyList<(short[] Pcm, int Rate)?>? DecodeSamples(byte[] blob) {
    var song = TryParse(blob);
    if (song == null)
      return null;
    var result = new List<(short[] Pcm, int Rate)?> { null };
    for (var i = 1; i < song.Samples.Length; ++i) {
      var s = song.Samples[i];
      result.Add(s == null ? null : (s.Data, s.BaseRate));
    }
    return result;
  }

  internal static TrackerSong? TryParse(byte[] blob) {
    try {
      return Parse(blob);
    } catch {
      return null;
    }
  }

  private static TrackerSong? Parse(byte[] blob) {
    if (blob.Length < 1084)
      return null;

    var sig = System.Text.Encoding.ASCII.GetString(blob, 1080, 4);
    var channels = ChannelsForSignature(sig);
    if (channels <= 0 || channels > 32)
      return null;

    // Sample headers: 31 entries of 30 bytes from offset 20.
    var samples = new TrackerSample?[32];
    var lengths = new int[32];
    for (var s = 0; s < 31; ++s) {
      var off = 20 + s * 30;
      var words = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(off + 22, 2));
      lengths[s + 1] = words * 2;
      var fineTune = blob[off + 24] & 0x0F;
      var volume = Math.Clamp(blob[off + 25], (byte)0, (byte)64);
      var loopStartWords = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(off + 26, 2));
      var loopLenWords = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(off + 28, 2));
      samples[s + 1] = new TrackerSample {
        Data = [],
        LoopStart = loopStartWords * 2,
        LoopLength = loopLenWords * 2,
        DefaultVolume = volume,
        FineTune = fineTune,
        BaseRate = RateForFineTune(fineTune),
      };
    }

    var songLen = blob[950];
    var restart = blob[951];

    // Order table at 952, 128 bytes.
    var order = new int[songLen];
    var maxPattern = 0;
    for (var i = 0; i < songLen && i < 128; ++i) {
      order[i] = blob[952 + i];
      if (order[i] > maxPattern) maxPattern = order[i];
    }
    // Some modules reference higher patterns than songLen implies; scan all 128.
    for (var i = 0; i < 128; ++i)
      if (blob[952 + i] > maxPattern) maxPattern = blob[952 + i];
    var numPatterns = maxPattern + 1;

    var patternBytes = 64 * channels * 4;
    var patternsStart = 1084;
    if (patternsStart + numPatterns * patternBytes > blob.Length)
      numPatterns = Math.Max(0, (blob.Length - patternsStart) / patternBytes);

    var patterns = new TrackerPattern[Math.Max(1, numPatterns)];
    for (var p = 0; p < patterns.Length; ++p) {
      var cells = new TrackerCell[64 * channels];
      for (var i = 0; i < cells.Length; ++i)
        cells[i] = new TrackerCell();
      var pat = new TrackerPattern { Rows = 64, Channels = channels, Cells = cells };
      if (p < numPatterns) {
        var off = patternsStart + p * patternBytes;
        for (var row = 0; row < 64; ++row) {
          for (var ch = 0; ch < channels; ++ch) {
            var cellOff = off + (row * channels + ch) * 4;
            DecodeCell(blob, cellOff, ref pat.Cell(row, ch));
          }
        }
      }
      patterns[p] = pat;
    }

    // Sample PCM follows the patterns.
    var sampleOff = patternsStart + numPatterns * patternBytes;
    for (var s = 1; s <= 31; ++s) {
      var len = lengths[s];
      if (len <= 0 || samples[s] is not { } smp)
        continue;
      if (sampleOff >= blob.Length)
        break;
      var take = Math.Min(len, blob.Length - sampleOff);
      var pcm = new short[take];
      for (var i = 0; i < take; ++i)
        pcm[i] = (short)((sbyte)blob[sampleOff + i] << 8);
      smp.Data = pcm;
      // Clamp loop window to the actual data length.
      if (smp.LoopStart > take) smp.LoopStart = 0;
      if (smp.LoopStart + smp.LoopLength > take) smp.LoopLength = Math.Max(0, take - smp.LoopStart);
      sampleOff += len;
    }

    var pan = new int[channels];
    for (var ch = 0; ch < channels; ++ch)
      pan[ch] = AmigaPanForChannel(ch);

    return new TrackerSong {
      Kind = TrackerKind.Mod,
      Channels = channels,
      Order = order.Length == 0 ? [0] : order,
      Patterns = patterns,
      Samples = samples,
      InitialSpeed = 6,
      InitialTempo = 125,
      GlobalVolume = 64,
      ChannelPan = pan,
      ChannelMuted = new bool[channels],
      RestartPosition = restart,
    };
  }

  private static void DecodeCell(byte[] blob, int off, ref TrackerCell cell) {
    if (off + 4 > blob.Length)
      return;
    var b0 = blob[off];
    var b1 = blob[off + 1];
    var b2 = blob[off + 2];
    var b3 = blob[off + 3];
    var period = ((b0 & 0x0F) << 8) | b1;
    var instrument = (b0 & 0xF0) | (b2 >> 4);
    var effect = b2 & 0x0F;
    var param = b3;
    cell.Period = period;
    cell.Instrument = instrument;
    cell.Effect = effect;
    cell.EffectParam = param;
    cell.Volume = -1;
  }

  /// <summary>
  /// Amiga LRRL panning: channels alternate L,R,R,L per group of four. ch0/ch3 → left,
  /// ch1/ch2 → right; wider channel counts repeat the pattern.
  /// </summary>
  private static int AmigaPanForChannel(int channel) {
    var inGroup = channel & 0x03;
    var left = inGroup is 0 or 3;
    return left ? 64 : 191; // ~25% / ~75% as ProTracker's hard-ish pan
  }

  private static int ChannelsForSignature(string sig) {
    switch (sig) {
      case "M.K.":
      case "M!K!":
      case "FLT4":
      case "4CHN":
        return 4;
      case "6CHN":
        return 6;
      case "8CHN":
      case "FLT8":
      case "CD81":
      case "OKTA":
        return 8;
    }
    // xCHN (e.g. 2CHN..9CHN) and xxCH (e.g. 16CH, 32CH).
    if (sig.Length == 4) {
      if (sig.EndsWith("CHN") && char.IsDigit(sig[0]))
        return sig[0] - '0';
      if (sig.EndsWith("CH") && char.IsDigit(sig[0]) && char.IsDigit(sig[1]))
        return (sig[0] - '0') * 10 + (sig[1] - '0');
    }
    return 4; // SoundTracker / unknown → 4 channels
  }

  /// <summary>
  /// Replay rate for a finetune nibble at the period-428 reference note (note index 12
  /// in the period table, the conventional C-2 sample rate of ~8287 Hz at finetune 0):
  /// the PAL clock divided by twice that period.
  /// </summary>
  internal static int RateForFineTune(int fineTuneNibble) {
    var row = AmigaPeriods.FineTuneToRow(fineTuneNibble);
    var period = AmigaPeriods.PeriodFor(12, row); // period-428 reference note (finetune 0)
    return period <= 0 ? SampleC2Rate : (int)Math.Round(AmigaPeriods.FrequencyForPeriod(period));
  }
}
