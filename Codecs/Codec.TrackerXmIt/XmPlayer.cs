#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.TrackerXmIt;

/// <summary>
/// A software player for FastTracker II Extended Module (<c>.xm</c>) files that renders the song
/// to interleaved stereo 16-bit PCM.
/// </summary>
/// <remarks>
/// <para>
/// Frequency handling follows the official XM.TXT (Triton) specification: linear frequency tables
/// use <c>period = 7680 - note*64 - finetune/2</c> and
/// <c>freq = 8363 * 2^((4608 - period) / 768)</c>; Amiga frequency tables use the classic
/// period-table interpolation (<c>freq = 8363 * 1712 / period</c>). Volume/panning envelopes,
/// auto-vibrato, fadeout, the volume column and effects 0..X are interpreted per XM.TXT, with
/// OpenMPT / libxmp consulted for ambiguous edge cases (e.g. volume-column semantics, multi-retrig
/// <c>Rxy</c> volume operations, and Ex sub-command memory).
/// </para>
/// <para>
/// Pragmatic scope: this targets a faithful musical rendering rather than cycle-exact mixing.
/// Sample interpolation is nearest-neighbour, the mixer is a straightforward additive accumulator,
/// and a few rarely-used niceties (per-sample relative-panning quirks, MIDI macros) are omitted.
/// </para>
/// </remarks>
public sealed class XmPlayer {

  private readonly XmModule _mod;
  private readonly int _sampleRate;

  private XmPlayer(XmModule mod, int sampleRate) {
    this._mod = mod;
    this._sampleRate = sampleRate;
  }

  /// <summary>Parses an XM module from its raw bytes. Throws on malformed input.</summary>
  public static XmPlayer Load(byte[] blob, int sampleRate = TrackerRender.OutputSampleRate)
    => new(XmModule.Parse(blob), sampleRate);

  /// <summary>The parsed module (song name, channel count, etc.).</summary>
  public XmModule Module => this._mod;

  /// <summary>
  /// Renders the song to interleaved stereo 16-bit PCM (LE), stopping at a detected order loop or
  /// the deterministic cap (whichever comes first).
  /// </summary>
  public byte[] Render(double maxSeconds = TrackerRender.MaxSeconds) {
    var engine = new XmEngine(this._mod, this._sampleRate);
    return engine.Render(maxSeconds);
  }

  /// <summary>
  /// Deterministic song length in seconds: walks the order list playing each row, detecting a
  /// revisited (order, row) to stop, capped at <see cref="TrackerRender.MaxSeconds"/>.
  /// </summary>
  public double EstimateSeconds() {
    var engine = new XmEngine(this._mod, this._sampleRate);
    return engine.EstimateSeconds(TrackerRender.MaxSeconds);
  }
}

/// <summary>Parsed XM module structure.</summary>
public sealed class XmModule {

  /// <summary>
  /// Provides the song name value.
  /// </summary>
public string SongName = "";
  /// <summary>
  /// Provides the tracker name value.
  /// </summary>
public string TrackerName = "";
  /// <summary>
  /// Provides the channel count value.
  /// </summary>
public int ChannelCount;
  /// <summary>
  /// Provides the song length value.
  /// </summary>
public int SongLength;
  /// <summary>
  /// Provides the restart position value.
  /// </summary>
public int RestartPosition;
  /// <summary>
  /// Provides the default tempo value.
  /// </summary>
public int DefaultTempo;   // ticks per row (XM "tempo")
  /// <summary>
  /// Provides the default bpm value.
  /// </summary>
public int DefaultBpm;     // BPM
  /// <summary>
  /// Provides the linear frequency value.
  /// </summary>
public bool LinearFrequency;
  /// <summary>
  /// Provides the order value.
  /// </summary>
public byte[] Order = [];
  /// <summary>
  /// Provides the patterns value.
  /// </summary>
public XmPattern[] Patterns = [];
  /// <summary>
  /// Provides the instruments value.
  /// </summary>
public XmInstrument[] Instruments = [];

