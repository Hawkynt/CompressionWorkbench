using Codec.Pcm;
using Compression.Registry;
using FileFormat.Wav;

namespace Compression.Lib;

/// <summary>
/// Capability-driven audio conversion. Routes the least destructive path first:
/// byte-exact passthrough, encoded packet remux, canonical PCM transcode, then the
/// legacy per-channel WAV pseudo-archive bridge.
/// </summary>
public static class AudioConversionOperation {

  public static void Convert(
    Stream input,
    string sourceFormatId,
    Stream output,
    string targetFormatId,
    FormatCreateOptions? options = null
  ) {
    ArgumentNullException.ThrowIfNull(sourceFormatId);
    ArgumentNullException.ThrowIfNull(targetFormatId);

    FormatRegistry.Initialize();
    var source = FormatRegistry.GetById(sourceFormatId)
      ?? throw new ArgumentException($"Unknown source format '{sourceFormatId}'.", nameof(sourceFormatId));
    var target = FormatRegistry.GetById(targetFormatId)
      ?? throw new ArgumentException($"Unknown target format '{targetFormatId}'.", nameof(targetFormatId));
    Convert(input, source, output, target, options);
  }

  public static void Convert(
    Stream input,
    IFormatDescriptor source,
    Stream output,
    IFormatDescriptor target,
    FormatCreateOptions? options = null
  ) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(target);
    options ??= new FormatCreateOptions();

