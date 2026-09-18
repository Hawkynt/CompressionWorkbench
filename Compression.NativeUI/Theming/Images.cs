using System.Collections.Concurrent;
using Hawkynt.NativeForms.Backends;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Theming;

/// <summary>
/// Turns raw pixels into backend images. NativeForms creates images through the active platform
/// backend, so nothing here can run before <see cref="BackendRegistry"/> has one registered — every
/// entry point is lazy for that reason.
/// </summary>
internal static class Images {
  private static readonly ConcurrentDictionary<(string Key, int Size), IImage> IconCache = new();

  /// <summary>Wraps a straight-ARGB buffer as a backend image.</summary>
  public static IImage FromArgb(int width, int height, ReadOnlySpan<int> argb)
    => BackendRegistry.Resolve().CreateImage(width, height, argb);

  /// <summary>Renders (and caches) one of the shell's icons at the requested size.</summary>
  public static IImage Icon(string key, int size = 16)
    => IconCache.GetOrAdd((key, size), static k => FromArgb(k.Size, k.Size, IconSet.Render(k.Key, k.Size)));
}
