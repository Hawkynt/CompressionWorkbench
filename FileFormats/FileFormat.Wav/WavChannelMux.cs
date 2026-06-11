#pragma warning disable CS1591

namespace FileFormat.Wav;

/// <summary>
/// Shared helper for PCM-container writers (WAV / CAF / W64 / RF64): collects
/// per-channel mono WAV inputs (LEFT/RIGHT/CENTER/… or CH_N), validates they
/// agree on sample rate + bit depth, and interleaves them into a single
/// little-endian integer PCM buffer.
/// </summary>
public static class WavChannelMux {

  /// <summary>Picks the per-channel mono WAV inputs from a flat list of
  /// (name, data) pairs, ordered by the conventional channel layout. Inputs whose
  /// names are not <c>FULL.*</c> and end in <c>.wav</c> are treated as channels.</summary>
  public static List<(string Name, byte[] Data)> GatherChannels(
      IReadOnlyList<(string Name, byte[] Data)> fileList) =>
    fileList
      .Where(f => {
        var name = Path.GetFileName(f.Name);
        return name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("FULL.", StringComparison.OrdinalIgnoreCase);
      })
      .OrderBy(f => ChannelOrder(Path.GetFileNameWithoutExtension(f.Name)))
      .ToList();

  /// <summary>Reads each mono channel WAV, verifies they share sample rate and
  /// bit depth and have equal frame counts, then interleaves into one PCM buffer.</summary>
  public static (byte[] Interleaved, int Channels, int SampleRate, int BitsPerSample) Interleave(
      IReadOnlyList<(string Name, byte[] Data)> channelBlobs) {
    if (channelBlobs.Count == 0)
      throw new InvalidOperationException("No channel WAVs supplied.");

    var channels = channelBlobs.Select(c => new WavReader().Read(c.Data)).ToList();
    var first = channels[0];
    if (channels.Any(c => c.SampleRate != first.SampleRate ||
                          c.BitsPerSample != first.BitsPerSample ||
                          c.NumChannels != 1))
      throw new InvalidOperationException(
        "All channel WAVs must be mono and share sample rate + bit depth.");

    var bytesPerSample = first.BitsPerSample / 8;
    var frameCount = first.InterleavedPcm.Length / bytesPerSample;
    if (channels.Any(c => c.InterleavedPcm.Length / bytesPerSample != frameCount))
      throw new InvalidOperationException("All channel WAVs must have the same frame count.");

    var ch = channels.Count;
    var interleaved = new byte[frameCount * ch * bytesPerSample];
    for (var f = 0; f < frameCount; ++f)
      for (var c = 0; c < ch; ++c)
        Buffer.BlockCopy(channels[c].InterleavedPcm, f * bytesPerSample,
          interleaved, (f * ch + c) * bytesPerSample, bytesPerSample);

    return (interleaved, ch, first.SampleRate, first.BitsPerSample);
  }

  private static int ChannelOrder(string name) => name.ToUpperInvariant() switch {
    "LEFT" or "FRONT_LEFT" or "MONO" => 0,
    "RIGHT" or "FRONT_RIGHT" => 1,
    "CENTER" => 2,
    "LFE" => 3,
    "BACK_LEFT" => 4,
    "BACK_RIGHT" => 5,
    "SIDE_LEFT" => 6,
    "SIDE_RIGHT" => 7,
    _ => int.TryParse(
           name.StartsWith("CH_", StringComparison.Ordinal) ? name[3..] : name,
           System.Globalization.NumberStyles.Integer,
           System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0,
  };
}
