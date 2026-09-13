namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a noise guard.
/// </summary>
public class NoiseGuard
{
    /// <summary>
    /// Initializes a new instance of <see cref="NoiseGuard"/>.
    /// </summary>
    public NoiseGuard(int low, int high, int fix)
    {
        Low = low;
        High = high;
        Fixed = fix;
    }

    /// <summary>
    /// Gets the low.
    /// </summary>
    public int Low { get; }
    /// <summary>
    /// Gets the high.
    /// </summary>
    public int High { get; }
    /// <summary>
    /// Gets the fixed.
    /// </summary>
    public int Fixed { get; }
}
