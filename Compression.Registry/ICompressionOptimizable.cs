namespace Compression.Registry;

/// <summary>
/// Opt-in maintenance capability: re-encode the live payload with the format's best
/// compression while keeping the container format and everything else it carries.
/// </summary>
/// <remarks>
/// <para>This is the <c>compress</c> verb of the maintenance split (see
/// <see cref="MaintenanceCapability.Compress"/>). It changes how bytes are encoded, never
/// what they decode to: names, contents, timestamps, attributes and every header field
/// that is not a compression parameter come out as they went in. A rewrite that cannot
/// keep those refuses with <see cref="NotSupportedException"/> instead.</para>
///
/// <para>It is deliberately separate from canonicalization (<see cref="IArchiveCanonicalizable"/>),
/// repacking (<see cref="IArchiveRepackable"/>) and geometry changes
/// (<see cref="ILayoutOptimizable"/>): a format must not implement it merely because it can
/// be re-created with other parameters, and <see cref="FormatCapabilities.SupportsOptimize"/>
/// (the <c>method+</c> creation hint) is not a claim to this capability.</para>
/// </remarks>
public interface ICompressionOptimizable {
  /// <summary>
  /// False when this instance cannot re-encode after all — a composite whose inner layer
  /// has no lossless re-encode. Defaults to true; <see cref="MaintenanceCapabilities"/>
  /// does not offer the operation where it is false.
  /// </summary>
  bool CanOptimizeCompression => true;

  /// <summary>
  /// Writes the payload of <paramref name="input"/> to <paramref name="output"/> re-encoded
  /// with the best compression the format offers.
  /// </summary>
  /// <param name="input">The source, readable and seekable.</param>
  /// <param name="output">The target, writable and seekable; it receives a complete file.</param>
  /// <param name="password">Password for an encrypted source, when the format has one.</param>
  /// <remarks>
  /// <para><b>Default implementation</b>, for single-stream formats
  /// (<see cref="IStreamFormatOperations"/>): decode to a scratch file, encode with
  /// <see cref="IStreamFormatOperations.CompressOptimal"/>, decode the result again and
  /// require the very same bytes. Formats with header metadata of their own (gzip's name
  /// and time) override it to carry that metadata across.</para>
  /// <para>Callers should still keep the original when the result is not smaller;
  /// <see cref="MaintenanceVerbs.Compress"/> does.</para>
  /// </remarks>
  void OptimizeCompression(Stream input, Stream output, string? password = null) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (this is not IStreamFormatOperations streamOperations)
      throw new NotSupportedException(
        $"The default {nameof(ICompressionOptimizable)} implementation needs {nameof(IStreamFormatOperations)}; "
        + "a container format must implement its own lossless re-encode.");

    // Decoding twice (source, then candidate) must not mean holding the payload in RAM:
    // a stream format can expand far beyond any array limit.
    using var raw = RebuildVerb.CreateScratchStream();
    input.Position = 0;
    streamOperations.Decompress(input, raw);
    raw.Flush();
    raw.Position = 0;
    streamOperations.CompressOptimal(raw, output);
    output.Flush();

    output.Position = 0;
    raw.Position = 0;
    ContentPreservation.RequireSameDecodedPayload(streamOperations, output, raw);
    output.Position = 0;
  }
}
