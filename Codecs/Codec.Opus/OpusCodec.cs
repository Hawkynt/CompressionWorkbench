#pragma warning disable CS1591
#pragma warning disable CS0618

using System.Buffers.Binary;
using Concentus;
using Concentus.Structs;

namespace Codec.Opus;

/// <summary>
/// RFC 6716 / RFC 7845 Opus codec. Ogg framing and metadata are handled locally; SILK,
/// hybrid, CELT and multistream signal coding are handled by pure-managed Concentus.
/// </summary>
public static partial class OpusCodec {

  /// <summary>
  /// Decodes Ogg Opus mapping family 0 (mono/stereo) and family 1 (Vorbis-order surround)
  /// to interleaved little-endian PCM16 at 48 kHz. Pre-skip and output gain are applied.
  /// </summary>
  public static void Decompress(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    _ = reader.TryReadTags();

    if (head.ChannelMappingFamily == 0) {
      using IOpusDecoder decoder = new OpusDecoder(48000, head.ChannelCount);
      decoder.Gain = head.OutputGainQ8;
      DecodeFamily0(reader, output, decoder, head.ChannelCount, head.PreSkip);
      return;
    }

    if (head.ChannelMappingFamily == 1) {
      using IOpusMultiStreamDecoder decoder = new OpusMSDecoder(
        48000,
        head.ChannelCount,
        head.StreamCount,
        head.CoupledStreamCount,
        head.ChannelMapping);
      decoder.Gain = head.OutputGainQ8;
      DecodeFamily1(reader, output, decoder, head.ChannelCount, head.PreSkip);
      return;
    }

    throw new NotSupportedException($"Opus channel mapping family {head.ChannelMappingFamily} is not supported by this stream surface.");
  }

  private static void DecodeFamily0(OggOpusReader reader, Stream output, IOpusDecoder decoder,
    int channels, int preSkip) {
    var pcm = new short[5760 * channels];
    var writer = new TrimmingPcmWriter(output, channels, preSkip);
    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0) continue;
      var decodedFrames = decoder.Decode(packet, pcm, 5760, decode_fec: false);
      if (decodedFrames < 0)
        throw new InvalidDataException($"Opus decoder returned invalid frame count {decodedFrames}.");
      writer.Add(pcm, decodedFrames);
    }

    writer.Finish(reader.LastGranulePosition);
  }

  private static void DecodeFamily1(OggOpusReader reader, Stream output, IOpusMultiStreamDecoder decoder,
    int channels, int preSkip) {
    var pcm = new short[5760 * channels];
    var writer = new TrimmingPcmWriter(output, channels, preSkip);
    while (reader.TryReadPacket(out var packet)) {
      if (packet.Length == 0) continue;
      var decodedFrames = decoder.DecodeMultistream(packet, pcm, 5760, decode_fec: false);
      if (decodedFrames < 0)
        throw new InvalidDataException($"Opus multistream decoder returned invalid frame count {decodedFrames}.");
      writer.Add(pcm, decodedFrames);
    }

    writer.Finish(reader.LastGranulePosition);
  }

  /// <summary>
  /// Writes decoded frames out, dropping the encoder's pre-skip at the front and
  /// its padding at the back.
  /// </summary>
  /// <remarks>
  /// The stream's true length is only known once the last page has been read, so
  /// the most recent chunk is held back rather than written: the trailing padding
  /// always falls inside it, and holding one chunk costs a frame of memory where
  /// buffering the whole decode would cost the file.
  /// </remarks>
  private sealed class TrimmingPcmWriter(Stream output, int channels, int preSkip) {
    private readonly byte[] _bytes = new byte[5760 * channels * 2];
    private readonly int _preSkipTotal = preSkip;
    private short[] _held = [];
    private int _heldFrames;
    private long _writtenFrames;
    private int _preSkipRemaining = preSkip;

    public void Add(short[] pcm, int decodedFrames) {
      this.Flush(this._heldFrames);

      var skip = Math.Min(this._preSkipRemaining, decodedFrames);
      this._preSkipRemaining -= skip;
      var keep = decodedFrames - skip;
      if (keep <= 0) {
        this._heldFrames = 0;
        return;
      }

      if (this._held.Length < keep * channels)
        this._held = new short[keep * channels];
      Array.Copy(pcm, skip * channels, this._held, 0, keep * channels);
      this._heldFrames = keep;
    }

    /// <summary>
    /// Flushes what is held, clipped to the length the final granule declares.
    /// </summary>
    public void Finish(long lastGranulePosition) {
      var frames = this._heldFrames;
      // Only a positive granule is a statement about length. Zero is what a page
      // carries when it has nothing to say, so trusting it would throw away the
      // last packet of any stream that never fills the field in.
      if (lastGranulePosition > 0) {
        // The granule counts pre-skip too, so the audible length is what is left
        // after it. A stream that declares less than we decoded is padded.
        var total = Math.Max(0, lastGranulePosition - this._preSkipTotal);
        frames = (int)Math.Clamp(total - this._writtenFrames, 0, frames);
      }

      this.Flush(frames);
      this._heldFrames = 0;
    }

    private void Flush(int frames) {
      if (frames <= 0) return;
      var sampleCount = frames * channels;
      for (var i = 0; i < sampleCount; ++i)
        BinaryPrimitives.WriteInt16LittleEndian(this._bytes.AsSpan(i * 2, 2), this._held[i]);
      output.Write(this._bytes, 0, sampleCount * 2);
      this._writtenFrames += frames;
    }
  }

  /// <summary>Reads OpusHead / OpusTags metadata without decoding audio.</summary>
  public static OpusStreamInfo ReadStreamInfo(Stream input) {
    ArgumentNullException.ThrowIfNull(input);
    var reader = new OggOpusReader(input);
    var head = reader.ReadHead();
    var tags = reader.TryReadTags();
    return new OpusStreamInfo(
      SampleRate: 48000,
      Channels: head.ChannelCount,
      PreSkip: head.PreSkip,
      InputSampleRate: (int)head.InputSampleRate,
      Vendor: tags?.Vendor,
      ChannelMappingFamily: head.ChannelMappingFamily,
      StreamCount: head.StreamCount,
      CoupledStreamCount: head.CoupledStreamCount,
      ChannelMapping: head.ChannelMapping);
  }
}

/// <summary>
/// Represents an opus stream info.
/// </summary>
public sealed record OpusStreamInfo(
  int SampleRate,
  int Channels,
  int PreSkip,
  int InputSampleRate,
  string? Vendor,
  int ChannelMappingFamily,
  int StreamCount,
  int CoupledStreamCount,
  IReadOnlyList<byte> ChannelMapping);
