#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.Dictionary.Lzma;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// eXpressor (CGSoftLabs) — a Win32 PE packer that stores the victim's sections as a
/// chain of raw LZMA1 streams in a single payload section.
/// </summary>
/// <remarks>
/// <para>
/// The container has no published specification; the layout below was read off the packed
/// samples. It is self-describing, which is what makes it recoverable without interpreting
/// the loader stub:
/// </para>
/// <list type="number">
///   <item>One section holds the whole payload. It is a back-to-back chain of raw LZMA1
///     streams, one per compressed section of the original image, laid out in the original's
///     section order.</item>
///   <item>Every stream starts with the LZMA SDK's five-byte properties header — the packed
///     <c>(pb·5 + lp)·9 + lc</c> byte followed by a little-endian dictionary size. The samples
///     surveyed all carry <c>0x5E</c> (lc = 4, lp = 0, pb = 2) and an 8 MiB dictionary, but the
///     header is read rather than assumed.</item>
///   <item>Each stream is terminated by an LZMA end-of-stream marker instead of a stored
///     length, so decoding one stream also finds where the next one begins.</item>
/// </list>
/// <para>
/// Before compressing, eXpressor also runs the LZMA SDK's x86 branch filter over every section,
/// which rewrites <c>E8</c>/<c>E9</c> call and jump displacements from relative to absolute.
/// Decompression therefore yields each original section with its branch targets still in absolute
/// form; only a stream that contained no convertible site at all comes back byte-identical, which
/// over the reference corpus is 73 of 359 streams. Inverting that filter is not implemented here — see the
/// <see cref="ExecutableDiagnosticCode.TransformNotReversible"/> diagnostic every result
/// carries — so the artifacts are published as what they demonstrably are, decompressed but
/// still branch-filtered, rather than being passed off as the original bytes.
/// </para>
/// </remarks>
public sealed class ExpressorExecutablePackerHandler : MinorExecutablePackerHandlerBase {
    /// <summary>
  /// Gets the id.
  /// </summary>
public override string Id => "expressor";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public override string DisplayName => "EXpressor";
    /// <summary>
  /// Performs the is packer section operation.
  /// </summary>
protected override bool IsPackerSection(string name) =>
    name.Contains("exp", StringComparison.OrdinalIgnoreCase) ||
    name.Contains("ex_", StringComparison.OrdinalIgnoreCase);
    /// <summary>
  /// Gets the literal signature.
  /// </summary>
protected override ReadOnlySpan<byte> LiteralSignature => "EXpressor"u8;

    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public override ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.SupportsPe |
    ExecutableUnpackCapabilities.SupportsX86;

  /// <summary>Smallest stream chain worth believing: a single stream this size or larger.</summary>
  private const int MinimumStreamOutput = 64;

    /// <summary>
  /// Performs the unpack operation.
  /// </summary>
public override UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    var artifacts = new List<UnpackArtifact> {
      new("metadata.json", this.BuildMetadataJson(packed), "stored"),
      new("original_packed.bin", image, "stored"),
    };
    var diagnostics = new List<ExecutableDiagnostic>();
    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.SupportsPe;
    if (packed.ImageInfo?.Architecture == CpuArchitecture.X64) caps |= ExecutableUnpackCapabilities.SupportsX64;
    else caps |= ExecutableUnpackCapabilities.SupportsX86;

    var level = ExecutableUnpackLevel.DetectionOnly;
    var budget = (int)Math.Min(options.MaximumDecompressedSize, int.MaxValue);

    // The payload section is not reliably named, so pick the section whose bytes actually
    // read as the longest LZMA chain rather than trusting a name.
    List<byte[]>? best = null;
    var bestSection = "";
    var bestConsumed = 0;
    byte[]? bestPayload = null;
    foreach (var section in PackerScanner.GetPeSectionRanges(image)) {
      if (section.RawSize < 16 || section.RawOffset >= (uint)image.Length) continue;
      var length = (int)Math.Min(section.RawSize, (uint)image.Length - section.RawOffset);
      var streams = ReadLzmaChain(image.AsSpan((int)section.RawOffset, length), budget, out var consumed);
      if (streams.Count == 0 || (best is not null && streams.Count <= best.Count)) continue;
      best = streams;
      bestSection = section.Name;
      bestConsumed = consumed;
      // Only a chain that runs to the end of its section is the payload section rather
      // than a stream that happens to start inside some other section's data.
      bestPayload = consumed >= length - 16 ? image.AsSpan((int)section.RawOffset, length).ToArray() : null;
    }

