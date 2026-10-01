namespace Compression.NativeUI.ViewModels;

/// <summary>
/// One maintenance target opened for the Defragment tab: the file the operations work on, its format,
/// what to do after the file changed, and what to clean up afterwards. A nested archive is worked on
/// as a temporary copy; disposing the session deletes it.
/// </summary>
internal sealed class MaintenanceSession(
  string key,
  string imagePath,
  string formatId,
  string displayName,
  Action<string> onMutated,
  Action? cleanup) : IDisposable {
  private Action? _cleanup = cleanup;

  /// <summary>Identifies the target, as <see cref="MainViewModel.MaintenanceTargetKey"/> does.</summary>
  public string Key { get; } = key;

  /// <summary>The file the operations read and write.</summary>
  public string ImagePath { get; } = imagePath;

  public string FormatId { get; } = formatId;

  /// <summary>The name the user knows the target by.</summary>
  public string DisplayName { get; } = displayName;

  /// <summary>Tells the shell the file changed, so it re-lists it and writes a nested copy back.</summary>
  public void NotifyMutated() => onMutated(this.ImagePath);

  public void Dispose() => Interlocked.Exchange(ref this._cleanup, null)?.Invoke();
}
