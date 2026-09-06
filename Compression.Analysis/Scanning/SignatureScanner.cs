namespace Compression.Analysis.Scanning;

/// <summary>
/// Deep-scans binary data for known format signatures at every byte offset and supplements those
/// hits with package-native structural header probes at candidate and aligned starts.
/// </summary>
public static class SignatureScanner {

  /// <summary>
  /// Scans the entire data for known signatures.
  /// </summary>
  /// <param name="data">Binary data to scan.</param>
  /// <param name="maxResults">Maximum number of results to return.</param>
  /// <param name="headerProbeAlignment">
  /// Alignment used for structure-only package detectors. Offset zero and every fixed-signature
  /// candidate are always probed. Use 0 to disable additional aligned probes, 512 for normal disk
  /// forensics, or 1 for exhaustive probing. Fixed magic signatures are always scanned byte-by-byte.
  /// </param>
  public static List<ScanResult> Scan(ReadOnlySpan<byte> data, int maxResults = 100, int headerProbeAlignment = 512) {
    var results = new List<ScanResult>();
    if (data.IsEmpty || maxResults <= 0)
      return results;

    // Keep a bounded working set, but never stop scanning the input merely because weak early
    // signatures filled it. A one-byte format magic can legitimately occur thousands of times in
    // arbitrary data; later high-confidence evidence must still get a chance to displace it.
    var candidateLimit = maxResults > int.MaxValue / 10 ? int.MaxValue : maxResults * 10;
    var trimThreshold = candidateLimit > int.MaxValue / 2 ? int.MaxValue : candidateLimit * 2;

    for (var magicOffset = 0; magicOffset < data.Length; ++magicOffset) {
      var b0 = data[magicOffset];

      foreach (var entry in SignatureDatabase.GetByFirstByte(b0))
        TryAddSignatureResult(data, magicOffset, entry, results);

      if (magicOffset + 1 < data.Length)
        foreach (var entry in SignatureDatabase.GetByPrefix(b0, data[magicOffset + 1]))
          TryAddSignatureResult(data, magicOffset, entry, results);

      // Masked leading bytes cannot be addressed by the exact prefix index. This list is normally
      // tiny; the full signature comparison below still applies every mask byte precisely.
      foreach (var entry in SignatureDatabase.MaskedPrefixEntries) {
        if (!LeadingByteMatches(b0, entry)) continue;
        TryAddSignatureResult(data, magicOffset, entry, results);
      }

      if (results.Count >= trimThreshold)
        NormalizeCandidates(results, candidateLimit);
    }

    // Bound the fixed-signature candidate set before invoking structural package detectors. This
    // also ensures a flood of low-confidence one-byte matches does not translate into an equally
    // large number of expensive structural probes.
    if (results.Count > candidateLimit)
      NormalizeCandidates(results, candidateLimit);

    ProbePackageHeaders(data, headerProbeAlignment, candidateLimit, trimThreshold, results);

    NormalizeCandidates(results, maxResults);
    return results;
  }

  private static void TryAddSignatureResult(
    ReadOnlySpan<byte> data,
    int magicOffset,
    SignatureDatabase.SignatureEntry entry,
    List<ScanResult> results) {
    var headerStart = magicOffset - entry.Offset;
    if (headerStart < 0) return;
    if (!MatchesMagic(data, magicOffset, entry.Magic, entry.Mask)) return;
    results.Add(CreateResult(data, headerStart, entry));
  }

  private static bool LeadingByteMatches(byte value, SignatureDatabase.SignatureEntry entry) {
    if (entry.Magic.Length == 0) return false;
    var mask = entry.Mask is { Length: > 0 } ? entry.Mask[0] : (byte)0xFF;
    return (value & mask) == (entry.Magic[0] & mask);
  }

