#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.TrackerXmIt;

/// <summary>
/// A parsed IT sample (IMPS header) with its PCM expanded to signed 16-bit. Handles 8/16-bit,
/// signed/unsigned, and IT214/IT215 compression via <see cref="ItSampleDecompressor"/>. Stereo
/// samples (flag 0x04) are downmixed to mono for the engine (documented limitation).
/// </summary>
public sealed class ItSample {

    /// <summary>
  /// Provides the name value.
  /// </summary>
public string Name = "";
    /// <summary>
  /// Provides the global volume value.
  /// </summary>
public int GlobalVolume = 64;
    /// <summary>
  /// Provides the default volume value.
  /// </summary>
public int DefaultVolume = 64;
    /// <summary>
  /// Provides the default pan value.
  /// </summary>
public int DefaultPan = 32;   // 0..64, bit7 = use
    /// <summary>
  /// Provides the use pan value.
  /// </summary>
public bool UsePan;
    /// <summary>
  /// Provides the c 5 speed value.
  /// </summary>
public int C5Speed = 8363;
    /// <summary>
  /// Provides the loop value.
  /// </summary>
public bool Loop;
    /// <summary>
  /// Provides the ping pong value.
  /// </summary>
public bool PingPong;
    /// <summary>
  /// Provides the sustain loop value.
  /// </summary>
public bool SustainLoop;
    /// <summary>
  /// Provides the sustain ping pong value.
  /// </summary>
public bool SustainPingPong;
    /// <summary>
  /// Provides the loop start value.
  /// </summary>
public int LoopStart;
    /// <summary>
  /// Provides the loop end value.
  /// </summary>
public int LoopEnd;
    /// <summary>
  /// Provides the sustain start value.
  /// </summary>
public int SustainStart;
    /// <summary>
  /// Provides the sustain end value.
  /// </summary>
public int SustainEnd;
    /// <summary>
  /// Provides the pcm value.
  /// </summary>
public short[] Pcm = [];
    /// <summary>
  /// Provides the vibrato speed and vibrato depth and vibrato rate and vibrato type value.
  /// </summary>
public int VibratoSpeed, VibratoDepth, VibratoRate, VibratoType;

    /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public static ItSample Parse(byte[] blob, int off) {
    var s = new ItSample();
    if (off <= 0 || off + 80 > blob.Length) return s;
    if (!(blob[off] == 'I' && blob[off + 1] == 'M' && blob[off + 2] == 'P' && blob[off + 3] == 'S'))
      return s;

    s.Name = ItModule.ReadAscii(blob, off + 20, 26);
    s.GlobalVolume = blob[off + 17];
    var flags = blob[off + 18];
    s.DefaultVolume = blob[off + 19];
    var cvt = blob[off + 46];
    var dfp = blob[off + 47];
    s.UsePan = (dfp & 0x80) != 0;
    s.DefaultPan = dfp & 0x7F;

    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 48, 4));
    s.LoopStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 52, 4));
    s.LoopEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 56, 4));
    s.C5Speed = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 60, 4));
    if (s.C5Speed <= 0) s.C5Speed = 8363;
    s.SustainStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 64, 4));
    s.SustainEnd = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 68, 4));
    var dataPtr = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(off + 72, 4));

    s.VibratoSpeed = blob[off + 76];
    s.VibratoDepth = blob[off + 77];
    s.VibratoRate = blob[off + 78];
    s.VibratoType = blob[off + 79];

    var hasData = (flags & 0x01) != 0;
    var is16 = (flags & 0x02) != 0;
    var stereo = (flags & 0x04) != 0;
    var compressed = (flags & 0x08) != 0;
    s.Loop = (flags & 0x10) != 0;
    s.SustainLoop = (flags & 0x20) != 0;
    s.PingPong = (flags & 0x40) != 0;
    s.SustainPingPong = (flags & 0x80) != 0;
    var signed = (cvt & 0x01) != 0;
    var it215 = (cvt & 0x04) != 0;

    if (!hasData || length <= 0 || dataPtr <= 0 || dataPtr >= blob.Length)
      return s;

    if (compressed) {
      try {
        var stream = blob.AsSpan(dataPtr, blob.Length - dataPtr);
        s.Pcm = is16
          ? ItSampleDecompressor.Decompress16(stream, length, it215)
          : Array.ConvertAll(ItSampleDecompressor.Decompress8(stream, length, it215), v => (short)(v << 8));
      } catch {
        s.Pcm = [];
      }
      return s;
    }

    s.Pcm = DecodePcm(blob, dataPtr, length, is16, signed, stereo);
    return s;
  }

  private static short[] DecodePcm(byte[] blob, int dataPtr, int length, bool is16, bool signed, bool stereo) {
    var channels = stereo ? 2 : 1;
    var bytesPerFrame = (is16 ? 2 : 1) * channels;
    var available = (blob.Length - dataPtr) / bytesPerFrame;
    var frames = Math.Min(length, available);
    var pcm = new short[frames];

    for (var i = 0; i < frames; ++i) {
      long acc = 0;
      for (var c = 0; c < channels; ++c) {
        short v;
        if (is16) {
          var o = dataPtr + (i * channels + c) * 2;
          var raw = (ushort)(blob[o] | (blob[o + 1] << 8));
          v = signed ? unchecked((short)raw) : unchecked((short)(raw - 32768));
        } else {
          var o = dataPtr + i * channels + c;
          var raw = blob[o];
          v = signed ? (short)(unchecked((sbyte)raw) << 8) : (short)((raw - 128) << 8);
        }
        acc += v;
      }
      pcm[i] = (short)(acc / channels);
    }
    return pcm;
  }
}
