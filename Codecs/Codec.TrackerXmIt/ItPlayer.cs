#pragma warning disable CS1591
namespace Codec.TrackerXmIt;

/// <summary>
/// A software player for Impulse Tracker (<c>.it</c>) modules that renders the song to interleaved
/// stereo 16-bit PCM.
/// </summary>
/// <remarks>
/// <para>
/// Implements the IT playback model from ITTECH.TXT (Jeffrey Lim): instrument vs. sample mode
/// (header flag 0x04), New Note Actions with a virtual-channel pool (capped at 64 background
/// voices), duplicate-check type/action, volume/panning/pitch envelopes with node ticks, the
/// resonant low-pass filter (<c>Zxx</c> and filter envelopes), and effects A..Z including the
/// S-command family. Old/new effect compatibility (flags 0x10 / 0x20) is honoured where it changes
/// semantics: with "old effects" set, <c>Gxx</c> (tone porta) and <c>Exx/Fxx</c> share memory
/// differently and vibrato runs at half depth — documented at the use sites.
/// </para>
/// <para>
/// Pragmatic scope: nearest-neighbour sample interpolation, additive mixing; the
/// <c>S9x</c> sound-control subset (surround/reverb/etc.) is parsed but ignored aside from S91
/// (surround) which maps to centre panning; MIDI macros are not implemented.
/// </para>
/// </remarks>
public sealed class ItPlayer {

  private readonly ItModule _mod;
  private readonly int _sampleRate;

  private ItPlayer(ItModule mod, int sampleRate) {
    this._mod = mod;
    this._sampleRate = sampleRate;
  }

  /// <summary>
  /// Performs the load operation.
  /// </summary>
public static ItPlayer Load(byte[] blob, int sampleRate = TrackerRender.OutputSampleRate)
    => new(ItModule.Parse(blob), sampleRate);

  /// <summary>
  /// Gets the module.
  /// </summary>
public ItModule Module => this._mod;

  /// <summary>
  /// Performs the render operation.
  /// </summary>
public byte[] Render(double maxSeconds = TrackerRender.MaxSeconds)
    => new ItEngine(this._mod, this._sampleRate).Render(maxSeconds);

  /// <summary>
  /// Performs the estimate seconds operation.
  /// </summary>
public double EstimateSeconds()
    => new ItEngine(this._mod, this._sampleRate).EstimateSeconds(TrackerRender.MaxSeconds);

  /// <summary>
  /// Test hook: builds the engine and steps it for <paramref name="ticks"/> ticks, returning the
  /// number of voices still actively sounding. Used to verify NNA virtual-channel behaviour.
  /// </summary>
  internal int ActiveVoicesAfterTicks(int ticks) {
    var engine = new ItEngine(this._mod, this._sampleRate);
    return engine.StepAndCountActiveVoices(ticks);
  }
}