  /// <summary>Parses the XM header, patterns and instruments. Throws on malformed input.</summary>
  public static XmModule Parse(byte[] blob) {
    if (blob.Length < 80) throw new InvalidDataException("XM too short.");
    if (!blob.AsSpan(0, 17).SequenceEqual("Extended Module: "u8))
      throw new InvalidDataException("Not an XM file.");

    var mod = new XmModule {
      SongName = ReadAscii(blob, 17, 20),
      TrackerName = ReadAscii(blob, 38, 20),
    };

    var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(60, 4));
    mod.SongLength = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(64, 2));
    mod.RestartPosition = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(66, 2));
    mod.ChannelCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(68, 2));
    var numPatterns = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(70, 2));
    var numInstruments = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(72, 2));
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(74, 2));
    mod.LinearFrequency = (flags & 0x01) != 0;
    mod.DefaultTempo = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(76, 2));
    mod.DefaultBpm = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(78, 2));
    if (mod.DefaultTempo <= 0) mod.DefaultTempo = 6;
    if (mod.DefaultBpm <= 0) mod.DefaultBpm = 125;
    if (mod.ChannelCount is <= 0 or > 64) throw new InvalidDataException("XM channel count out of range.");

    var orderLen = Math.Min(mod.SongLength, 256);
    var order = new byte[orderLen];
    Array.Copy(blob, 80, order, 0, Math.Min(orderLen, Math.Max(0, blob.Length - 80)));
    mod.Order = order;

    var cursor = 60 + headerSize;

    var patterns = new XmPattern[numPatterns];
    for (var p = 0; p < numPatterns; ++p) {
      if (cursor + 9 > blob.Length) { patterns[p] = XmPattern.Empty(mod.ChannelCount); continue; }
      var patHdrLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor, 4));
      var rows = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(cursor + 5, 2));
      var packedSize = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(cursor + 7, 2));
      var dataOff = cursor + patHdrLen;
      if (rows <= 0) rows = 64;
      var data = packedSize > 0 && dataOff + packedSize <= blob.Length
        ? blob.AsSpan(dataOff, packedSize).ToArray()
        : [];
      patterns[p] = XmPattern.Unpack(data, rows, mod.ChannelCount);
      cursor = dataOff + packedSize;
    }
    mod.Patterns = patterns;

    var instruments = new XmInstrument[numInstruments];
    for (var ins = 0; ins < numInstruments; ++ins) {
      if (cursor + 29 > blob.Length) { instruments[ins] = new XmInstrument(); continue; }
      var insHdrSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(cursor, 4));
      var numSamples = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(cursor + 27, 2));
      var instrument = new XmInstrument { Name = ReadAscii(blob, cursor + 4, 22) };

      if (numSamples == 0) {
        cursor += insHdrSize > 0 ? insHdrSize : 29;
        instruments[ins] = instrument;
        continue;
      }

      // Extended instrument header (XM.TXT): sample-header size at +29, note→sample map at +33
      // (96 bytes), volume envelope at +129, panning envelope at +169, point counts and
      // sustain/loop indices, then vibrato + fadeout.
      ParseInstrumentEnvelopes(blob, cursor, instrument);

      var sampleHeaderSize = 40;
      var shStart = cursor + (insHdrSize > 0 ? insHdrSize : 263);
      var samples = new XmSample[numSamples];
      var lengths = new int[numSamples];
      for (var si = 0; si < numSamples; ++si) {
        var shOff = shStart + si * sampleHeaderSize;
        var s = new XmSample();
        if (shOff + sampleHeaderSize <= blob.Length) {
          lengths[si] = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(shOff, 4));
          s.LoopStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(shOff + 4, 4));
          s.LoopLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(shOff + 8, 4));
          s.Volume = blob[shOff + 12];
          s.Finetune = unchecked((sbyte)blob[shOff + 13]);
          var sflags = blob[shOff + 14];
          s.Panning = blob[shOff + 15];
          s.RelativeNote = unchecked((sbyte)blob[shOff + 16]);
          s.Is16Bit = (sflags & 0x10) != 0;
          s.LoopType = sflags & 0x03; // 0 none, 1 forward, 2 ping-pong
          s.Name = ReadAscii(blob, shOff + 18, 22);
        }
        samples[si] = s;
      }

      var dataCursor = shStart + numSamples * sampleHeaderSize;
      for (var si = 0; si < numSamples; ++si) {
        var byteLen = lengths[si];
        if (byteLen > 0 && dataCursor + byteLen <= blob.Length) {
          samples[si].SetData(blob.AsSpan(dataCursor, byteLen).ToArray(), samples[si].Is16Bit);
        } else if (byteLen > 0 && dataCursor < blob.Length) {
          samples[si].SetData(blob.AsSpan(dataCursor, blob.Length - dataCursor).ToArray(), samples[si].Is16Bit);
        }
        dataCursor += Math.Max(0, byteLen);
      }
      instrument.Samples = samples;
      instruments[ins] = instrument;
      cursor = dataCursor;
    }
    mod.Instruments = instruments;

    return mod;
  }

  private static void ParseInstrumentEnvelopes(byte[] blob, int insOff, XmInstrument instrument) {
    // Note→sample map: 96 bytes at +33.
    if (insOff + 33 + 96 <= blob.Length)
      for (var n = 0; n < 96; ++n)
        instrument.SampleMap[n] = blob[insOff + 33 + n];

    static XmEnvelope ReadEnv(byte[] b, int ptsOff, int count, int sustain, int loopStart, int loopEnd, int type) {
      var env = new XmEnvelope {
        Enabled = (type & 0x01) != 0,
        Sustain = (type & 0x02) != 0,
        Loop = (type & 0x04) != 0,
        SustainPoint = sustain,
        LoopStart = loopStart,
        LoopEnd = loopEnd,
      };
      count = Math.Clamp(count, 0, 12);
      env.Points = new (int X, int Y)[count];
      for (var i = 0; i < count; ++i) {
        var o = ptsOff + i * 4;
        if (o + 4 > b.Length) break;
        env.Points[i] = (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)),
                         BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o + 2, 2)));
      }
      return env;
    }

    if (insOff + 241 <= blob.Length) {
      var volPoints = blob[insOff + 225];
      var panPoints = blob[insOff + 226];
      var volSustain = blob[insOff + 227];
      var volLoopStart = blob[insOff + 228];
      var volLoopEnd = blob[insOff + 229];
      var panSustain = blob[insOff + 230];
      var panLoopStart = blob[insOff + 231];
      var panLoopEnd = blob[insOff + 232];
      var volType = blob[insOff + 233];
      var panType = blob[insOff + 234];
      instrument.VibratoType = blob[insOff + 235];
      instrument.VibratoSweep = blob[insOff + 236];
      instrument.VibratoDepth = blob[insOff + 237];
      instrument.VibratoRate = blob[insOff + 238];
      instrument.Fadeout = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(insOff + 239, 2));

      instrument.VolumeEnvelope = ReadEnv(blob, insOff + 129, volPoints, volSustain, volLoopStart, volLoopEnd, volType);
      instrument.PanningEnvelope = ReadEnv(blob, insOff + 177, panPoints, panSustain, panLoopStart, panLoopEnd, panType);
    }
  }

  private static string ReadAscii(byte[] blob, int offset, int length) {
    var end = Math.Min(offset + length, blob.Length);
    var chars = new List<char>();
    for (var i = offset; i < end; ++i) {
      var b = blob[i];
      if (b == 0) break;
      if (b >= 0x20 && b < 0x7F) chars.Add((char)b);
    }
    return new string(chars.ToArray()).Trim();
  }
}

