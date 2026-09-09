#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Bik;

/// <summary>
/// Packet-preserving Bink 1/2 muxer. The writer consumes the pseudo-archive representation
/// produced by <see cref="BikReader"/>: <c>metadata.ini</c>, the concatenated per-frame
/// video packets in <c>VIDEO.bin</c>, and one concatenated <c>TRACKn.bin</c> per audio track.
/// Per-frame packet lengths in the metadata restore the interleaving without decoding or
/// re-encoding either proprietary codec.
/// </summary>
internal static class BikWriter {

  private const int MaxAudioTracks = 256;
  private const int MaxFrames = 1_000_000;

  private sealed record AudioTrack(uint MaxDecodedSize, ushort SampleRate, ushort Flags, uint Id);
  private sealed record Frame(bool KeyFrame, int VideoSize, int[] AudioSizes);
  private sealed record Header(
    string Signature,
    uint Reserved,
    uint Width,
    uint Height,
    uint FpsNumerator,
    uint FpsDenominator,
    uint VideoFlags,
    uint? ExtraHeader,
    AudioTrack[] AudioTracks,
    Frame[] Frames
  );

  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var files = inputs
      .Where(static input => !input.IsDirectory)
      .GroupBy(static input => LeafName(input.ArchiveName), StringComparer.OrdinalIgnoreCase)
      .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

    if (!files.TryGetValue("metadata.ini", out var metadataInput) || !files.TryGetValue("VIDEO.bin", out var videoInput)) {
      if (files.TryGetValue("FULL.bik", out var fullInput)) {
        WritePassthrough(output, fullInput.ReadContent());
        return;
      }

      throw new InvalidDataException(
        "Bink muxing requires metadata.ini + VIDEO.bin (+ TRACKn.bin for audio), or a byte-exact FULL.bik passthrough.");
    }

    var header = ParseMetadata(Encoding.UTF8.GetString(metadataInput.ReadContent()));
    var video = videoInput.ReadContent();
    var expectedVideoBytes = header.Frames.Sum(static frame => (long)frame.VideoSize);
    if (expectedVideoBytes != video.LongLength)
      throw new InvalidDataException($"VIDEO.bin has {video.LongLength} bytes, metadata describes {expectedVideoBytes}.");

    var audio = new byte[header.AudioTracks.Length][];
    for (var trackIndex = 0; trackIndex < audio.Length; ++trackIndex) {
      var expected = header.Frames.Sum(frame => (long)frame.AudioSizes[trackIndex]);
      if (!files.TryGetValue($"TRACK{trackIndex}.bin", out var trackInput)) {
        if (expected != 0)
          throw new InvalidDataException($"TRACK{trackIndex}.bin is required by metadata ({expected} bytes).");
        audio[trackIndex] = [];
        continue;
      }

      audio[trackIndex] = trackInput.ReadContent();
      if (audio[trackIndex].LongLength != expected)
        throw new InvalidDataException(
          $"TRACK{trackIndex}.bin has {audio[trackIndex].LongLength} bytes, metadata describes {expected}.");
    }

