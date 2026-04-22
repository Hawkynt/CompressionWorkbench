namespace Compression.Registry;

/// <summary>
/// Opt-in capability: the descriptor can reject inputs that don't belong in this archive
/// type. Applied by the UI (drag-drop prohibition cursor + tooltip) and the CLI (rejection
/// with non-zero exit) before <see cref="IArchiveCreatable.Create"/> or
/// <see cref="IArchiveModifiable.Add"/> is called.
/// <para>
/// Descriptors that accept anything (ZIP, TAR, …) simply don't implement this interface.
/// </para>
/// </summary>
public interface IArchiveWriteConstraints {
  /// <summary>
  /// Evaluates <paramref name="input"/> against the descriptor's rules. Returns <c>false</c>
  /// with a human-readable <paramref name="reason"/> when the input is rejected.
  /// </summary>
  bool CanAccept(ArchiveInputInfo input, out string? reason);

  /// <summary>
  /// Maximum cumulative size of all inputs the archive can hold, in bytes. Null means no
  /// inherent size ceiling (most formats). Fixed-size disk images (C64 D64 = 174848,
  /// Amiga ADF = 901120, PC 1.44 MB floppy = 1474560) expose their limit here so the UI
  /// can reject drops that would overflow.
  /// </summary>
  long? MaxTotalArchiveSize { get; }

  /// <summary>
  /// Minimum total image size the format requires, in bytes. Null (default) means no
  /// floor. Filesystem images (UDF ≈ 1 MB, XFS = 16 MB, ReiserFS = 128 MB) advertise
  /// their real-world minimum-viable size here so UI can warn before the writer produces
  /// a round-tripable but mount-rejected image.
  /// </summary>
  long? MinTotalArchiveSize => null;

  /// <summary>
  /// One-line human summary shown in UI tooltips on rejection — e.g. <c>"accepts:
  /// metadata.ini, cover.jpg/png, lyrics.txt"</c> for an MP3 archive.
  /// </summary>
  string AcceptedInputsDescription { get; }
}
