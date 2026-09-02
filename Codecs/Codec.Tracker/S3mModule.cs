#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.Tracker;

/// <summary>
/// Parses a Scream Tracker 3 (S3M) module into the shared <see cref="TrackerSong"/>
/// model and renders it. Handles the packed pattern stream, PCM (type 1) instruments
/// with their C2SPD, the channel-settings table (muted/disabled channels), default
/// 0x3/0xC panning and the optional 32-byte pan section.
/// </summary>
/// <remarks>Layout and effects per the official Scream Tracker 3 TECH.DOC.</remarks>
public static class S3mModule {

  /// <summary>
  /// Renders the S3M to interleaved stereo 16-bit PCM at <paramref name="outputRate"/>,
  /// or null if the blob is not a usable S3M.
  /// </summary>
  public static (byte[] Pcm, double Seconds)? Render(byte[] blob, int outputRate, double maxSeconds) {
    var song = TryParse(blob);
    if (song == null)
      return null;
    var player = new S3mPlayer(song, outputRate);
    var pcm = player.Render(maxSeconds);
    return (pcm, pcm.Length / 4.0 / outputRate);
  }

  /// <summary>
  /// Performs the estimate seconds operation.
  /// </summary>
public static double? EstimateSeconds(byte[] blob) {
    var song = TryParse(blob);
    return song == null ? null : SongLength.Estimate(song);
  }

  /// <summary>Decoded mono samples (1-based instrument index → 16-bit PCM and C2SPD), or null.</summary>
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
    if (blob.Length < 96 || blob[44] != 'S' || blob[45] != 'C' || blob[46] != 'R' || blob[47] != 'M')
      return null;

