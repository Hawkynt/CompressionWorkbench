#pragma warning disable CS1591

namespace FileSystem.JuiceFs;

internal enum JuiceFsBackupKind {
  Json,
  Binary,
}

/// <summary>
/// Reads portable JuiceFS metadata backups produced by <c>juicefs dump</c>.
/// JSON tree dumps and the v1.3+ segmented protobuf encoding are supported.
/// </summary>
/// <remarks>
/// Metadata backups contain namespace and chunk metadata, not file payload bytes.
/// Payload blocks remain in the configured object store, so this reader exposes
/// backup metadata only and never fabricates file content.
/// </remarks>
public sealed class JuiceFsReader : IDisposable {
  /// <summary>
  /// Legacy synthetic tag retained for source compatibility. Real JuiceFS
  /// metadata backups do not carry this marker and the reader does not use it.
  /// </summary>
  public static readonly byte[] DumpTag = "JuiceFS"u8.ToArray();

  /// <summary>Canonical binary-backup magic / end-of-segments marker.</summary>
  public const uint BakMagic = 0x00747083u;

  internal const uint BakVersion = 1u;

  private readonly byte[] _data;
  private readonly List<JuiceFsEntry> _entries = [];

  /// <summary>Gets the inspectable entries exposed from the backup.</summary>
  public IReadOnlyList<JuiceFsEntry> Entries => _entries;

  /// <summary>
  /// Legacy compatibility property. Real metadata backups have no offset-eight
  /// wrapper word, so this remains zero.
  /// </summary>
  public uint TrailingWord { get; private set; }

  /// <summary>Gets whether the supplied stream parsed as a real JuiceFS metadata backup.</summary>
  public bool ValidHeader { get; private set; }

  internal JuiceFsBackupKind Kind { get; private set; }
  internal uint BinaryVersion { get; private set; }
  internal int SegmentCount { get; private set; }

  /// <summary>Initializes a reader over a JuiceFS metadata backup.</summary>
  public JuiceFsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    _data = memory.ToArray();

    if (JuiceFsJsonBackup.TryParse(_data, _entries)) {
      this.Kind = JuiceFsBackupKind.Json;
      this.ValidHeader = true;
      return;
    }
    if (JuiceFsBinaryBackup.TryParse(_data, _entries, out var version, out var segmentCount)) {
      this.Kind = JuiceFsBackupKind.Binary;
      this.BinaryVersion = version;
      this.SegmentCount = segmentCount;
      this.ValidHeader = true;
      return;
    }

    throw new InvalidDataException(
      "JuiceFS: input is neither a valid juicefs dump JSON document nor a v1.3+ binary metadata backup.");
  }

  /// <summary>Extracts one metadata-backup entry.</summary>
  public byte[] Extract(JuiceFsEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.UsesSourceData)
      return entry.Data;
    if (entry.Offset < 0 || entry.Size < 0 || entry.Offset > _data.LongLength - entry.Size)
      throw new InvalidDataException($"JuiceFS entry '{entry.Name}' points outside the backup.");
    if (entry.Size > int.MaxValue)
      throw new IOException($"JuiceFS entry '{entry.Name}' is too large for buffered extraction.");
    return _data.AsSpan((int)entry.Offset, (int)entry.Size).ToArray();
  }

  /// <summary>Releases resources held by this reader.</summary>
  public void Dispose() { }
}
