using Compression.Registry;

namespace Compression.Sfx;

/// <summary>What will extract the payload, and what to call it.</summary>
/// <param name="FormatName">Shown to the person running the archive.</param>
/// <param name="Extract">Writes the payload's contents into a directory.</param>
public sealed record SfxExtractor(string FormatName, Action<Stream, string> Extract);

/// <summary>
/// Decides which reader extracts the payload. The two tiers answer this very differently, which is
/// the whole reason the tiers exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carved</b> knows its one format at compile time and names the descriptor directly, so the
/// linker can drop every other format in the reference closure. Measured: a zip stub costs about
/// 20 KB more than an empty native binary.
/// </para>
/// <para>
/// <b>Universal</b> carries every archive descriptor and matches magic bytes at run time. Measured
/// at roughly 11 MB for 237 formats, against 33 MB if it went through <c>Compression.Lib</c> and
/// dragged in the filesystem and audio descriptors too — neither of which can ever be a payload.
/// </para>
/// </remarks>
public static class SfxFormatResolver {
#if SFX_TIER_CARVED

#if !SFX_FORMAT_ZIP && !SFX_FORMAT_SEVENZIP && !SFX_FORMAT_TAR && !SFX_FORMAT_RAR && !SFX_FORMAT_CAB && !SFX_FORMAT_TARGZ && !SFX_FORMAT_TARXZ && !SFX_FORMAT_TARZST
  // A carved stub with no format would build, and then refuse every archive it was ever attached
  // to — inside someone else's download, where it is least debuggable.
#error SfxTier=Carved needs -p:SfxFormat=Zip|SevenZip|Tar|Rar|Cab|TarGz|TarXz|TarZst.
#endif

  /// <summary>Returns the one reader this stub was carved around.</summary>
  /// <param name="payload">The bounded payload; unused here, since the format is already known.</param>
  public static SfxExtractor? Resolve(Stream payload) {
    _ = payload;
    var operations = CreateCarvedOperations();
    return new(CarvedFormatName, (stream, directory) => operations.Extract(stream, directory, null, null));
  }

  private const string CarvedFormatName =
#if SFX_FORMAT_ZIP
    "Zip";
#elif SFX_FORMAT_SEVENZIP
    "7-Zip";
#elif SFX_FORMAT_TAR
    "Tar";
#elif SFX_FORMAT_RAR
    "RAR";
#elif SFX_FORMAT_CAB
    "CAB";
#elif SFX_FORMAT_TARGZ
    "tar.gz";
#elif SFX_FORMAT_TARXZ
    "tar.xz";
#else
    "tar.zst";
#endif

  private static IArchiveFormatOperations CreateCarvedOperations() =>
#if SFX_FORMAT_ZIP
    new FileFormat.Zip.ZipFormatDescriptor();
#elif SFX_FORMAT_SEVENZIP
    new FileFormat.SevenZip.SevenZipFormatDescriptor();
#elif SFX_FORMAT_TAR
    new FileFormat.Tar.TarFormatDescriptor();
#elif SFX_FORMAT_RAR
    new FileFormat.Rar.RarFormatDescriptor();
#elif SFX_FORMAT_CAB
    new FileFormat.Cab.CabFormatDescriptor();
#elif SFX_FORMAT_TARGZ
    CompoundTar("Gzip");
#elif SFX_FORMAT_TARXZ
    CompoundTar("Xz");
#else
    CompoundTar("Zstd");
#endif

#if SFX_FORMAT_TARGZ || SFX_FORMAT_TARXZ || SFX_FORMAT_TARZST
  /// <summary>
  /// Compound tar is the one family that cannot simply be named. <c>CompoundTarDescriptor</c>
  /// resolves its two halves through string keys against the registry, which a carved stub does not
  /// populate — that would trim to a null dereference at extraction time rather than a link error.
  /// So the stub registers exactly the two descriptors it needs and nothing else.
  /// </summary>
  private static IArchiveFormatOperations CompoundTar(string streamFormatId) {
    FormatRegistry.Register(new FileFormat.Tar.TarFormatDescriptor());
    FormatRegistry.Register(StreamDescriptor());
    FormatRegistry.Initialize();

    var name = "tar." + streamFormatId.ToLowerInvariant();
    return new CompoundTarDescriptor("Tar" + streamFormatId, name, streamFormatId, "." + name, ["." + name]);
  }

  private static IFormatDescriptor StreamDescriptor() =>
#if SFX_FORMAT_TARGZ
    new FileFormat.Gzip.GzipFormatDescriptor();
#elif SFX_FORMAT_TARXZ
    new FileFormat.Xz.XzFormatDescriptor();
#else
    new FileFormat.Zstd.ZstdFormatDescriptor();
#endif
#endif

#else

  /// <summary>How far into a compressed payload to look for a tar header.</summary>
  private const int TarProbeLength = 512;

  /// <summary>
  /// Identifies the payload by magic bytes across every registered descriptor and returns what
  /// extracts it. Extension-based detection is deliberately absent: an SFX has no payload filename.
  /// </summary>
  /// <param name="payload">The bounded payload, which is rewound before returning.</param>
  /// <remarks>
  /// A match on a single-stream codec is not the end of the question. A .tar.gz is gzip by its
  /// magic and tar by its content, and the library only tells the two apart by file extension —
  /// which a self-extractor does not have. So the stub decompresses the first header's worth and
  /// looks: a tar inside goes to the compound descriptor, anything else is decompressed as the
  /// single file it is.
  /// </remarks>
  public static SfxExtractor? Resolve(Stream payload) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var header = new byte[512];
    payload.Position = 0;
    var read = payload.Read(header, 0, header.Length);
    payload.Position = 0;
    if (read <= 0) return null;

