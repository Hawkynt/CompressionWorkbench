using Compression.Registry.Streaming;

namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can produce a fresh archive from a list of inputs (WORM).
/// Descriptors that do not implement this interface cannot be created from scratch.
/// </summary>
/// <remarks>
/// Separate from <see cref="IArchiveFormatOperations"/> so callers discover the capability at
/// the type level (<c>if (ops is IArchiveCreatable c) …</c>) instead of hitting a runtime
/// <c>NotSupportedException</c>.
/// </remarks>
public interface IArchiveCreatable {
  /// <summary>
  /// Produces a fresh archive at <paramref name="output"/> containing <paramref name="inputs"/>.
  /// Existing archive contents (if any) at <paramref name="output"/> are overwritten.
  /// </summary>
  void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options);

  /// <summary>
  /// Two-pass streaming variant of <see cref="Create"/>: <paramref name="inputs"/>
  /// is an enumerable of (name, size, openStream) tuples. Writers that override
  /// this method can use the pre-known sizes to plan layout/geometry in a first
  /// pass, then write the target stream and copy each entry's bytes via 64 KB
  /// chunks in a second pass — never holding an entry's bytes in RAM beyond the
  /// chunk buffer.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The default implementation buffers each input via
  /// <see cref="Func{Stream}.Invoke"/> into a byte array and dispatches to the
  /// classic <see cref="Create"/>. Descriptors that benefit from streaming
  /// (FAT, ext, ZIP/store) should override; ZIP DEFLATE, TAR (already
  /// streams writes) and 7z (solid-block, can't predict per-entry layout)
  /// are free to keep the default.
  /// </para>
  /// <para>
  /// The factories returned by <see cref="StreamingArchiveInput.OpenStream"/>
  /// are typically bounded entry streams, so the writer physically cannot
  /// copy past the source entry's logical size — slack, padding and
  /// adjacent-entry bytes are unreachable through this pipeline.
  /// </para>
  /// </remarks>
  public virtual void CreateFromStreams(
      Stream target,
      IEnumerable<StreamingArchiveInput> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(inputs);
    var buffered = new List<ArchiveInputInfo>();
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        buffered.Add(new ArchiveInputInfo(input.Name, input.Name, IsDirectory: true));
        continue;
      }
      using var src = input.OpenStream();
      using var ms = new MemoryStream();
      src.CopyTo(ms);
      buffered.Add(ArchiveInputInfo.InMemory(input.Name, ms.ToArray()));
    }
    this.Create(target, buffered, options);
  }
}