/// <summary>A decoded XM envelope (volume or panning).</summary>
public sealed class XmEnvelope {
  /// <summary>
  /// Provides the enabled value.
  /// </summary>
public bool Enabled;
  /// <summary>
  /// Provides the sustain value.
  /// </summary>
public bool Sustain;
  /// <summary>
  /// Provides the loop value.
  /// </summary>
public bool Loop;
  /// <summary>
  /// Provides the sustain point value.
  /// </summary>
public int SustainPoint;
  /// <summary>
  /// Provides the loop start value.
  /// </summary>
public int LoopStart;
  /// <summary>
  /// Provides the loop end value.
  /// </summary>
public int LoopEnd;
  /// <summary>
  /// Provides the points value.
  /// </summary>
public (int X, int Y)[] Points = [];
}

/// <summary>A decoded XM instrument.</summary>
public sealed class XmInstrument {
  /// <summary>
  /// Provides the name value.
  /// </summary>
public string Name = "";
  /// <summary>
  /// Provides the sample map value.
  /// </summary>
public byte[] SampleMap = new byte[96];
  /// <summary>
  /// Provides the samples value.
  /// </summary>
public XmSample[] Samples = [];
  /// <summary>
  /// Provides the volume envelope value.
  /// </summary>
public XmEnvelope VolumeEnvelope = new();
  /// <summary>
  /// Provides the panning envelope value.
  /// </summary>
public XmEnvelope PanningEnvelope = new();
  /// <summary>
  /// Provides the vibrato type value.
  /// </summary>
public int VibratoType;
  /// <summary>
  /// Provides the vibrato sweep value.
  /// </summary>
public int VibratoSweep;
  /// <summary>
  /// Provides the vibrato depth value.
  /// </summary>
public int VibratoDepth;
  /// <summary>
  /// Provides the vibrato rate value.
  /// </summary>
public int VibratoRate;
  /// <summary>
  /// Provides the fadeout value.
  /// </summary>
public int Fadeout;
}

