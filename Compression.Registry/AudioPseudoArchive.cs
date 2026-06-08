namespace Compression.Registry;

/// <summary>
/// Shared plumbing for audio containers surfaced as pseudo-archives. The model
/// separates the CONTAINER from the DATA it carries: the pseudo-archive is the
/// container format itself, and every listed entry is a pseudo-file of carried
/// data. Kinds encode that distinction —
/// <list type="bullet">
///   <item><c>Container</c> — the byte-exact original container (<c>FULL.&lt;ext&gt;</c>);
///     round-trips the file unchanged.</item>
///   <item><c>Stream</c> — a carried elementary bitstream (e.g. an Ogg logical
///     stream's packets) still in its coded form.</item>
///   <item><c>Track</c> — a carried audio/video track in multi-track containers,
///     or one rendered subtune of a multi-song chiptune.</item>
///   <item><c>Channel</c> — one decoded speaker as a playable mono PCM WAV
///     (named per <c>Codec.Pcm.ChannelLayout</c>, mono through 22.2 and beyond).</item>
///   <item><c>Tag</c> — carried metadata (comments, ID3, bext, …).</item>
/// </list>
/// A descriptor builds the <see cref="Entry"/> list (the format-specific part) and
/// delegates listing, on-disk extraction and single-entry streaming here.
/// <para><b>Eager vs. lazy entries.</b> An entry's payload may be supplied eagerly
/// as a <see cref="byte"/>[] (the common case for already-parsed blobs) or lazily
/// through a producing factory plus a declared byte size (for expensive renders such
/// as emulated chiptune subtunes). For a lazy entry the declared size is exact and
/// deterministic — a render's WAV byte count is fully predictable — so listing reports
/// it without ever invoking the factory; the factory runs only when that specific
/// entry is extracted, and its result is cached so a repeat extraction does not
/// re-render.</para>
/// </summary>
public static class AudioPseudoArchive {

  /// <summary>
  /// One surfaced pseudo-archive entry with its display <see cref="Kind"/> and codec
  /// <see cref="Method"/>. The payload is either eager (<see cref="Data"/> set at
  /// construction) or lazy (produced on demand by <see cref="Factory"/> with the byte
  /// count declared up-front in <see cref="DeclaredSize"/>); see <see cref="Lazy"/>.
  /// </summary>
  public sealed class Entry {

    /// <summary>The display name (path-like; may contain <c>/</c> separators).</summary>
    public string Name { get; }

    /// <summary>The display kind (Container/Stream/Track/Channel/Tag).</summary>
    public string Kind { get; }

    /// <summary>The codec/method label reported in listings.</summary>
    public string Method { get; }

    private byte[]? _data;
    private readonly Func<byte[]>? _factory;
    private readonly long _declaredSize;

    /// <summary>
    /// Builds an <b>eager</b> entry whose payload is already materialised. This is the
    /// long-standing entry shape relied on by the bulk of the audio descriptors and must
    /// keep compiling unchanged.
    /// </summary>
    public Entry(string Name, string Kind, byte[] Data, string Method = "stored") {
      this.Name = Name;
      this.Kind = Kind;
      this.Method = Method;
      this._data = Data;
      this._factory = null;
      this._declaredSize = Data.Length;
    }

    private Entry(string name, string kind, Func<byte[]> factory, long declaredSize, string method) {
      this.Name = name;
      this.Kind = kind;
      this.Method = method;
      this._data = null;
      this._factory = factory;
      this._declaredSize = declaredSize;
    }

    /// <summary>
    /// Builds a <b>lazy</b> entry: <paramref name="factory"/> produces the payload only when
    /// the entry is extracted, and <paramref name="declaredSize"/> is the exact byte count
    /// the factory will yield (used for listing without invoking the factory). The produced
    /// bytes are cached on first materialisation so a second extraction reuses them.
    /// </summary>
    public static Entry Lazy(string name, string kind, Func<byte[]> factory, long declaredSize, string method = "stored")
      => new(name, kind, factory, declaredSize, method);

    /// <summary>The declared byte size — the materialised payload length for an eager entry,
    /// or the producer's promised output length for a lazy one (no factory invocation).</summary>
    public long DeclaredSize => this._declaredSize;

    /// <summary>True for a lazy entry whose payload has not yet been produced (becomes false once
    /// the factory has run and the result is cached).</summary>
    public bool IsLazy => this._factory is not null && this._data is null;

    /// <summary>
    /// Returns the payload, invoking and caching the factory on first access for a lazy entry.
    /// </summary>
    public byte[] Materialize() {
      if (this._data is { } cached)
        return cached;
      var produced = this._factory!();
      this._data = produced;
      return produced;
    }
  }

  /// <summary>
  /// Projects built entries into <see cref="ArchiveEntryInfo"/> rows for listing. Lazy
  /// entries report their <see cref="Entry.DeclaredSize"/> without being materialised, so
  /// listing stays fast regardless of how expensive a render would be.
  /// </summary>
  public static List<ArchiveEntryInfo> List(IReadOnlyList<Entry> entries)
    => entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.DeclaredSize, CompressedSize: e.DeclaredSize,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>Writes the entries to <paramref name="outputDir"/>, honouring an optional name
  /// filter. Only the entries actually written are materialised.</summary>
  public static void Extract(IReadOnlyList<Entry> entries, string outputDir, string[]? files) {
    foreach (var e in entries) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Materialize());
    }
  }

  /// <summary>Streams a single named entry to <paramref name="output"/>, materialising only
  /// the requested entry.</summary>
  public static void ExtractEntry(IReadOnlyList<Entry> entries, string entryName, Stream output) {
    foreach (var e in entries)
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Materialize());
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }
}
