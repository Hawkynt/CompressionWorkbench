using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Builds a self-extracting archive that is simultaneously a Windows executable and a POSIX shell
/// script, so one file extracts itself on Windows, Linux and macOS.
/// </summary>
/// <remarks>
/// <para>
/// The trick is that a PE file's DOS header is inert to Windows beyond its first two bytes and its
/// <c>e_lfanew</c> field, while a shell reads the same bytes as text. Making byte 0 read
/// <c>MZ=1 #</c> gives the shell a harmless variable assignment followed by a comment that runs to
/// the end of the line — and the DOS header contains no newline, so the comment safely swallows
/// <c>e_lfanew</c> too. The DOS stub after it ("This program cannot be run in DOS mode") is dead
/// weight nobody reads, so the bootstrap goes there. No PE surgery is needed: the headers do not
/// move and no section offset changes.
/// </para>
/// <para>
/// On Linux and macOS this must be invoked as <c>sh archive.sfx</c> rather than
/// <c>./archive.sfx</c>. The kernel needs ELF magic or a <c>#!</c> line at offset zero and
/// <c>MZ</c> is already there; nothing can be done about that in a single file.
/// </para>
/// <para>
/// The archive payload stays contiguous and untouched at the end, exactly as in a single-target
/// SFX, so third-party tools that scan a PE overlay for their own signature still find and extract
/// it without running our stub at all.
/// </para>
/// </remarks>
public static class SfxPolyglot {
  private const int DosHeaderLength = 0x40;
  private const int LfanewOffset = 0x3C;

  /// <summary>Offsets are written zero-padded to a fixed width so the script's length is stable.</summary>
  private const int OffsetWidth = 12;

  /// <summary>A stub compiled for one runtime.</summary>
  /// <param name="Rid">The runtime identifier it was built for.</param>
  /// <param name="Bytes">The stub binary.</param>
  public readonly record struct TargetStub(string Rid, byte[] Bytes);

  /// <summary>
  /// Writes a polyglot self-extractor.
  /// </summary>
  /// <param name="outputPath">File to create.</param>
  /// <param name="windowsStub">The PE stub; this is what Windows executes and what gives the file its shape.</param>
  /// <param name="posixStubs">Stubs for POSIX runtimes, selected at run time by <c>uname</c>.</param>
  /// <param name="archive">The archive payload, copied verbatim.</param>
  /// <exception cref="ArgumentException">The Windows stub is not a usable PE.</exception>
  public static void Write(string outputPath, byte[] windowsStub, IReadOnlyList<TargetStub> posixStubs, Stream archive) {
    ArgumentNullException.ThrowIfNull(windowsStub);
    ArgumentNullException.ThrowIfNull(posixStubs);
    ArgumentNullException.ThrowIfNull(archive);

    var patched = PatchDosStub(windowsStub, posixStubs, out var selector);

    using var output = File.Create(outputPath);
    output.Write(patched);
    output.Write(selector);
    foreach (var stub in posixStubs) output.Write(stub.Bytes);

    var archiveOffset = output.Position;
    archive.CopyTo(output);
    SfxTrailer.Write(output, archiveOffset);
  }

