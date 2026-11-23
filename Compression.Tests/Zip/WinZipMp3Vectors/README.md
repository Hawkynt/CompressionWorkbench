# ZIP method 94 (WinZip MP3) reference vectors

## The WinZip archive

From bwhitn/unpackio (https://github.com/bwhitn/unpackio, revision
`b70a5e23e75c83f045593e2106a26d9f62b2245f`, `crates/unpackio/tests/fixtures/method94/*.b64`),
base64-decoded here and released under that repository's MIT test-fixture terms (`CORPUS.md`,
"ZIP WinZip methods 94 and 96 fixtures").

| File | Bytes | SHA-256 | What it is |
|------|------:|---------|------------|
| `winzip21-method94.zipx` | 408 | `4cb0f2e7d5fae6f13d708ad79cf4721064675a50f6582d936aa41573e308a841` | Written by WinZip 21.0.12288 (64-bit evaluation edition) with ZIPX "Best method"; one member, method 94, version needed 2.0, 216-byte payload at local offset 59 |
| `method94-project-authored.mp3` | 55,587 | `372d875979967b2d95b48c2ded842a2a6bbd295c50d2455f8ad9829d2826aa0e` | The archived MP3 (MPEG-1 Layer III, 128 kbit/s, 44.1 kHz, mono; CRC-32 `E6CDB0AC`) |

The archive must extract to the MP3 byte for byte, and the payload is the PMP stream packMP3 v1.0g
writes for that MP3.

## packMP3 pairs

Each `<name>.pmp` is what packMP3 v1.0g (https://github.com/packjpg/packMP3, revision
`e61c11941552f4ffe6e219a847f441d9520d2e50`, LGPL-3.0) writes for `<name>.mp3`, and decodes back
to it.

- Pairs without a prefix come from the same unpackio revision,
  `crates/unpackio/tests/fixtures/method94/research/pairs` (MIT test-fixture terms; LAME 4.0
  encodes, packMP3 v1.0g recompresses; `research/manifest.json` there records every argument).
- `cwb_*` pairs are project-authored: synthetic tones encoded by FFmpeg 6.1.1 with libmp3lame, then
  packMP3 v1.0g built from the revision above with g++.