    if (BestMatch(header.AsSpan(0, read)) is not { } best) return null;

    if (FormatRegistry.GetArchiveOps(best.Id) is { } archive)
      return new(best.DisplayName, (stream, directory) => archive.Extract(stream, directory, null, null));

    if (FormatRegistry.GetStreamOps(best.Id) is not { } codec) return null;

    // Several formats are "tar inside gzip" — a Rust .crate is one — and only their extension tells
    // them apart. With no extension to go on, the plain compound tar is the honest answer, so it is
    // preferred over whichever specialisation happens to be registered first.
    if (ContainsTar(codec, payload)
        && FormatRegistry.All
          .Where(d => d.TarCompressionFormatId == best.Id)
          .OrderByDescending(d => d is CompoundTarDescriptor)
          .FirstOrDefault() is { } compound
        && FormatRegistry.GetArchiveOps(compound.Id) is { } compoundArchive)
      return new(compound.DisplayName, (stream, directory) => compoundArchive.Extract(stream, directory, null, null));

    return new(best.DisplayName, (stream, directory) => DecompressSingleFile(codec, stream, directory));
  }

  private static IFormatDescriptor? BestMatch(ReadOnlySpan<byte> header) {
    IFormatDescriptor? best = null;
    var bestConfidence = 0.0;

    foreach (var descriptor in FormatRegistry.All)
      foreach (var signature in descriptor.MagicSignatures)
        if (signature.Confidence > bestConfidence && Matches(header, signature)) {
          best = descriptor;
          bestConfidence = signature.Confidence;
        }

    return best;
  }

  /// <summary>
  /// Decompresses just enough to see whether the first 512 bytes form a tar header, then rewinds.
  /// </summary>
  private static bool ContainsTar(IStreamFormatOperations codec, Stream payload) {
    var probe = new byte[TarProbeLength];
    var have = 0;

    try {
      payload.Position = 0;
      if (codec.WrapDecompress(payload) is { } wrapped) {
        using (wrapped) {
          int n;
          while (have < probe.Length && (n = wrapped.Read(probe, have, probe.Length - have)) > 0)
            have += n;
        }
      } else {
        // A codec without a streaming reader has to decompress into something; stop it once the
        // header has arrived rather than inflating a whole payload just to read 512 bytes.
        using var sink = new PrefixCapture(probe);
        try {
          codec.Decompress(payload, sink);
        } catch (PrefixCapture.Full) {
          // Expected: the header is in.
        }

        have = sink.Captured;
      }
    } catch {
      // A payload that will not even start decompressing is certainly not a tar inside.
      return false;
    } finally {
      payload.Position = 0;
    }

    return have == TarProbeLength && IsTarHeader(probe);
  }

  /// <summary>
  /// POSIX tar says "ustar" at offset 257. The original Unix v7 format says nothing at all, so a
  /// header whose checksum adds up is accepted too — that sum is what tar itself validates.
  /// </summary>
  private static bool IsTarHeader(ReadOnlySpan<byte> header) {
    if (header.Slice(257, 5).SequenceEqual("ustar"u8)) return true;

    var stored = header.Slice(148, 8);
    var digits = stored.IndexOfAnyExcept((byte)' ');
    if (digits < 0) return false;

    long expected = 0;
    var any = false;
    foreach (var b in stored[digits..]) {
      if (b is (byte)' ' or 0) break;
      if (b is < (byte)'0' or > (byte)'7') return false;
      expected = expected * 8 + (b - '0');
      any = true;
    }
    if (!any) return false;

    long sum = 0;
    for (var i = 0; i < header.Length; ++i)
      sum += i is >= 148 and < 156 ? ' ' : header[i];

    return sum == expected;
  }

  /// <summary>
  /// A lone compressed file. Its original name is not recorded anywhere this stub can read, so the
  /// output is named plainly rather than guessed at.
  /// </summary>
  private static void DecompressSingleFile(IStreamFormatOperations codec, Stream payload, string directory) {
    using var output = File.Create(Path.Combine(directory, "extracted"));
    codec.Decompress(payload, output);
  }

  private static bool Matches(ReadOnlySpan<byte> header, MagicSignature signature) {
    var bytes = signature.Bytes;
    if (bytes.Length == 0 || signature.Offset < 0) return false;
    if (signature.Offset + bytes.Length > header.Length) return false;

    var window = header.Slice(signature.Offset, bytes.Length);
    var mask = signature.Mask;

    for (var i = 0; i < bytes.Length; ++i) {
      var actual = mask is null ? window[i] : (byte)(window[i] & mask[i]);
      var expected = mask is null ? bytes[i] : (byte)(bytes[i] & mask[i]);
      if (actual != expected) return false;
    }

    return true;
  }

  /// <summary>Keeps the first bytes written to it and stops the writer once it has enough.</summary>
  private sealed class PrefixCapture(byte[] buffer) : Stream {
    public sealed class Full : Exception;

    public int Captured { get; private set; }

    public override void Write(byte[] data, int offset, int count) => this.Write(data.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data) {
      var take = Math.Min(data.Length, buffer.Length - this.Captured);
      data[..take].CopyTo(buffer.AsSpan(this.Captured));
      this.Captured += take;
      if (this.Captured == buffer.Length) throw new Full();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => this.Captured;
    public override long Position { get => this.Captured; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] data, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
  }

#endif
}
