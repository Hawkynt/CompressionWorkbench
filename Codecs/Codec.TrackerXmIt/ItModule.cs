#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Codec.TrackerXmIt;

/// <summary>
/// Parsed Impulse Tracker module structure (header, orders, instruments, samples, patterns),
/// per ITTECH.TXT (Jeffrey Lim). Compressed samples are decompressed via
/// <see cref="ItSampleDecompressor"/> at parse time so the engine sees absolute PCM.
/// </summary>
public sealed class ItModule {

    /// <summary>
  /// Provides the song name value.
  /// </summary>
public string SongName = "";
    /// <summary>
  /// Provides the order count value.
  /// </summary>
public int OrderCount;
    /// <summary>
  /// Provides the instrument count value.
  /// </summary>
public int InstrumentCount;
    /// <summary>
  /// Provides the sample count value.
  /// </summary>
public int SampleCount;
    /// <summary>
  /// Provides the pattern count value.
  /// </summary>
public int PatternCount;
    /// <summary>
  /// Provides the flags value.
  /// </summary>
public int Flags;
    /// <summary>
  /// Provides the special value.
  /// </summary>
public int Special;
    /// <summary>
  /// Provides the global volume value.
  /// </summary>
public int GlobalVolume = 128;
    /// <summary>
  /// Provides the mix volume value.
  /// </summary>
public int MixVolume = 48;
    /// <summary>
  /// Provides the initial speed value.
  /// </summary>
public int InitialSpeed = 6;
    /// <summary>
  /// Provides the initial tempo value.
  /// </summary>
public int InitialTempo = 125;
    /// <summary>
  /// Provides the separation value.
  /// </summary>
public int Separation = 128;
    /// <summary>
  /// Provides the instrument mode value.
  /// </summary>
public bool InstrumentMode;     // flags bit 2 (0x04)
    /// <summary>
  /// Provides the linear slides value.
  /// </summary>
public bool LinearSlides;       // flags bit 3 (0x08)
    /// <summary>
  /// Provides the old effects value.
  /// </summary>
public bool OldEffects;         // flags bit 4 (0x10)
    /// <summary>
  /// Provides the link g effect value.
  /// </summary>
public bool LinkGEffect;        // flags bit 5 (0x20)
    /// <summary>
  /// Provides the compatible version value.
  /// </summary>
public int CompatibleVersion;   // cmwt
    /// <summary>
  /// Provides the order value.
  /// </summary>
public byte[] Order = [];
    /// <summary>
  /// Provides the channel pan value.
  /// </summary>
public byte[] ChannelPan = new byte[64];
    /// <summary>
  /// Provides the channel volume value.
  /// </summary>
public byte[] ChannelVolume = new byte[64];
    /// <summary>
  /// Provides the instruments value.
  /// </summary>
public ItInstrument[] Instruments = [];
    /// <summary>
  /// Provides the samples value.
  /// </summary>
public ItSample[] Samples = [];
    /// <summary>
  /// Provides the patterns value.
  /// </summary>
public ItPattern[] Patterns = [];