    var result = Mux(header, video, audio);
    WritePassthrough(output, result);
  }

  private static byte[] Mux(Header header, byte[] video, byte[][] audio) {
    using var stream = new MemoryStream();

    stream.Write(Encoding.ASCII.GetBytes(header.Signature));
    WriteUInt32(stream, 0); // file size - 8, patched below
    WriteUInt32(stream, checked((uint)header.Frames.Length));
    WriteUInt32(stream, 0); // largest frame, patched below
    WriteUInt32(stream, header.Reserved);
    WriteUInt32(stream, header.Width);
    WriteUInt32(stream, header.Height);
    WriteUInt32(stream, header.FpsNumerator);
    WriteUInt32(stream, header.FpsDenominator);
    WriteUInt32(stream, header.VideoFlags);
    WriteUInt32(stream, checked((uint)header.AudioTracks.Length));

    if (HasExtraHeader(header.Signature))
      WriteUInt32(stream, header.ExtraHeader ?? 0);

    foreach (var track in header.AudioTracks)
      WriteUInt32(stream, track.MaxDecodedSize);
    foreach (var track in header.AudioTracks) {
      WriteUInt16(stream, track.SampleRate);
      WriteUInt16(stream, track.Flags);
    }
    foreach (var track in header.AudioTracks)
      WriteUInt32(stream, track.Id);

    var indexOffset = stream.Position;
    foreach (var _ in header.Frames)
      WriteUInt32(stream, 0);

    var videoOffset = 0;
    var audioOffsets = new int[audio.Length];
    var frameOffsets = new uint[header.Frames.Length];
    uint largestFrame = 0;

    for (var frameIndex = 0; frameIndex < header.Frames.Length; ++frameIndex) {
      if (stream.Position > uint.MaxValue)
        throw new NotSupportedException("Bink muxing is limited by its 32-bit absolute frame offsets.");

      frameOffsets[frameIndex] = checked((uint)stream.Position);
      var start = stream.Position;
      var frame = header.Frames[frameIndex];

      for (var trackIndex = 0; trackIndex < audio.Length; ++trackIndex) {
        var packetSize = frame.AudioSizes[trackIndex];
        WriteUInt32(stream, checked((uint)packetSize));
        stream.Write(audio[trackIndex], audioOffsets[trackIndex], packetSize);
        audioOffsets[trackIndex] += packetSize;
      }

      stream.Write(video, videoOffset, frame.VideoSize);
      videoOffset += frame.VideoSize;

      // Frame offsets use bit 0 as the keyframe marker, therefore physical frame starts
      // must be even. Native Bink files are aligned accordingly; normalize synthetic mux
      // inputs with one zero byte when necessary.
      if ((stream.Position & 1) != 0)
        stream.WriteByte(0);

      var frameSize = checked((uint)(stream.Position - start));
      largestFrame = Math.Max(largestFrame, frameSize);
    }

    if (stream.Position > uint.MaxValue)
      throw new NotSupportedException("Bink muxing is limited to files addressable by 32-bit frame offsets.");
    if (stream.Position < 8)
      throw new InvalidDataException("Invalid Bink output size.");

    var result = stream.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)result.Length - 8));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), largestFrame);

    for (var frameIndex = 0; frameIndex < header.Frames.Length; ++frameIndex) {
      var offset = frameOffsets[frameIndex];
      if (header.Frames[frameIndex].KeyFrame || frameIndex == 0)
        offset |= 1;
      BinaryPrimitives.WriteUInt32LittleEndian(
        result.AsSpan(checked((int)indexOffset + frameIndex * 4), 4), offset);
    }

    return result;
  }

  private static Header ParseMetadata(string text) {
    var ini = ParseIni(text);
    var bink = Section(ini, "Bink");
    var signature = Required(bink, "signature");
    if (signature.Length != 4 || !IsSupportedSignature(signature))
      throw new InvalidDataException($"Unsupported Bink signature '{signature}'.");

    var frameCount = Int32(bink, "frames", 1, MaxFrames);
    var trackCount = Int32(bink, "audio_tracks", 0, MaxAudioTracks);
    var width = UInt32(bink, "width");
    var height = UInt32(bink, "height");
    var fpsNumerator = TryUInt32(bink, "fps_num", out var fpsNum) ? fpsNum : ParseFps(bink).Numerator;
    var fpsDenominator = TryUInt32(bink, "fps_den", out var fpsDen) ? fpsDen : ParseFps(bink).Denominator;
    if (width == 0 || height == 0 || fpsNumerator == 0 || fpsDenominator == 0)
      throw new InvalidDataException("Bink dimensions and frame rate must be non-zero.");

    var tracks = new AudioTrack[trackCount];
    for (var index = 0; index < trackCount; ++index) {
      var section = Section(ini, $"Track{index}");
      tracks[index] = new AudioTrack(
        UInt32(section, "max_decoded_size", defaultValue: 0),
        checked((ushort)UInt32(section, "sample_rate", maxValue: ushort.MaxValue)),
        checked((ushort)UInt32(section, "flags", maxValue: ushort.MaxValue)),
        UInt32(section, "id", defaultValue: 0)
      );
    }

    var frames = new Frame[frameCount];
    for (var frameIndex = 0; frameIndex < frameCount; ++frameIndex) {
      var section = Section(ini, $"Frame{frameIndex}");
      var audioSizes = new int[trackCount];
      for (var trackIndex = 0; trackIndex < trackCount; ++trackIndex)
        audioSizes[trackIndex] = Int32(section, $"audio{trackIndex}_size", 0, int.MaxValue);
      frames[frameIndex] = new Frame(
        Boolean(section, "keyframe", defaultValue: frameIndex == 0),
        Int32(section, "video_size", 0, int.MaxValue),
        audioSizes
      );
    }

    return new Header(
      signature,
      UInt32(bink, "reserved", defaultValue: 0),
      width,
      height,
      fpsNumerator,
      fpsDenominator,
      UInt32(bink, "video_flags", defaultValue: 0),
      TryUInt32(bink, "extra_header", out var extraHeader) ? extraHeader : null,
      tracks,
      frames
    );
  }

  private static Dictionary<string, Dictionary<string, string>> ParseIni(string text) {
    var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string>? current = null;

    foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line[0] is ';' or '#')
        continue;
      if (line.StartsWith('[') && line.EndsWith(']')) {
        var name = line[1..^1].Trim();
        current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        result[name] = current;
        continue;
      }

      var equals = line.IndexOf('=');
      if (current is null || equals <= 0)
        continue;
      current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
    }

    return result;
  }

  private static Dictionary<string, string> Section(
      Dictionary<string, Dictionary<string, string>> ini, string name)
    => ini.TryGetValue(name, out var section)
      ? section
      : throw new InvalidDataException($"metadata.ini is missing [{name}].");

  private static string Required(Dictionary<string, string> section, string key)
    => section.TryGetValue(key, out var value) && value.Length != 0
      ? value
      : throw new InvalidDataException($"metadata.ini is missing '{key}'.");

  private static int Int32(Dictionary<string, string> section, string key, int minValue, int maxValue) {
    var value = ParseUnsigned(Required(section, key));
    if (value > (ulong)maxValue || value < (ulong)minValue)
      throw new InvalidDataException($"metadata.ini value '{key}' is out of range.");
    return checked((int)value);
  }

  private static uint UInt32(Dictionary<string, string> section, string key, uint defaultValue = 0, uint maxValue = uint.MaxValue) {
    if (!section.TryGetValue(key, out var text) || text.Length == 0)
      return defaultValue;
    var value = ParseUnsigned(text);
    if (value > maxValue)
      throw new InvalidDataException($"metadata.ini value '{key}' is out of range.");
    return checked((uint)value);
  }

  private static bool TryUInt32(Dictionary<string, string> section, string key, out uint value) {
    value = 0;
    if (!section.TryGetValue(key, out var text) || text.Length == 0)
      return false;
    var parsed = ParseUnsigned(text);
    if (parsed > uint.MaxValue)
      throw new InvalidDataException($"metadata.ini value '{key}' is out of range.");
    value = (uint)parsed;
    return true;
  }

  private static bool Boolean(Dictionary<string, string> section, string key, bool defaultValue) {
    if (!section.TryGetValue(key, out var text) || text.Length == 0)
      return defaultValue;
    return text.ToLowerInvariant() switch {
      "true" or "yes" or "1" => true,
      "false" or "no" or "0" => false,
      _ => throw new InvalidDataException($"metadata.ini value '{key}' is not a boolean."),
    };
  }

  private static (uint Numerator, uint Denominator) ParseFps(Dictionary<string, string> section) {
    var parts = Required(section, "fps").Split('/', 2, StringSplitOptions.TrimEntries);
    if (parts.Length != 2)
      throw new InvalidDataException("metadata.ini 'fps' must be numerator/denominator.");
    var numerator = ParseUnsigned(parts[0]);
    var denominator = ParseUnsigned(parts[1]);
    if (numerator is 0 or > uint.MaxValue || denominator is 0 or > uint.MaxValue)
      throw new InvalidDataException("metadata.ini 'fps' is out of range.");
    return ((uint)numerator, (uint)denominator);
  }

  private static ulong ParseUnsigned(string text) {
    var value = text.Trim();
    try {
      if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return ulong.Parse(value.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
      return ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    } catch (Exception exception) when (exception is FormatException or OverflowException) {
      throw new InvalidDataException($"metadata.ini value '{text}' is not an unsigned integer.", exception);
    }
  }

  private static void WritePassthrough(Stream output, byte[] data) {
    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    output.Write(data);
  }

  private static void WriteUInt16(Stream stream, ushort value) {
    stream.WriteByte((byte)value);
    stream.WriteByte((byte)(value >> 8));
  }

  private static void WriteUInt32(Stream stream, uint value) {
    stream.WriteByte((byte)value);
    stream.WriteByte((byte)(value >> 8));
    stream.WriteByte((byte)(value >> 16));
    stream.WriteByte((byte)(value >> 24));
  }

  private static string LeafName(string name) {
    var normalized = (name ?? string.Empty).Replace('\\', '/');
    var slash = normalized.LastIndexOf('/');
    return slash < 0 ? normalized : normalized[(slash + 1)..];
  }

  private static bool IsSupportedSignature(string signature)
    => signature is "BIKb" or "BIKf" or "BIKg" or "BIKh" or "BIKi" or "BIKk"
      or "KB2a" or "KB2d" or "KB2f" or "KB2g" or "KB2h" or "KB2i" or "KB2j" or "KB2k";

  private static bool HasExtraHeader(string signature)
    => signature is "BIKk" or "KB2i" or "KB2j" or "KB2k";
}