  /// <summary>
  /// Overwrites the DOS stub with a shell bootstrap and returns the selector script that bootstrap
  /// will source. The PE headers are left exactly where they were.
  /// </summary>
  private static byte[] PatchDosStub(byte[] windowsStub, IReadOnlyList<TargetStub> posixStubs, out byte[] selector) {
    if (windowsStub.Length < DosHeaderLength + 4 || windowsStub[0] != 'M' || windowsStub[1] != 'Z')
      throw new ArgumentException("The Windows stub is not a PE file.", nameof(windowsStub));

    var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(windowsStub.AsSpan(LfanewOffset, 4));
    if (peHeaderOffset <= DosHeaderLength || peHeaderOffset > windowsStub.Length)
      throw new ArgumentException($"The Windows stub has an unusable e_lfanew ({peHeaderOffset}).", nameof(windowsStub));

    // Everything from 0x40 up to the PE header is ours; that region is the DOS stub program. One
    // byte of it goes to the newline that ends the shell's first line — which cannot sit any
    // earlier, because 0x3C..0x3F is e_lfanew and a newline there would corrupt the PE.
    var room = peHeaderOffset - DosHeaderLength - 1;

    // The selector's length has to be known before its own offset can be written into the
    // bootstrap, and the stub offsets it contains depend on that length in turn. Fixed-width
    // zero-padded numbers break the cycle: the text is rebuilt with real values and comes out the
    // same size as the placeholder pass.
    var selectorOffset = (long)windowsStub.Length;
    selector = Encoding.ASCII.GetBytes(BuildSelector(posixStubs, selectorOffset, placeholder: true));
    selector = Encoding.ASCII.GetBytes(BuildSelector(posixStubs, selectorOffset + selector.Length, placeholder: false));

    var bootstrap = BuildBootstrap(selectorOffset, selector.Length);
    if (bootstrap.Length > room)
      throw new ArgumentException(
        $"The DOS stub has {room} bytes but the bootstrap needs {bootstrap.Length}.", nameof(windowsStub));

    var patched = (byte[])windowsStub.Clone();

    // Line 1: "MZ" is required by the loader, "=1" makes it a shell assignment instead of an unknown
    // command, and "#" comments out the rest of the DOS header including e_lfanew. No byte in
    // 0x02..0x3F may be a newline or the comment ends early and the shell reads header bytes as code.
    patched[2] = (byte)'=';
    patched[3] = (byte)'1';
    patched[4] = (byte)' ';
    patched[5] = (byte)'#';
    for (var i = 6; i < LfanewOffset; ++i)
      if (patched[i] == (byte)'\n' || patched[i] == 0) patched[i] = (byte)' ';

    // e_lfanew stays byte-for-byte untouched, but it lives inside the comment, so a newline in it
    // would end line 1 early and feed header bytes to the shell as commands. A PE header at an
    // offset containing 0x0A is pathological rather than impossible, so this refuses rather than
    // shipping something that misbehaves only on POSIX.
    for (var i = LfanewOffset; i < DosHeaderLength; ++i)
      if (patched[i] == (byte)'\n')
        throw new ArgumentException("The stub's PE header offset contains a newline byte.", nameof(windowsStub));

    // Line 1 ends immediately after the DOS header, and the bootstrap follows on line 2. Anything
    // left over is newlines, which the shell reads as blank lines.
    Array.Fill(patched, (byte)'\n', DosHeaderLength, peHeaderOffset - DosHeaderLength);
    bootstrap.CopyTo(patched.AsSpan(DosHeaderLength + 1));
    return patched;
  }

  /// <summary>
  /// The few bytes that fit in a DOS stub: copy the selector script out of ourselves, run it, stop.
  /// </summary>
  private static byte[] BuildBootstrap(long selectorOffset, int selectorLength) {
    // tail counts bytes from one, not zero.
    var script =
      $"S=`mktemp`;tail -c +{selectorOffset + 1} \"$0\"|head -c {selectorLength}>$S;. $S;rm -f $S;exit\n";
    return Encoding.ASCII.GetBytes(script);
  }

  /// <summary>
  /// Picks the stub matching the running kernel and architecture, unpacks it, and hands it the
  /// original file so it can find the payload — the stub runs from a temporary copy, which has no
  /// payload appended to it.
  /// </summary>
  private static string BuildSelector(IReadOnlyList<TargetStub> posixStubs, long firstStubOffset, bool placeholder) {
    var script = new StringBuilder();
    script.Append("u=`uname -s`;a=`uname -m`\ncase \"$u:$a\" in\n");

    var offset = firstStubOffset;
    foreach (var stub in posixStubs) {
      var start = placeholder ? 0 : offset + 1;
      script.Append($"{UnamePattern(stub.Rid)}) O={start.ToString().PadLeft(OffsetWidth, '0')};");
      script.Append($"L={stub.Bytes.Length.ToString().PadLeft(OffsetWidth, '0')};;\n");
      offset += stub.Bytes.Length;
    }

    script.Append("*) echo \"No bundled extractor for $u $a.\" >&2;exit 1;;\nesac\n");
    script.Append("D=`mktemp`\n");
    script.Append("tail -c +$O \"$0\"|head -c $L>\"$D\"\n");
    script.Append("chmod +x \"$D\"\n");
    script.Append("CWB_SFX_SOURCE=\"$0\" \"$D\" \"$@\"\n");
    script.Append("r=$?;rm -f \"$D\";exit $r\n");
    return script.ToString();
  }

  /// <summary>Maps a runtime identifier onto what <c>uname -s</c> and <c>uname -m</c> report.</summary>
  private static string UnamePattern(string rid) => rid switch {
    "linux-x64" or "linux-musl-x64" => "Linux:x86_64",
    "linux-arm64" or "linux-musl-arm64" => "Linux:aarch64",
    "osx-x64" => "Darwin:x86_64",
    "osx-arm64" => "Darwin:arm64",
    _ => throw new ArgumentException($"'{rid}' is not a POSIX runtime this can select for.", nameof(rid)),
  };
}
