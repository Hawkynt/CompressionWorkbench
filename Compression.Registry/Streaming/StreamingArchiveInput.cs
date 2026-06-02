namespace Compression.Registry.Streaming;

/// <summary>
/// Describes a single streaming input for an archive write: a name, its size
/// (so two-pass writers can plan layout/geometry up front), whether it is a
/// directory placeholder, and a factory that opens its bytes as a
/// <see cref="Stream"/> on demand.
/// </summary>
/// <remarks>
/// <para>
/// The factory is invoked at most once per input by an
/// <see cref="IArchiveCreatable.CreateFromStreams"/> implementation; the
/// returned stream is disposed after consumption. The source <c>OpenStream</c>
/// is typically a <see cref="BoundedEntryStream"/> sized to the entry's
/// logical size, so the writer physically cannot copy slack or
/// adjacent-entry bytes through the pipeline.
/// </para>
/// <para>
/// Use this in place of <see cref="ArchiveInputInfo"/> when the source can
/// produce per-entry streams (e.g. a FAT cluster-chain reader, ZIP local
/// header DEFLATE wrapper, TAR positional slice) — it lets the conversion
/// pipeline stay bounded-memory regardless of source / target size.
/// </para>
/// </remarks>
/// <param name="Name">The entry's archive name (path-like, forward-slash).</param>
/// <param name="Size">The entry's logical byte size, used by two-pass writers
/// to compute geometry before any data is read. <c>0</c> for directory
/// placeholders.</param>
/// <param name="IsDirectory">When true, no <c>OpenStream</c> is required and
/// the entry represents a directory placeholder in the target.</param>
/// <param name="OpenStream">Factory that returns the entry's bytes as a
/// <see cref="Stream"/> — typically a <see cref="BoundedEntryStream"/>.
/// Ignored when <see cref="IsDirectory"/> is <c>true</c>.</param>
public sealed record StreamingArchiveInput(
  string Name,
  long Size,
  bool IsDirectory,
  Func<Stream> OpenStream
);
