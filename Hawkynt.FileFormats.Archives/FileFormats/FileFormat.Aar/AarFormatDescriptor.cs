#pragma warning disable CS1591
using FileFormat.ZipContainer;

namespace FileFormat.Aar;

/// <summary>
/// Android Archive (AAR) — the ZIP an Android library module builds: <c>AndroidManifest.xml</c>,
/// <c>classes.jar</c>, <c>R.txt</c> and optional <c>res/</c>, <c>jni/</c>, <c>assets/</c>, <c>lint.jar</c>.
/// Recognised by extension only (a plain ZIP otherwise).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://developer.android.com/studio/projects/android-library#aar-contents</c> — Android developer documentation, AAR contents</description></item>
///   <item><description>PRONOM <c>fmt/2043</c> — Android Archive File</description></item>
/// </list>
/// </summary>
public sealed class AarFormatDescriptor : ModifiableZipContainerFormatDescriptor {

  /// <inheritdoc />
  public override string DisplayName => "AAR (Android library)";

  /// <inheritdoc />
  public override IReadOnlyList<string> Extensions => [".aar"];

  /// <inheritdoc />
  public override string Description => "Android library archive (ZIP-based)";
}