    if (source.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase)) {
      Rewind(input);
      input.CopyTo(output);
      return;
    }

    if (source is IAudioDemuxSource demux && target is IAudioMuxTarget mux) {
      Rewind(input);
      if (demux.TryDemux(input, out var encoded) && encoded is not null &&
          mux.SupportedMuxCodecs.Contains(encoded.Format.CodecId, StringComparer.OrdinalIgnoreCase) &&
          mux.CanMux(encoded.Format, options, out _)) {
        mux.Mux(output, encoded, options);
        return;
      }
    }

    if (AudioFormatAdapters.ResolvePcmTarget(target) is { } pcmTarget) {
      AudioPcmBuffer? pcm = null;
      if (AudioFormatAdapters.ResolvePcmSource(source) is { } pcmSource) {
        Rewind(input);
        pcm = pcmSource.DecodePcm(input);
      } else if (TryDecodePseudoArchivePcm(input, source, out var bridgedPcm)) {
        pcm = bridgedPcm;
      }

      if (pcm is not null) {
        var codec = ResolveCodec(pcmTarget, options);
        if (!pcmTarget.CanEncode(pcm.Format, codec, options, out var reason))
          throw new NotSupportedException(
            $"{target.Id} cannot encode {pcm.Format.Channels}ch/{pcm.Format.SampleRate}Hz/" +
            $"{pcm.Format.BitsPerSample}-bit PCM as '{codec}': {reason ?? "unsupported combination"}.");
        pcmTarget.EncodePcm(output, pcm, codec, options);
        return;
      }
    }

    if (TryPseudoArchiveBridge(input, source, output, target, options))
      return;

    throw new NotSupportedException(
      $"No audio conversion route exists from '{source.Id}' to '{target.Id}'. " +
      "The source must expose encoded packets or PCM/channels and the target must expose a compatible mux/encode/create capability.");
  }

  private static string ResolveCodec(IAudioPcmTarget target, FormatCreateOptions options) {
    if (!string.IsNullOrWhiteSpace(options.MethodName)) return options.MethodName;
    var explicitCodec = options.GetOption("codec", string.Empty);
    if (!string.IsNullOrWhiteSpace(explicitCodec)) return explicitCodec;
    if (target.SupportedEncodeCodecs.Count == 0)
      throw new NotSupportedException("The target advertises no audio encoder codecs.");
    return target.SupportedEncodeCodecs[0];
  }

  private static bool TryDecodePseudoArchivePcm(
    Stream input,
    IFormatDescriptor source,
    out AudioPcmBuffer? pcm
  ) {
    pcm = null;
    if (source is not IArchiveFormatOperations sourceArchive) return false;

    Rewind(input);
    var listed = sourceArchive.List(input, password: null);
    var entries = listed
      .Where(static entry => entry.Kind.Equals("Channel", StringComparison.OrdinalIgnoreCase) &&
                             entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static entry => entry.Index)
      .ToArray();

    // A mono WAV is already the canonical channel file, so its archive view does not
    // need to manufacture MONO.wav. Treat FULL.wav as the one channel in that case.
    if (entries.Length == 0 && source.Id.Equals("Wav", StringComparison.OrdinalIgnoreCase)) {
      var full = listed.FirstOrDefault(static entry =>
        entry.Name.Equals("FULL.wav", StringComparison.OrdinalIgnoreCase));
      if (full is not null) entries = [full];
    }
    if (entries.Length == 0) return false;

    var decoded = new List<WavReader.ParsedWav>(entries.Length);
    foreach (var entry in entries) {
      Rewind(input);
      var bytes = sourceArchive.ExtractEntryToMemory(input, entry.Name, password: null);
      decoded.Add(new WavReader().Read(bytes));
    }

    if (decoded.Count == 1 && decoded[0].NumChannels > 1) {
      var only = decoded[0];
      if (only.FormatCode is not (1 or 3)) return false;
      pcm = new AudioPcmBuffer(
        new AudioPcmFormat(
          only.SampleRate,
          only.NumChannels,
          only.BitsPerSample,
          only.FormatCode == 3 ? AudioPcmEncoding.IeeeFloat
            : only.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger
            : AudioPcmEncoding.SignedInteger),
        only.InterleavedPcm);
      return true;
    }

    var first = decoded[0];
    if (first.NumChannels != 1 || first.FormatCode is not (1 or 3)) return false;
    if (decoded.Any(channel => channel.NumChannels != 1 || channel.FormatCode != first.FormatCode ||
                               channel.BitsPerSample != first.BitsPerSample || channel.SampleRate != first.SampleRate ||
                               channel.InterleavedPcm.Length != first.InterleavedPcm.Length))
      return false;

    var interleaved = PcmCodec.Interleave(decoded.Select(static channel => channel.InterleavedPcm).ToList(), first.BitsPerSample);
    pcm = new AudioPcmBuffer(
      new AudioPcmFormat(
        first.SampleRate,
        decoded.Count,
        first.BitsPerSample,
        first.FormatCode == 3 ? AudioPcmEncoding.IeeeFloat
          : first.BitsPerSample == 8 ? AudioPcmEncoding.UnsignedInteger
          : AudioPcmEncoding.SignedInteger),
      interleaved);
    return true;
  }

  private static bool TryPseudoArchiveBridge(
    Stream input,
    IFormatDescriptor source,
    Stream output,
    IFormatDescriptor target,
    FormatCreateOptions options
  ) {
    if (source is not IArchiveFormatOperations sourceArchive || target is not IArchiveCreatable targetCreate)
      return false;

    Rewind(input);
    var entries = sourceArchive.List(input, password: null)
      .Where(static entry => entry.Kind.Equals("Channel", StringComparison.OrdinalIgnoreCase) &&
                             entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static entry => entry.Index)
      .ToArray();
    if (entries.Length == 0) return false;

    var inputs = new List<ArchiveInputInfo>(entries.Length);
    foreach (var entry in entries) {
      Rewind(input);
      var bytes = sourceArchive.ExtractEntryToMemory(input, entry.Name, password: null);
      inputs.Add(ArchiveInputInfo.InMemory(entry.Name, bytes));
    }

    if (target is IArchiveWriteConstraints constraints)
      foreach (var archiveInput in inputs)
        if (!constraints.CanAccept(archiveInput, out var reason))
          throw new NotSupportedException(
            $"{target.Id} rejected converted channel '{archiveInput.ArchiveName}': {reason ?? "unsupported input"}.");

    targetCreate.Create(output, inputs, options);
    return true;
  }

  private static void Rewind(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
  }
}