/// <summary>A decoded XM sample with its PCM expanded to signed 16-bit (delta-decoded).</summary>
public sealed class XmSample {
  /// <summary>
  /// Provides the name value.
  /// </summary>
public string Name = "";
  /// <summary>
  /// Provides the volume value.
  /// </summary>
public int Volume = 64;
  /// <summary>
  /// Provides the finetune value.
  /// </summary>
public sbyte Finetune;
  /// <summary>
  /// Provides the panning value.
  /// </summary>
public byte Panning = 128;
  /// <summary>
  /// Provides the relative note value.
  /// </summary>
public sbyte RelativeNote;
  /// <summary>
  /// Provides the is 16 bit value.
  /// </summary>
public bool Is16Bit;
  /// <summary>
  /// Provides the loop type value.
  /// </summary>
public int LoopType;
  /// <summary>
  /// Provides the loop start value.
  /// </summary>
public int LoopStart;   // in sample frames
  /// <summary>
  /// Provides the loop length value.
  /// </summary>
public int LoopLength;  // in sample frames
  /// <summary>
  /// Provides the pcm value.
  /// </summary>
public short[] Pcm = [];

  /// <summary>Stores raw delta-coded XM sample bytes, expanding to absolute signed 16-bit PCM.</summary>
  public void SetData(byte[] raw, bool is16) {
    if (is16) {
      var count = raw.Length / 2;
      this.Pcm = new short[count];
      short old = 0;
      for (var i = 0; i < count; ++i) {
        var delta = unchecked((short)(raw[i * 2] | (raw[i * 2 + 1] << 8)));
        old = unchecked((short)(old + delta));
        this.Pcm[i] = old;
      }
      // Loop fields in the header are byte counts for 16-bit samples → convert to frames.
      this.LoopStart /= 2;
      this.LoopLength /= 2;
    } else {
      this.Pcm = new short[raw.Length];
      sbyte old = 0;
      for (var i = 0; i < raw.Length; ++i) {
        old = unchecked((sbyte)(old + unchecked((sbyte)raw[i])));
        this.Pcm[i] = (short)(old << 8);
      }
    }
  }
}