    if (bestPayload is not null) {
      artifacts.Add(new("compressed_payload.bin", bestPayload, "stored"));
      level = ExecutableUnpackLevel.PayloadLocated;
      caps |= ExecutableUnpackCapabilities.CanLocatePayload;
    }

    if (best is null) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "EXpressor detected, but no section read as a chain of raw LZMA streams.", true));
      return Finish(level, caps, artifacts, diagnostics, packed, this.Id);
    }

    var total = 0L;
    for (var i = 0; i < best.Count; ++i) {
      artifacts.Add(new($"decompressed/stream_{i:000}.bin", best[i], "lzma"));
      total += best[i].Length;
    }
    level = ExecutableUnpackLevel.PayloadDecompressed;
    caps |= ExecutableUnpackCapabilities.CanDecompressPayload;

    diagnostics.Add(new(ExecutableDiagnosticCode.UnsupportedCompressionMethod,
      $"EXpressor payload in section '{bestSection}': {best.Count} raw LZMA stream(s), " +
      $"{bestConsumed} compressed bytes expanding to {total}.", false));
    diagnostics.Add(new(ExecutableDiagnosticCode.TransformNotReversible,
      "eXpressor applies the LZMA SDK's x86 branch filter to every section before compressing, so the " +
      "decompressed streams still carry absolute E8/E9 call and jump targets wherever the filter found " +
      "any to convert. A stream is byte-identical to its original section only when it contained no " +
      "convertible site at all, which over the reference corpus is the minority of them. No inverse " +
      "branch filter is applied here, in preference to emitting bytes that are neither the packed nor " +
      "the original form.", false));

    return Finish(level, caps, artifacts, diagnostics, packed, this.Id);
  }

  private static UnpackResult Finish(
    ExecutableUnpackLevel level,
    ExecutableUnpackCapabilities caps,
    List<UnpackArtifact> artifacts,
    List<ExecutableDiagnostic> diagnostics,
    PackedExecutable packed,
    string id) {
    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Walks the chain of raw LZMA streams at the start of <paramref name="payload"/>, stopping
  /// at the first position that does not open a stream that decodes to its end marker.
  /// </summary>
  private static List<byte[]> ReadLzmaChain(ReadOnlySpan<byte> payload, int budget, out int consumed) {
    var streams = new List<byte[]>();
    var position = 0;
    consumed = 0;

    while (position + 5 < payload.Length && budget > 0) {
      var properties = payload[position];
      var dictionarySize = BinaryPrimitives.ReadUInt32LittleEndian(payload[(position + 1)..]);
      // A properties byte is only valid below 9·5·5, and the packer never asks for a
      // dictionary smaller than a page or larger than a gigabyte. Both cheaply reject noise.
      if (properties >= 9 * 5 * 5 || dictionarySize is < 0x1000 or > 0x4000_0000) break;

      byte[] output;
      int used;
      try {
        using var input = new MemoryStream(payload[(position + 5)..].ToArray(), writable: false);
        using var sink = new BoundedStream(budget);
        new LzmaDecoder(input, payload.Slice(position, 5).ToArray()).Decode(sink);
        output = sink.ToArray();
        used = (int)input.Position;
      } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or NotSupportedException) {
        break;
      }

      if (output.Length < MinimumStreamOutput || used <= 5) break;
      streams.Add(output);
      budget -= output.Length;
      position += 5 + used;
      consumed = position;
    }

    return streams;
  }

  /// <summary>
  /// A write-only sink that refuses to grow past a byte budget, so a section of noise that
  /// happens to open like an LZMA stream cannot decode into an unbounded allocation.
  /// </summary>
  private sealed class BoundedStream(int budget) : Stream {
    private readonly MemoryStream _inner = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this._inner.Length;
    public override long Position { get => this._inner.Position; set => throw new NotSupportedException(); }

    public byte[] ToArray() => this._inner.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => this.Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) {
      if (this._inner.Length + buffer.Length > budget) throw new InvalidDataException("EXpressor: LZMA stream exceeded the decompression budget.");
      this._inner.Write(buffer);
    }

    public override void WriteByte(byte value) {
      if (this._inner.Length + 1 > budget) throw new InvalidDataException("EXpressor: LZMA stream exceeded the decompression budget.");
      this._inner.WriteByte(value);
    }

    public override void Flush() => this._inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing) {
      if (disposing) this._inner.Dispose();
      base.Dispose(disposing);
    }
  }
}
