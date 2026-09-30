namespace Compression.NativeUI.Navigation;

/// <summary>Back and Forward, the way every file manager and browser does them.</summary>
/// <remarks>
/// Arriving somewhere by Back or Forward is reported to <see cref="Visit"/> like any other arrival;
/// it is recognised because the place is already the current one, which is what keeps Forward
/// alive after a step back.
/// </remarks>
internal sealed class NavigationHistory(int capacity = 100) {
  private readonly List<Location> _visited = [];
  private int _current = -1;

  /// <summary>True when there is somewhere behind the current place.</summary>
  public bool CanGoBack => this._current > 0;

  /// <summary>True when a step back left somewhere ahead.</summary>
  public bool CanGoForward => this._current >= 0 && this._current < this._visited.Count - 1;

  /// <summary>Records an arrival. Arriving where the shell already is records nothing.</summary>
  public void Visit(Location location) {
    if (this._current >= 0 && this._visited[this._current] == location) return;

    // A new place after stepping back replaces the trail that was ahead.
    this._visited.RemoveRange(this._current + 1, this._visited.Count - this._current - 1);
    this._visited.Add(location);

    if (this._visited.Count > capacity) this._visited.RemoveRange(0, this._visited.Count - capacity);
    this._current = this._visited.Count - 1;
  }

  /// <summary>Steps back, returning where to go, or null when there is nothing behind.</summary>
  public Location? Back() => this.CanGoBack ? this._visited[--this._current] : null;

  /// <summary>Steps forward, returning where to go, or null when there is nothing ahead.</summary>
  public Location? Forward() => this.CanGoForward ? this._visited[++this._current] : null;
}
