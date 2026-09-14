#pragma warning disable CS1591
namespace FileSystem.JuiceFs;

internal static class JuiceFsShrinker {
  public static void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead)
      throw new ArgumentException("JuiceFS shrink input must be readable.", nameof(input));
    if (!output.CanWrite)
      throw new ArgumentException("JuiceFS shrink output must be writable.", nameof(output));

    if (ReferenceEquals(input, output)) {
      ShrinkInPlace(input);
      return;
    }

    if (!input.CanSeek) {
      using var bufferedInput = CreateScratchStream();
      input.CopyTo(bufferedInput);
      bufferedInput.Position = 0;
      Shrink(bufferedInput, output);
      return;
    }

    var originalPosition = input.Position;
    JuiceFsBackupKind kind;
    try {
      input.Position = 0;
      using var reader = new JuiceFsReader(input);
      kind = reader.Kind;
    } finally {
      input.Position = originalPosition;
    }

    if (kind != JuiceFsBackupKind.Json) {
      CopyOriginal(input, output);
      return;
    }

    using var candidate = CreateScratchStream();
    input.Position = 0;
    MinifyJson(input, candidate);
    candidate.Flush();

    var valid = false;
    try {
      candidate.Position = 0;
      using var check = new JuiceFsReader(candidate);
      valid = check.Kind == JuiceFsBackupKind.Json;
    } catch (InvalidDataException) {
      valid = false;
    }

    var originalLength = input.Length;
    PrepareOutput(output);
    if (valid && candidate.Length < originalLength) {
      candidate.Position = 0;
      candidate.CopyTo(output);
    } else {
      CopyOriginal(input, output, truncate: false);
    }
    output.Flush();
  }

  private static void ShrinkInPlace(Stream stream) {
    if (!stream.CanSeek)
      throw new ArgumentException("JuiceFS in-place shrink requires a seekable stream.", nameof(stream));

    var originalPosition = stream.Position;
    using var original = CreateScratchStream();
    try {
      stream.Position = 0;
      stream.CopyTo(original);
      original.Flush();
    } finally {
      stream.Position = originalPosition;
    }

    using var replacement = CreateScratchStream();
    original.Position = 0;
    Shrink(original, replacement);
    replacement.Position = 0;

    try {
      ReplaceContents(stream, replacement);
    } catch (Exception commitException) {
      try {
        original.Position = 0;
        ReplaceContents(stream, original);
        stream.Position = originalPosition;
      } catch (Exception rollbackException) {
        throw new IOException(
          "JuiceFS in-place shrink failed and restoring the original stream also failed.",
          new AggregateException(commitException, rollbackException));
      }
      throw;
    }
  }

  private static void ReplaceContents(Stream target, Stream source) {
    target.Position = 0;
    target.SetLength(0);
    source.Position = 0;
    source.CopyTo(target);
    target.SetLength(target.Position);
    target.Flush();
    target.Position = 0;
  }

  private static void MinifyJson(Stream input, Stream output) {
    var buffer = new byte[64 * 1024];
    var inString = false;
    var escaped = false;
    while (true) {
      var read = input.Read(buffer, 0, buffer.Length);
      if (read == 0)
        break;
      for (var i = 0; i < read; ++i) {
        var b = buffer[i];
        if (inString) {
          output.WriteByte(b);
          if (escaped) {
            escaped = false;
            continue;
          }
          if (b == (byte)'\\') escaped = true;
          else if (b == (byte)'"') inString = false;
          continue;
        }
        if (b == (byte)'"') {
          inString = true;
          output.WriteByte(b);
          continue;
        }
        if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
          continue;
        output.WriteByte(b);
      }
    }
  }

  private static void CopyOriginal(Stream input, Stream output, bool truncate = true) {
    input.Position = 0;
    if (truncate)
      PrepareOutput(output);
    input.CopyTo(output);
    output.Flush();
  }

  private static void PrepareOutput(Stream output) {
    if (!output.CanSeek)
      return;
    output.Position = 0;
    output.SetLength(0);
  }

  private static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_juicefs_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
}