    var songLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2));
    var globalVol = blob[48];
    var initialSpeed = blob[49];
    var initialTempo = blob[50];
    var masterVolFlags = blob[51];
    var defaultPan = blob[53]; // 0xFC → pan section present
    var hasPanSection = defaultPan == 0xFC;

    if (initialSpeed == 0) initialSpeed = 6;
    if (initialTempo < 0x20) initialTempo = 125;

    // 32 channel settings at offset 64.
    var channelSettings = new byte[32];
    Array.Copy(blob, 64, channelSettings, 0, 32);

    // Determine active channel count: highest enabled channel index + 1.
    var maxChannel = 0;
    for (var i = 0; i < 32; ++i)
      if ((channelSettings[i] & 0x80) == 0) // bit7 set = disabled
        maxChannel = i + 1;
    var channels = Math.Max(1, maxChannel);

    var orderOff = 96;
    var order = new int[songLen];
    for (var i = 0; i < songLen; ++i)
      order[i] = blob[orderOff + i];

    var instrParaOff = orderOff + songLen;
    var patternParaOff = instrParaOff + numInstruments * 2;

    // Optional 32-byte pan section follows the pattern parapointers.
    var panSectionOff = patternParaOff + numPatterns * 2;

    // Instruments.
    var samples = new TrackerSample?[numInstruments + 1];
    for (var s = 0; s < numInstruments; ++s) {
      var ip = instrParaOff + s * 2;
      if (ip + 2 > blob.Length) break;
      var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(ip, 2));
      if (para == 0) continue;
      var off = para * 16;
      if (off + 80 > blob.Length) continue;
      if (blob[off] != 1) continue; // type 1 = PCM
      var memSegHi = blob[off + 13];
      var memSegLo = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 14, 2));
      var memSeg = (memSegHi << 16) | memSegLo;
      var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 16, 4));
      var loopStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 20, 4));
      var loopEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 24, 4));
      var volume = Math.Clamp(blob[off + 28], (byte)0, (byte)64);
      var flags = blob[off + 31];
      var c2spd = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 32, 4));
      if (c2spd <= 0) c2spd = 8363;
      var loops = (flags & 0x01) != 0;
      var is16Bit = (flags & 0x04) != 0;
      var dataOff = memSeg * 16;
      if (length <= 0 || dataOff <= 0 || dataOff >= blob.Length) continue;

      var pcm = new short[length];
      if (is16Bit) {
        for (var i = 0; i < length; ++i) {
          var bo = dataOff + i * 2;
          if (bo + 2 > blob.Length) break;
          // S3M 16-bit samples are unsigned by default; bias to signed.
          var u = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(bo, 2));
          pcm[i] = (short)(u - 32768);
        }
      } else {
        for (var i = 0; i < length; ++i) {
          var bo = dataOff + i;
          if (bo >= blob.Length) break;
          // S3M 8-bit PCM is unsigned (0..255, centre 128); convert to signed 16-bit.
          pcm[i] = (short)((blob[bo] - 128) << 8);
        }
      }

      samples[s + 1] = new TrackerSample {
        Data = pcm,
        LoopStart = loops ? Math.Min(loopStart, length) : 0,
        LoopLength = loops ? Math.Max(0, Math.Min(loopEnd, length) - loopStart) : 0,
        DefaultVolume = volume,
        BaseRate = c2spd,
        FineTune = 0,
      };
    }

    // Patterns.
    var patterns = new TrackerPattern[Math.Max(1, (int)numPatterns)];
    for (var p = 0; p < patterns.Length; ++p) {
      var cells = new TrackerCell[64 * channels];
      for (var i = 0; i < cells.Length; ++i)
        cells[i] = new TrackerCell();
      var pat = new TrackerPattern { Rows = 64, Channels = channels, Cells = cells };
      if (p < numPatterns) {
        var pp = patternParaOff + p * 2;
        if (pp + 2 <= blob.Length) {
          var para = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pp, 2));
          if (para != 0)
            UnpackPattern(blob, para * 16, pat);
        }
      }
      patterns[p] = pat;
    }

    // Panning.
    var pan = new int[channels];
    for (var ch = 0; ch < channels; ++ch) {
      var setting = channelSettings[ch];
      // Channel type < 8 = left group, 8..15 = right group (the 0x3/0xC default scheme).
      var type = setting & 0x7F;
      var basePan = type < 8 ? 0x30 : 0xC0; // ~left / ~right in 0..255
      if (hasPanSection && panSectionOff + ch < blob.Length) {
        var pv = blob[panSectionOff + ch];
        if ((pv & 0x20) != 0)
          basePan = (pv & 0x0F) * 17; // 0..15 → 0..255
      }
      pan[ch] = basePan;
    }

    var muted = new bool[channels];
    for (var ch = 0; ch < channels; ++ch)
      muted[ch] = (channelSettings[ch] & 0x80) != 0;

    _ = masterVolFlags;
    return new TrackerSong {
      Kind = TrackerKind.S3m,
      Channels = channels,
      Order = order.Length == 0 ? [0] : order,
      Patterns = patterns,
      Samples = samples,
      InitialSpeed = initialSpeed,
      InitialTempo = initialTempo,
      GlobalVolume = Math.Clamp((int)globalVol, 0, 64),
      ChannelPan = pan,
      ChannelMuted = muted,
    };
  }

  /// <summary>
  /// Unpacks one packed S3M pattern. Each row is a sequence of (whatByte, fields…) tokens
  /// terminated by a zero <c>what</c> byte; bit5 = note+instrument, bit6 = volume, bit7 = effect.
  /// </summary>
  private static void UnpackPattern(byte[] blob, int off, TrackerPattern pat) {
    if (off + 2 > blob.Length)
      return;
    var length = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off, 2));
    var pos = off + 2;
    var end = Math.Min(off + length, blob.Length);
    var row = 0;
    while (pos < end && row < pat.Rows) {
      var what = blob[pos++];
      if (what == 0) {
        ++row;
        continue;
      }
      var channel = what & 0x1F;
      var hasNote = (what & 0x20) != 0;
      var hasVol = (what & 0x40) != 0;
      var hasEffect = (what & 0x80) != 0;

      int note = 0, instrument = 0, volume = -1, effect = 0, param = 0;
      if (hasNote) {
        if (pos + 1 >= end) break;
        var noteByte = blob[pos++];
        instrument = blob[pos++];
        note = ConvertNote(noteByte);
      }
      if (hasVol) {
        if (pos >= end) break;
        volume = Math.Clamp((int)blob[pos++], 0, 64);
      }
      if (hasEffect) {
        if (pos + 1 >= end) break;
        effect = blob[pos++];
        param = blob[pos++];
      }

      if (channel < pat.Channels) {
        ref var cell = ref pat.Cell(row, channel);
        cell.Note = note;
        cell.Instrument = instrument;
        cell.Volume = volume;
        cell.Effect = effect;
        cell.EffectParam = param;
      }
    }
  }

  /// <summary>
  /// Converts an S3M note byte to a 1-based semitone index. The byte packs octave in
  /// the high nibble and note (0..11) in the low nibble; 255 = empty, 254 = note off.
  /// </summary>
  private static int ConvertNote(byte noteByte) {
    if (noteByte == 255)
      return 0;
    if (noteByte == 254)
      return 254; // note off
    var octave = noteByte >> 4;
    var semitone = noteByte & 0x0F;
    return octave * 12 + semitone + 1;
  }
}
