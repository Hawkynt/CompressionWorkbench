using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;

namespace Compression.Mounting;

/// <summary>
/// Durable undo journal around explicitly qualified native archive edits.
/// The source is not modified until an entire digest-verified copy has been
/// persisted to a same-directory journal. A remaining journal is replayed
/// before the next mount; a verification failure rolls the edit back.
/// </summary>
/// <remarks>
/// Atomic renaming of the journal file is provided by its host filesystem.
/// Without an fsync of the parent directory, power-loss durability of the
/// *directory entry* cannot be promised portably by managed .NET alone.
/// Native in-place mode must therefore remain an explicit opt-in rather than
/// a blanket promise based on IArchiveModifiable.
/// </remarks>
public static class JournaledArchiveWriteback {
  private static ReadOnlySpan<byte> Magic => "CWBUNDO1"u8;
  private const int HeaderSize = 8 + sizeof(long) + 32;

  public static string JournalPath(string sourcePath) {
    ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
    return sourcePath + ".cwb-undo";
  }

  /// <summary>
  /// Applies a native mutation directly to a named archive after persisting an
  /// undo journal. Validates the result before discarding the journal. Any
  /// failure after the journal was published restores the original bytes.
  /// </summary>
  public static void Apply(
      FileStream source,
      Action<Stream> mutate,
      Action<Stream> validate) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(mutate);
    ArgumentNullException.ThrowIfNull(validate);
    if (!source.CanRead || !source.CanWrite || !source.CanSeek)
      throw new NotSupportedException("Journalled archive edits need a named readable/writable/seekable file.");

    var saved = source.Position;
    var journal = JournalPath(source.Name);
    if (File.Exists(journal))
      throw new IOException($"A pending recovery journal exists for '{source.Name}'; recover before editing.");

    var intermediate = journal + "." + Guid.NewGuid().ToString("N") + ".tmp";
    var published = false;
    try {
      WriteUndo(source, intermediate);
      File.Move(intermediate, journal);
      published = true;

      source.Position = 0;
      mutate(source);
      source.Flush(flushToDisk: true);
      source.Position = 0;
      validate(source);
      source.Flush(flushToDisk: true);
      File.Delete(journal);
      published = false;
    } catch (Exception failed) {
      if (published) {
        try {
          RecoverIfRequired(source);
        } catch (Exception rollbackError) {
          throw new AggregateException(
            "In-place archive edit failed and the persistent undo journal could not be replayed. Do not delete the journal.",
            failed, rollbackError);
        }
      }
      ExceptionDispatchInfo.Capture(failed).Throw();
      throw;
    } finally {
      if (!published && File.Exists(intermediate))
        File.Delete(intermediate);
      source.Position = Math.Min(saved, source.Length);
    }
  }

  /// <summary>
  /// Restores original bytes from a surviving same-directory undo journal.
  /// First verifies the complete stored backup; corrupt journals refuse to
  /// touch the source. Recovery is idempotent until the journal is deleted.
  /// </summary>
  public static bool RecoverIfRequired(FileStream source) {
    ArgumentNullException.ThrowIfNull(source);
    var path = JournalPath(source.Name);
    if (!File.Exists(path))
      return false;
    if (!source.CanRead || !source.CanWrite || !source.CanSeek)
      throw new IOException("A pending archive undo journal requires writable access for recovery.");

    var saved = source.Position;
    using var reader = new FileStream(path, FileMode.Open, FileAccess.Read,
      FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
    Span<byte> header = stackalloc byte[HeaderSize];
    reader.ReadExactly(header);
    if (!header[..Magic.Length].SequenceEqual(Magic))
      throw new InvalidDataException("The archive undo journal has an invalid signature.");

    var size = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(Magic.Length, sizeof(long)));
    if (size < 0 || reader.Length != HeaderSize + size)
      throw new InvalidDataException("The archive undo journal has an invalid original size.");

    using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)) {
      var buffer = new byte[128 * 1024];
      for (long remaining = size; remaining > 0;) {
        var count = reader.Read(buffer.AsSpan(0, checked((int)Math.Min(remaining, buffer.Length))));
        if (count == 0)
          throw new EndOfStreamException("Truncated archive undo journal.");
        hash.AppendData(buffer.AsSpan(0, count));
        remaining -= count;
      }
      var digest = hash.GetHashAndReset();
      if (!digest.AsSpan().SequenceEqual(header[16..]))
        throw new InvalidDataException("The archive undo journal checksum does not match its data.");
    }

    reader.Position = HeaderSize;
    source.Position = 0;
    reader.CopyTo(source);
    source.SetLength(size);
    source.Flush(flushToDisk: true);
    source.Position = Math.Min(saved, size);
    reader.Dispose();
    File.Delete(path);
    return true;
  }

  private static void WriteUndo(FileStream source, string path) {
    var saved = source.Position;
    try {
      using var journal = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
        FileShare.Read | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
      journal.Write(Magic);
      Span<byte> length = stackalloc byte[sizeof(long)];
      BinaryPrimitives.WriteInt64LittleEndian(length, source.Length);
      journal.Write(length);
      journal.Write(new byte[32]);

      source.Position = 0;
      using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
      var buffer = new byte[128 * 1024];
      for (long remaining = source.Length; remaining > 0;) {
        var count = source.Read(buffer.AsSpan(0, checked((int)Math.Min(remaining, buffer.Length))));
        if (count <= 0)
          throw new EndOfStreamException("Source archive changed while creating the undo journal.");
        journal.Write(buffer.AsSpan(0, count));
        hash.AppendData(buffer.AsSpan(0, count));
        remaining -= count;
      }
      journal.Position = 16;
      journal.Write(hash.GetHashAndReset());
      journal.Flush(flushToDisk: true);
    } finally {
      source.Position = saved;
    }
  }
}
