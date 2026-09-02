namespace OggVorbisEncoder.Lookups;

/// <summary>
/// Represents a delta.
/// </summary>
public struct Delta
{
    /// <summary>
    /// Initializes a new instance of <see cref="Delta"/>.
    /// </summary>
public Delta(
        float min,
        float max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>
    /// Provides the min value.
    /// </summary>
public float Min;
    /// <summary>
    /// Provides the max value.
    /// </summary>
public float Max;
}
