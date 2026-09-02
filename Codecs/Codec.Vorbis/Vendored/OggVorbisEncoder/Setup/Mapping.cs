using System;
using System.Linq;

namespace OggVorbisEncoder.Setup;

/// <summary>
/// Represents a mapping.
/// </summary>
public class Mapping
{
    /// <summary>
    /// Initializes a new instance of <see cref="Mapping"/>.
    /// </summary>
public Mapping(
        int submaps,
        int[] channelMuxList,
        int[] floorSubMap,
        int[] residueSubMap,
        int couplingSteps,
        int[] couplingMag,
        int[] couplingAng)
    {
        if (floorSubMap?.Length != residueSubMap?.Length)
            throw new ArgumentException($"{nameof(floorSubMap)} and {nameof(residueSubMap)} must be the same size");

        if (couplingMag?.Length != couplingAng?.Length)
            throw new ArgumentException($"{nameof(couplingMag)} and {nameof(couplingAng)} must be the same size");

        SubMaps = submaps;
        ChannelMuxList = channelMuxList;
        FloorSubMap = floorSubMap;
        ResidueSubMap = residueSubMap;
        CouplingSteps = couplingSteps;
        CouplingMag = couplingMag;
        CouplingAng = couplingAng;
    }

    /// <summary>
    /// Gets the sub maps.
    /// </summary>
public int SubMaps { get; }

    /// <summary>
    /// Gets the channel mux list.
    /// </summary>
public int[] ChannelMuxList { get; } // up to 256 channels in a Vorbis stream

    /// <summary>
    /// Gets the floor sub map.
    /// </summary>
public int[] FloorSubMap { get; } // [mux] submap to floors
    /// <summary>
    /// Gets the residue sub map.
    /// </summary>
public int[] ResidueSubMap { get; } // [mux] submap to residue

    /// <summary>
    /// Gets the coupling steps.
    /// </summary>
public int CouplingSteps { get; }

    /// <summary>
    /// Gets the coupling mag.
    /// </summary>
public int[] CouplingMag { get; }
    /// <summary>
    /// Gets the coupling ang.
    /// </summary>
public int[] CouplingAng { get; }

    /// <summary>
    /// Performs the clone operation.
    /// </summary>
public Mapping Clone() => new Mapping(
        SubMaps,
        ChannelMuxList.ToArray(),
        FloorSubMap.ToArray(),
        ResidueSubMap.ToArray(),
        CouplingSteps,
        CouplingMag.ToArray(),
        CouplingAng.ToArray());
}
