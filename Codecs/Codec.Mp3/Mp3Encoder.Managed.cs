#pragma warning disable CS1591

using System.Buffers.Binary;
using GroovyCodecs.Types;
using ManagedLameEncoder = GroovyCodecs.Mp3.Mp3Encoder;

namespace Codec.Mp3;

/// <summary>Layer III channel mode.</summary>
public enum Mp3EncoderChannelMode {
  /// <summary>
  /// Selects the value automatically.
  /// </summary>
Auto = -1,
  /// <summary>
  /// Specifies the stereo option.
  /// </summary>
Stereo = 0,
  /// <summary>
  /// Specifies the joint stereo option.
  /// </summary>
JointStereo = 1,
  /// <summary>
  /// Specifies the dual channel option.
  /// </summary>
DualChannel = 2,
  /// <summary>
  /// Specifies the mono option.
  /// </summary>
Mono = 3,
}

/// <summary>
/// Managed MP3 encoder controls. The backend is the LGPL-3.0 GroovyMp3 C# port of LAME/Jump3r;
/// it is fully managed and performs no P/Invoke/native codec calls.
/// </summary>
/// <param name="SampleRate">PCM input sample rate.</param>
/// <param name="Channels">PCM input channel count (1 or 2).</param>
/// <param name="BitrateKbps">CBR bitrate in kbit/s; -1 lets LAME select a default. Ignored by VBR.</param>
/// <param name="ChannelMode">Stereo coding mode. Mono input is always encoded mono.</param>
/// <param name="Quality">LAME algorithm/VBR quality, 1 (highest) through 9 (lowest).</param>
/// <param name="VariableBitrate">Use LAME VBR instead of constant bitrate.</param>
/// <param name="OutputSampleRate">Optional resampled output rate; null preserves the input rate.</param>
public sealed record Mp3EncoderOptions(
  int SampleRate,
  int Channels,
  int BitrateKbps = 128,
  Mp3EncoderChannelMode ChannelMode = Mp3EncoderChannelMode.Auto,
  int Quality = 5,
  bool VariableBitrate = false,
  int? OutputSampleRate = null
);

/// <summary>Pure-managed MPEG Layer III encoder facade.</summary>
public static class Mp3Encoder {

  /// <summary>Encodes interleaved little-endian PCM16 samples to MP3.</summary>
  public static byte[] Encode(ReadOnlySpan<short> interleaved, Mp3EncoderOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    Validate(interleaved.Length, options);

    var source = new AudioFormat {
      SampleRate = options.SampleRate,
      BitsPerSample = 16,
      Channels = checked((short)options.Channels),
      BlockAlign = checked((short)(options.Channels * 2)),
      AverageBytesPerSecond = checked(options.SampleRate * options.Channels * 2),
      BigEndian = false,
      IsFloatingPoint = false,
      Properties = new Dictionary<string, object>(),
    };
    var target = new AudioFormat {
      SampleRate = options.OutputSampleRate ?? options.SampleRate,
      BitsPerSample = -1,
      Channels = checked((short)options.Channels),
      BigEndian = false,
      IsFloatingPoint = false,
      Properties = new Dictionary<string, object> {
        [ManagedLameEncoder.P_QUALITY] = options.Quality,
        [ManagedLameEncoder.P_BITRATE] = options.BitrateKbps,
        [ManagedLameEncoder.P_CHMODE] = ChannelModeName(options.Channels == 1 ? Mp3EncoderChannelMode.Mono : options.ChannelMode),
        [ManagedLameEncoder.P_VBR] = options.VariableBitrate,
      },
    };

    var encoder = new ManagedLameEncoder(source, target);
    try {
      using var output = new MemoryStream();
      var pcmBuffer = new byte[Math.Max(encoder.InputBufferSize, 4096)];
      var encodedBuffer = new byte[Math.Max(encoder.OutputBufferSize, 8192)];
      var sampleOffset = 0;

      while (sampleOffset < interleaved.Length) {
        var samplesToCopy = Math.Min(pcmBuffer.Length / 2, interleaved.Length - sampleOffset);
        var bytesToCopy = samplesToCopy * 2;
        for (var i = 0; i < samplesToCopy; ++i)
          BinaryPrimitives.WriteInt16LittleEndian(pcmBuffer.AsSpan(i * 2, 2), interleaved[sampleOffset + i]);

        var produced = encoder.EncodeBuffer(pcmBuffer, 0, bytesToCopy, encodedBuffer);
        if (produced < 0 || produced > encodedBuffer.Length)
          throw new InvalidDataException($"Managed MP3 encoder returned invalid byte count {produced}.");
        if (produced > 0)
          output.Write(encodedBuffer, 0, produced);
        sampleOffset += samplesToCopy;
      }

      var finalBytes = encoder.EncodeFinish(encodedBuffer);
      if (finalBytes < 0 || finalBytes > encodedBuffer.Length)
        throw new InvalidDataException($"Managed MP3 encoder flush returned invalid byte count {finalBytes}.");
      if (finalBytes > 0)
        output.Write(encodedBuffer, 0, finalBytes);
      return output.ToArray();
    } finally {
      encoder.Close();
    }
  }

  private static void Validate(int sampleCount, Mp3EncoderOptions options) {
    if (options.Channels is < 1 or > 2)
      throw new ArgumentOutOfRangeException(nameof(options), "MP3 supports mono or stereo PCM input.");
    if (sampleCount % options.Channels != 0)
      throw new ArgumentException("Interleaved PCM sample count must be a multiple of the channel count.");
    if (options.SampleRate is < 8000 or > 48000)
      throw new ArgumentOutOfRangeException(nameof(options), "LAME MP3 input rate must be in the supported 8-48 kHz range.");
    if (options.OutputSampleRate is < 8000 or > 48000)
      throw new ArgumentOutOfRangeException(nameof(options), "MP3 output rate must be in the supported 8-48 kHz range.");
    if (options.Quality is < 1 or > 9)
      throw new ArgumentOutOfRangeException(nameof(options), "MP3 quality must be 1-9.");
    if (options.BitrateKbps != ManagedLameEncoder.BITRATE_AUTO && options.BitrateKbps is < 8 or > 320)
      throw new ArgumentOutOfRangeException(nameof(options), "MP3 bitrate must be -1 (auto) or 8-320 kbit/s.");
    if (options.Channels == 1 && options.ChannelMode is not (Mp3EncoderChannelMode.Auto or Mp3EncoderChannelMode.Mono))
      throw new ArgumentException("A mono PCM source cannot use a stereo MP3 channel mode.", nameof(options));
  }

  private static string ChannelModeName(Mp3EncoderChannelMode mode) => mode switch {
    Mp3EncoderChannelMode.Stereo => "stereo",
    Mp3EncoderChannelMode.JointStereo => "jointstereo",
    Mp3EncoderChannelMode.DualChannel => "dual",
    Mp3EncoderChannelMode.Mono => "mono",
    _ => "auto",
  };
}
