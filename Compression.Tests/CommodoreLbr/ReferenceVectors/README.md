# Commodore LBR reference vectors

All three are real containers built by the C64 "Library" program. They come from the Archive Team
sample corpus at <http://sembiance.com/fileFormatSamples/archive/c64Library/> (linked from
<http://fileformats.archiveteam.org/wiki/LBR_(Commodore)>), downloaded 2026-10-30 and unmodified.

| File | Members | Notes |
| --- | --- | --- |
| `chess.lbr` | 3 PRG | 24 bytes of 0x1A XMODEM padding after the last member |
| `joplin.lbr` | 3 PRG | no padding |
| `blkbox.lbr` | 4 PRG + 1 SEQ | 364 bytes of 0x1A padding |

The format has no checksum. The evidence is structural:
- every directory, written back by `CommodoreLbr.Write`, is byte-identical to Library's;
- the member sizes account for every byte up to the transfer padding;
- each PRG starts with a plausible C64 load address.

`CommodoreLbrTests` records each member's SHA-256 prefix.