  private static bool MatchesMagic(ReadOnlySpan<byte> data, int offset, byte[] magic, byte[]? mask) {
    if (offset < 0 || magic.Length == 0 || offset > data.Length - magic.Length)
      return false;

    if (mask is null)
      return data.Slice(offset, magic.Length).SequenceEqual(magic);

    for (var i = 0; i < magic.Length; ++i)
      if ((data[offset + i] & mask[i]) != (magic[i] & mask[i]))
        return false;
    return true;
  }

  private static void ProbePackageHeaders(
    ReadOnlySpan<byte> data,
    int alignment,
    int candidateLimit,
    int trimThreshold,
    List<ScanResult> results) {
    // Fixed magics are cheap enough to scan byte-granularly. Their reconstructed file starts are
    // therefore excellent places to ask a richer package detector to resolve shared/container
    // signatures without doing an O(bytes × structural-detectors) exhaustive walk.
    var signatureCandidateOffsets = results
      .Where(static result => result.MagicLength > 0)
      .Select(static result => result.Offset)
      .Where(offset => offset >= 0 && offset <= int.MaxValue)
      .Select(static offset => (int)offset)
      .ToHashSet();

    foreach (var source in SignatureDatabase.HeaderDetectionSources) {
      if (source.HeaderProbeLength <= 0)
        continue;

      ProbePackageHeaderAt(data, source, 0, results);

      foreach (var offset in signatureCandidateOffsets) {
        if (offset == 0) continue;
        ProbePackageHeaderAt(data, source, offset, results);
        if (results.Count >= trimThreshold)
          NormalizeCandidates(results, candidateLimit);
      }

      if (alignment <= 0)
        continue;

      // Use a wider induction variable so a very large span/alignment cannot wrap the probe offset
      // back into negative int territory on the final increment.
      for (long alignedOffset = alignment; alignedOffset < data.Length; alignedOffset += alignment) {
        var offset = (int)alignedOffset;
        if (signatureCandidateOffsets.Contains(offset)) continue;
        ProbePackageHeaderAt(data, source, offset, results);
        if (results.Count >= trimThreshold)
          NormalizeCandidates(results, candidateLimit);
      }
    }
  }

  private static void ProbePackageHeaderAt(
    ReadOnlySpan<byte> data,
    Compression.Registry.IFormatDetectionSource source,
    int offset,
    List<ScanResult> results) {
    var available = data.Length - offset;
    if (available <= 0) return;
    var length = Math.Min(source.HeaderProbeLength, available);
    var match = source.DetectHeader(data.Slice(offset, length));
    if (match is null) return;

    var previewLength = Math.Min(16, available);
    var preview = Convert.ToHexString(data.Slice(offset, previewLength));
    results.Add(new ScanResult(offset, match.FormatId, match.Confidence, 0, preview));
  }

  private static void NormalizeCandidates(List<ScanResult> results, int limit) {
    results.Sort(static (a, b) => {
      var confidence = b.Confidence.CompareTo(a.Confidence);
      return confidence != 0 ? confidence : a.Offset.CompareTo(b.Offset);
    });

    // Sorting first intentionally means duplicate package/descriptor detections retain the strongest
    // evidence for one format at one candidate start. Compact in place to keep intermediate scans
    // bounded without allocating another result list.
    var seen = new HashSet<(long Offset, string Format)>();
    var writeIndex = 0;
    for (var readIndex = 0; readIndex < results.Count && writeIndex < limit; ++readIndex) {
      var result = results[readIndex];
      if (!seen.Add((result.Offset, result.FormatName)))
        continue;
      results[writeIndex++] = result;
    }

    if (writeIndex < results.Count)
      results.RemoveRange(writeIndex, results.Count - writeIndex);
  }

  private static ScanResult CreateResult(
    ReadOnlySpan<byte> data,
    long headerOffset,
    SignatureDatabase.SignatureEntry entry) {
    var start = checked((int)headerOffset);
    var previewLength = Math.Min(16, data.Length - start);
    var preview = Convert.ToHexString(data.Slice(start, previewLength));
    return new ScanResult(headerOffset, entry.FormatName, entry.Confidence, entry.Magic.Length, preview);
  }
}