| File | Bytes | SHA-256 | Covers |
|------|------:|---------|--------|
| `stereo_intensity.mp3` | 2,085 | `ada1742f5400333160a4f2974d83c4b9861554ef9450e5dcea245241b4bc87d6` | intensity stereo, no main data |
| `stereo_intensity.pmp` | 29 | `7b77657aa97a104cf58ce3d4976455f678e93bc775419639328560e3179cb45f` | |
| `mono_crc.mp3` | 17,135 | `d37f0c69bd5437befccc722268e023323e6e64ab6c7eb7efee767bb3a3dbe364` | frame CRCs, mono |
| `mono_crc.pmp` | 12,818 | `aaf335c94dac8a9c0b133dae12fe5d4d3bb11941273b392535b20d6f951046a7` | |
| `stereo_crc.mp3` | 25,703 | `ccd78370aeb7fca89e8fcf125c55f8bdd8bf788b73de19fcb5bfff29ec6dcb2c` | frame CRCs, stereo |
| `stereo_crc.pmp` | 19,083 | `c533cff3be8c7807c1db34db07f2ac9fd942e8abc03df0731d96ff3606655613` | |
| `stereo_dual.mp3` | 17,135 | `aa29c334e4d87ccf0a135619dd50d42baa43ec9f6092ad0cab571589c0411db9` | dual channel |
| `stereo_dual.pmp` | 12,135 | `4d68ade34ec1d94f7f7b30ee0ac53a631a52913d6756b644695f8f21f721eb11` | |
| `stereo_force_ms.mp3` | 17,135 | `576596537a98096bb7e92da4f5eb5d7a7d4ed60036b328129fb5941d8bb9ddd7` | mid/side stereo in every frame |
| `stereo_force_ms.pmp` | 10,570 | `5700413589f7488346a6792cfe678d2f1e227cc5ab0e0a4171083a1cc5a2b901` | |
| `mono_emphasis_50_15.mp3` | 17,135 | `ed3f3eafdefd252adb0c9fa9983c3012f66c75949d7ed17d81b07f3d21e0a7fc` | 50/15 µs emphasis |
| `mono_emphasis_50_15.pmp` | 12,916 | `e4ee1427ab001ec4a2c32aadbba9cf098c0e51b7d0848ea20874f56cd32e50d8` | |
| `mono_id3v2.mp3` | 17,527 | `1b90bba0e1ec91a17130979411ee87f98eaa55d6ef35203a107c07284ced69d5` | ID3v2 tag before the audio |
| `mono_id3v2.pmp` | 13,072 | `b48ab2271323bb9d682beb982bdcf0239009b17ebcd67e16db62d04192b0201c` | |
| `mono_vbr_9.mp3` | 6,555 | `9ea1198f7b9e7b154589cb4fd25083e2dfd35ee57646f289baa86bd6b19a8355` | VBR (per-frame bitrate coding) |
| `mono_vbr_9.pmp` | 3,901 | `fa11f95cd6cbf6775a2676631649439b8dba15b25ca015465c3878dec556f741` | |
| `mono_44100_032.mp3` | 4,180 | `67397034e2526bee208c5607e7cd3b7ba36ddc1305086b6690207847882bad4f` | lowest bitrate, 32 kbit/s |
| `mono_44100_032.pmp` | 2,749 | `c4bf12b8120087893030ba23c91f696c7a5722049261ea315333c77753542e8b` | |
| `stereo_32000_128.mp3` | 17,280 | `4b771a6a11cf74f59dd6b16bd6eb79fc640d2a1290209bf2875b486ed41d2f7c` | 32 kHz |
| `stereo_32000_128.pmp` | 7,240 | `a420d9973d79481011ce7ab1fdff86ccb3103e5462ba188b14b29f16b83bbcd6` | |
| `mono_no_reservoir.mp3` | 17,135 | `0e7cf7b940f3d632cc89be9d0252764d32f53c3dfe1a0312c690ecbe773ef87a` | no bit reservoir |
| `mono_no_reservoir.pmp` | 11,927 | `68580de62fa458648ef37c619dc5867707831d0d44fc171d08406e49f2f42daf` | |
| `cwb_headcut.mp3` | 10,868 | `e22633170e1b76ea857543f7013ce0732516c35e902adde7e9bcc32f1ea276f3` | stream cut mid-frame: data before the first frame, and first frames whose reservoir lies outside the file (stored aside) |
| `cwb_headcut.pmp` | 8,064 | `ebc6edd0211525a9b640410b3cbfc8ff54040e259a0017d2c97aabbc2e1073f3` | |
| `cwb_truncated.mp3` | 10,868 | `eb9f722a5b35cad9719a92667ed69d2e10c32220d61d8b15f7fec8889e3fbe04` | incomplete last frame |
| `cwb_truncated.pmp` | 8,321 | `3744421cd5f99644e442545b6cfb7bb616f9585f570ce376b1cbc36fb6216e5b` | |
| `cwb_garbagehead.mp3` | 16,833 | `cd1dc9085465de13bdd711e0ada160a8c2424fb99523a1876012575b872a2098` | non-audio bytes before the first frame |
| `cwb_garbagehead.pmp` | 11,785 | `42924a2b43543c0e7a75f47e5a45a791a96d75520de38f641f615250a5fe217f` | |
| `cwb_transient.mp3` | 18,540 | `b7eb4928d5c8bb64e476e0cfd8adf9b3640b26e47812c0fc92165df75de22492` | short blocks and block switching, stereo |
| `cwb_transient.pmp` | 6,959 | `a2d0c8d78b9624cdb2597e4abfbab4b033880c786868aa37d62cff75dade95a6` | |