    /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public static ItModule Parse(byte[] blob) {
    if (blob.Length < 192 || !(blob[0] == 'I' && blob[1] == 'M' && blob[2] == 'P' && blob[3] == 'M'))
      throw new InvalidDataException("Not an IT file.");

    var mod = new ItModule {
      SongName = ReadAscii(blob, 4, 26),
      OrderCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(32, 2)),
      InstrumentCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(34, 2)),
      SampleCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(36, 2)),
      PatternCount = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(38, 2)),
      CompatibleVersion = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(42, 2)),
      Flags = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(44, 2)),
      Special = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(46, 2)),
    };
    mod.GlobalVolume = blob[48];
    mod.MixVolume = blob[49];
    mod.InitialSpeed = blob[50] > 0 ? blob[50] : 6;
    mod.InitialTempo = blob[51] > 0 ? blob[51] : 125;
    mod.Separation = blob[52];
    mod.InstrumentMode = (mod.Flags & 0x04) != 0;
    mod.LinearSlides = (mod.Flags & 0x08) != 0;
    mod.OldEffects = (mod.Flags & 0x10) != 0;
    mod.LinkGEffect = (mod.Flags & 0x20) != 0;

    for (var i = 0; i < 64; ++i) {
      var pan = blob[64 + i];
      mod.ChannelPan[i] = pan;
      mod.ChannelVolume[i] = blob[128 + i];
    }

    var order = new byte[mod.OrderCount];
    Array.Copy(blob, 192, order, 0, Math.Min(mod.OrderCount, Math.Max(0, blob.Length - 192)));
    mod.Order = order;

    var insOffsets = 192 + mod.OrderCount;
    var smpOffsets = insOffsets + mod.InstrumentCount * 4;
    var patOffsets = smpOffsets + mod.SampleCount * 4;

    mod.Instruments = new ItInstrument[mod.InstrumentCount];
    for (var i = 0; i < mod.InstrumentCount; ++i) {
      var off = (int)ReadU32(blob, insOffsets + i * 4);
      mod.Instruments[i] = ItInstrument.Parse(blob, off, mod.CompatibleVersion);
    }

    mod.Samples = new ItSample[mod.SampleCount];
    for (var i = 0; i < mod.SampleCount; ++i) {
      var off = (int)ReadU32(blob, smpOffsets + i * 4);
      mod.Samples[i] = ItSample.Parse(blob, off);
    }

    mod.Patterns = new ItPattern[mod.PatternCount];
    for (var p = 0; p < mod.PatternCount; ++p) {
      var off = (int)ReadU32(blob, patOffsets + p * 4);
      mod.Patterns[p] = ItPattern.Parse(blob, off);
    }

    return mod;
  }

  private static uint ReadU32(byte[] b, int off)
    => off + 4 <= b.Length ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off, 4)) : 0;

  internal static string ReadAscii(byte[] blob, int offset, int length) {
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

/// <summary>An IT envelope (volume, panning, or pitch/filter) with node ticks and y-values.</summary>
public sealed class ItEnvelope {
    /// <summary>
  /// Provides the enabled value.
  /// </summary>
public bool Enabled;
    /// <summary>
  /// Provides the loop value.
  /// </summary>
public bool Loop;
    /// <summary>
  /// Provides the sustain value.
  /// </summary>
public bool Sustain;
    /// <summary>
  /// Provides the is filter value.
  /// </summary>
public bool IsFilter;       // pitch envelope flag 0x80 → acts as filter cutoff envelope
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
  /// Provides the nodes value.
  /// </summary>
public (int Tick, int Y)[] Nodes = [];
}

/// <summary>A parsed IT instrument (new IMPI format; NNA, DCT/DCA, envelopes, sample map).</summary>
public sealed class ItInstrument {
    /// <summary>
  /// Provides the name value.
  /// </summary>
public string Name = "";
    /// <summary>
  /// Provides the new note action value.
  /// </summary>
public int NewNoteAction;   // 0 cut, 1 continue, 2 off, 3 fade
    /// <summary>
  /// Provides the duplicate check type value.
  /// </summary>
public int DuplicateCheckType;   // 0 off, 1 note, 2 sample, 3 instrument
    /// <summary>
  /// Provides the duplicate check action value.
  /// </summary>
public int DuplicateCheckAction; // 0 cut, 1 off, 2 fade
    /// <summary>
  /// Provides the fadeout value.
  /// </summary>
public int Fadeout;         // 0..128, applied >>? (ITTECH: /512 per tick scaled)
    /// <summary>
  /// Provides the global volume value.
  /// </summary>
public int GlobalVolume = 128;
    /// <summary>
  /// Provides the default pan value.
  /// </summary>
public int DefaultPan = 32; // 0..64, 32 = centre; bit7 = "don't use"
    /// <summary>
  /// Provides the use pan value.
  /// </summary>
public bool UsePan;
    /// <summary>
  /// Provides the pitch pan separation value.
  /// </summary>
public int PitchPanSeparation;
    /// <summary>
  /// Provides the pitch pan center value.
  /// </summary>
public int PitchPanCenter = 60;
    /// <summary>
  /// Provides the note sample map value.
  /// </summary>
public byte[] NoteSampleMap = new byte[120]; // note→ (note, sample) pairs flattened: sample index
    /// <summary>
  /// Provides the note map value.
  /// </summary>
public byte[] NoteMap = new byte[120];       // note→ remapped note
    /// <summary>
  /// Provides the volume envelope value.
  /// </summary>
public ItEnvelope VolumeEnvelope = new();
    /// <summary>
  /// Provides the panning envelope value.
  /// </summary>
public ItEnvelope PanningEnvelope = new();
    /// <summary>
  /// Provides the pitch envelope value.
  /// </summary>
public ItEnvelope PitchEnvelope = new();
    /// <summary>
  /// Provides the initial filter cutoff value.
  /// </summary>
public int InitialFilterCutoff = -1;   // from IFC (bit7 set = enabled)
    /// <summary>
  /// Provides the initial filter resonance value.
  /// </summary>
public int InitialFilterResonance = -1;

    /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
public static ItInstrument Parse(byte[] blob, int off, int cmwt) {
    var ins = new ItInstrument();
    if (off <= 0 || off + 4 > blob.Length) return ins;
    var isNew = blob[off] == 'I' && blob[off + 1] == 'M' && blob[off + 2] == 'P' && blob[off + 3] == 'I';
    if (!isNew) {
      // Old (pre-2.0) instrument: 64-byte header, simpler. Map keymap then volume envelope.
      ins.Name = ItModule.ReadAscii(blob, off + 20, 26);
      if (off + 64 + 240 <= blob.Length)
        for (var n = 0; n < 120; ++n)
          ins.NoteSampleMap[n] = blob[off + 64 + n * 2 + 1];
      return ins;
    }

    ins.Name = ItModule.ReadAscii(blob, off + 32, 26);
    if (off + 0x230 > blob.Length) return ins;
    ins.NewNoteAction = blob[off + 0x11];
    ins.DuplicateCheckType = blob[off + 0x12];
    ins.DuplicateCheckAction = blob[off + 0x13];
    ins.Fadeout = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(off + 0x14, 2));
    ins.PitchPanSeparation = unchecked((sbyte)blob[off + 0x16]);
    ins.PitchPanCenter = blob[off + 0x17];
    ins.GlobalVolume = blob[off + 0x18];
    var dfp = blob[off + 0x19];
    ins.UsePan = (dfp & 0x80) == 0;
    ins.DefaultPan = dfp & 0x7F;
    var ifc = blob[off + 0x1F];
    var ifr = blob[off + 0x20];
    if ((ifc & 0x80) != 0) ins.InitialFilterCutoff = ifc & 0x7F;
    if ((ifr & 0x80) != 0) ins.InitialFilterResonance = ifr & 0x7F;

    // Note/sample keyboard table: 120 entries of (note, sample) starting at 0x40.
    var keyOff = off + 0x40;
    for (var n = 0; n < 120; ++n) {
      var o = keyOff + n * 2;
      if (o + 1 >= blob.Length) break;
      ins.NoteMap[n] = blob[o];
      ins.NoteSampleMap[n] = blob[o + 1];
    }

    // Envelopes: volume at 0x130, panning at 0x182, pitch at 0x1D4 (each 82 bytes).
    ins.VolumeEnvelope = ParseEnvelope(blob, off + 0x130, isFilterCapable: false);
    ins.PanningEnvelope = ParseEnvelope(blob, off + 0x182, isFilterCapable: false);
    ins.PitchEnvelope = ParseEnvelope(blob, off + 0x1D4, isFilterCapable: true);
    return ins;
  }

  private static ItEnvelope ParseEnvelope(byte[] blob, int off, bool isFilterCapable) {
    var env = new ItEnvelope();
    if (off + 82 > blob.Length) return env;
    var flags = blob[off];
    env.Enabled = (flags & 0x01) != 0;
    env.Loop = (flags & 0x02) != 0;
    env.Sustain = (flags & 0x04) != 0;
    if (isFilterCapable) env.IsFilter = (flags & 0x80) != 0;
    var num = blob[off + 1];
    env.LoopStart = blob[off + 2];
    env.LoopEnd = blob[off + 3];
    env.SustainStart = blob[off + 4];
    env.SustainEnd = blob[off + 5];
    num = (byte)Math.Clamp((int)num, 0, 25);
    env.Nodes = new (int, int)[num];
    for (var i = 0; i < num; ++i) {
      var o = off + 6 + i * 3;
      if (o + 2 >= blob.Length) break;
      var y = unchecked((sbyte)blob[o]);
      var tick = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(o + 1, 2));
      env.Nodes[i] = (tick, y);
    }
    return env;
  }
}
