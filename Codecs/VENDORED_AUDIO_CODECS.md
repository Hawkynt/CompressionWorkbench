# Vendored managed audio codec sources

The audio codec projects compile the following source copies directly. They are not NuGet, native, runtime, submodule, or build-time dependencies.

| Codec | Source | Pinned revision | License | Local path |
|---|---|---|---|---|
| Opus | `lostromb/concentus` | `3885c4e46513ef0fc81fca100189e54f1714c6ca` | See preserved upstream `LICENSE.txt` | `Codec.Opus/Vendored/Concentus/` |
| Vorbis | `SteveLillis/.NET-Ogg-Vorbis-Encoder` | `9211018f92f09cff58bb0e98e3af322e83c48f3c` | MIT | `Codec.Vorbis/Vendored/OggVorbisEncoder/` |
| MP3 | `jongoochgithub/GroovyCodecs` | `007bb4fc160180ad0af2cf8b1566250677c801f1` | LGPL-3.0 | `Codec.Mp3/Vendored/GroovyCodecs/` |
| AMR-WB | `PhrSite/SipLib` | `297c28be53f97b36e6acc73c56d7b1251b5b25f4` | See preserved upstream license where supplied | `Codec.AmrWb/Vendored/AmrWbLib/` |

The copies retain their upstream notices. Integration code belongs to the corresponding `Codec.*` project and uses the repository-wide .NET 10 / C# 14 build settings. Package references to these codec implementations are intentionally absent.
