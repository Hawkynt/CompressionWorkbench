namespace FileFormat.Vib;

/// <summary>A single file/directory decoded from a VIB payload tar.</summary>
/// <param name="Path">Entry path as stored in the payload tar.</param>
/// <param name="Data">Entry contents (empty for directories).</param>
/// <param name="IsDirectory">True when the entry is a directory.</param>
public sealed record VibEntry(string Path, byte[] Data, bool IsDirectory);
