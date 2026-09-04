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

    var outputCodecExplicitlyRequested =
      !string.IsNullOrWhiteSpace(options.MethodName) || options.HasOption("codec");
    if (source.Id.Equals(target.Id, StringComparison.OrdinalIgnoreCase) && !outputCodecExplicitlyRequested) {
      Rewind(input);
      input.CopyTo(output);
      return;
    }

    var demux = AudioAdapterResolver.ResolveDemuxSource(source);
    var mux = AudioAdapterResolver.ResolveMuxTarget(target);
    if (demux is not null && mux is not null) {
      Rewind(input);
      if (demux.TryDemux(input, out var encoded) && encoded is not null &&
          mux.SupportedMuxCodecs.Contains(encoded.Format.CodecId, StringComparer.OrdinalIgnoreCase) &&
          mux.CanMux(encoded.Format, options, out _)) {
        mux.Mux(output, encoded, options);
        return;
      }
    }

    var pcmTarget = AudioAdapterResolver.ResolvePcmTarget(target);
    if (pcmTarget is not null) {
      AudioPcmBuffer? pcm = null;
      if (AudioAdapterResolver.ResolvePcmSource(source) is { } pcmSource) {
        Rewind(input);
        pcm = pcmSource.DecodePcm(input);
      } else if (TryDecodePseudoArchivePcm(input, source, out var bridgedPcm)) {
        pcm = bridgedPcm;
      }

      if (pcm is not null) {
        var codec = ResolveCodec(pcmTarget, options);
        if (!pcmTarget.CanEncode(pcm.Format, codec, options, out var reason)) {
          // Only now, having been refused, try the widths the target might take:
          // an 8-bit source into a 16-bit-only encoder is the common case.
          if (!TryRequantizeFor(pcmTarget, pcm, codec, options, out var adapted))
            throw new NotSupportedException(
              $"{target.Id} cannot encode {pcm.Format.Channels}ch/{pcm.Format.SampleRate}Hz/" +
              $"{pcm.Format.BitsPerSample}-bit PCM as '{codec}': {reason ?? "unsupported combination"}.");
          pcm = adapted;
        }

        pcmTarget.EncodePcm(output, pcm, codec, options);
        return;
      }
    }

    string? refusal = null;
    if (TryPseudoArchiveBridge(input, source, output, target, options, ref refusal))
      return;

    // A source that exposes no Channel entries — a mono file has nothing to split,
    // and plenty of formats surface only their container — still has audio in it.
    // Decode it and make the channels ourselves rather than refuse the conversion.
    if (TryDecodedPcmToPseudoArchive(input, source, output, target, options, ref refusal))
      return;

    throw new NotSupportedException(
      $"No audio conversion route exists from '{source.Id}' to '{target.Id}'. " +
      (refusal ?? "The source must expose encoded packets or PCM/channels and the target must expose a compatible mux/encode/create capability."));
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
      .Where(static entry => string.Equals(entry.Kind, "Channel", StringComparison.OrdinalIgnoreCase) &&
                             entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
      .OrderBy(static entry => entry.Index)
      .ToArray();

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
      decoded.Add(new WavReader().ReadCanonicalPcm(bytes));
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

  /// <summary>
  /// Builds the target from PCM we decoded ourselves, by splitting it into the same
  /// per-channel mono WAVs a container would have surfaced.
  /// </summary>
  /// <remarks>
  /// <see cref="TryPseudoArchiveBridge" /> only fires when the source already lists
  /// Channel entries. A mono file never does — there is nothing to split — so every
  /// create-only target was unreachable from mono input while the same conversion
  /// worked from stereo.
  /// </remarks>
  /// <summary>
  /// Offers the encoder the same audio at a width it accepts, when it has refused
  /// the one it was given.
  /// </summary>
  /// <remarks>
  /// Widening is exact; narrowing loses the low bits, and is only reached because
  /// the target accepts nothing wider. Sample rate and channel count are left
  /// alone — changing those is resampling and downmixing, not re-quantisation.
  /// </remarks>
  private static bool TryRequantizeFor(
    IAudioPcmTarget target,
    AudioPcmBuffer pcm,
    string codec,
    FormatCreateOptions options,
    out AudioPcmBuffer adapted
  ) {
    adapted = pcm;
    if (pcm.Format.Encoding == AudioPcmEncoding.IeeeFloat) return false;

    foreach (var bits in (int[])[16, 24, 32, 8]) {
      if (bits == pcm.Format.BitsPerSample) continue;

      var candidateFormat = new AudioPcmFormat(
        pcm.Format.SampleRate,
        pcm.Format.Channels,
        bits,
        bits == 8 ? AudioPcmEncoding.UnsignedInteger : AudioPcmEncoding.SignedInteger);
      if (!target.CanEncode(candidateFormat, codec, options, out _)) continue;

      adapted = new AudioPcmBuffer(
        candidateFormat,
        PcmCodec.Requantize(pcm.InterleavedData, pcm.Format.BitsPerSample, bits));
      return true;
    }

    return false;
  }

  private static bool TryDecodedPcmToPseudoArchive(
    Stream input,
    IFormatDescriptor source,
    Stream output,
    IFormatDescriptor target,
    FormatCreateOptions options,
    ref string? refusal
  ) {
    if (AudioAdapterResolver.ResolvePseudoArchiveTarget(target) is not { } targetCreate)
      return false;

    AudioPcmBuffer? pcm = null;
    if (AudioAdapterResolver.ResolvePcmSource(source) is { } pcmSource) {
      Rewind(input);
      pcm = pcmSource.DecodePcm(input);
    } else if (TryDecodePseudoArchivePcm(input, source, out var bridged)) {
      pcm = bridged;
    }

    if (pcm is null) return false;

    // The split emits integer PCM WAVs; float PCM has its own splitter.
    var channels = pcm.Format.Encoding == AudioPcmEncoding.IeeeFloat
      ? PcmCodec.SplitInterleavedFloat(
        pcm.InterleavedData, pcm.Format.Channels, pcm.Format.SampleRate, pcm.Format.BitsPerSample)
      : PcmCodec.SplitInterleavedPcm(
        pcm.InterleavedData, pcm.Format.Channels, pcm.Format.SampleRate, pcm.Format.BitsPerSample);

    static List<ArchiveInputInfo> AsInputs(IReadOnlyList<(string Name, byte[] WavBlob)> channels)
      => channels.Select(channel => ArchiveInputInfo.InMemory($"{channel.Name}.wav", channel.WavBlob)).ToList();

    if (TryCreateFromChannels(targetCreate, target, AsInputs(channels), output, options, ref refusal))
      return true;

    // Refused as it stands. An 8-bit source into a container that wants at least
    // 16 is the usual reason, and widening is exact, so offer it once that way.
    if (pcm.Format.Encoding == AudioPcmEncoding.IeeeFloat || pcm.Format.BitsPerSample != 8)
      return false;

    var widened = PcmCodec.SplitInterleavedPcm(
      PcmCodec.Requantize(pcm.InterleavedData, 8, 16),
      pcm.Format.Channels, pcm.Format.SampleRate, 16);
    return TryCreateFromChannels(targetCreate, target, AsInputs(widened), output, options, ref refusal);
  }

  /// <summary>
  /// Writes the channels through the target's own create path, reporting refusal
  /// rather than throwing, and leaving <paramref name="output" /> untouched unless
  /// the whole file was produced.
  /// </summary>
  private static bool TryCreateFromChannels(
    IArchiveCreatable targetCreate,
    IFormatDescriptor target,
    IReadOnlyList<ArchiveInputInfo> inputs,
    Stream output,
    FormatCreateOptions options,
    ref string? refusal
  ) {
    if (target is IArchiveWriteConstraints constraints)
      foreach (var archiveInput in inputs)
        if (!constraints.CanAccept(archiveInput, out var reason)) {
          refusal = $"{target.Id} rejected channel '{archiveInput.ArchiveName}': {reason ?? "unsupported input"}.";
          return false;
        }

    // into a scratch buffer, so a refusal half way through leaves nothing behind
    using var scratch = new MemoryStream();
    try {
      targetCreate.Create(scratch, inputs, options);
    } catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException
                                          or ArgumentException or InvalidDataException) {
      refusal = $"{target.Id}: {exception.Message}";
      return false;
    }

    if (scratch.Length == 0) {
      refusal = $"{target.Id} produced no output.";
      return false;
    }

    scratch.Position = 0;
    scratch.CopyTo(output);
    return true;
  }

  private static bool TryPseudoArchiveBridge(
    Stream input,
    IFormatDescriptor source,
    Stream output,
    IFormatDescriptor target,
    FormatCreateOptions options,
    ref string? refusal
  ) {
    if (source is not IArchiveFormatOperations sourceArchive ||
        AudioAdapterResolver.ResolvePseudoArchiveTarget(target) is not { } targetCreate)
      return false;

    Rewind(input);
    var entries = sourceArchive.List(input, password: null)
      .Where(static entry => string.Equals(entry.Kind, "Channel", StringComparison.OrdinalIgnoreCase) &&
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
        if (!constraints.CanAccept(archiveInput, out var reason)) {
          refusal = $"{target.Id} rejected converted channel '{archiveInput.ArchiveName}': {reason ?? "unsupported input"}.";
          return false;
        }

    // A refusal here is not the end: the decoded-PCM route may still be able to
    // offer the same audio at a width this target accepts.
    return TryCreateFromChannels(targetCreate, target, inputs, output, options, ref refusal);
  }

  private static void Rewind(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
  }
}
