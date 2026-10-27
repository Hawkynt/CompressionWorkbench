# Third-party notices

This distribution contains, in binary form, third-party code under two
arrangements: sources vendored verbatim under `Hawkynt.FileFormats.Audio/Codecs/Codec.*/Vendored/`, and
ports written from third-party sources that carry their own notice. Both sets
have licences requiring the notice to travel with the binaries, so every one is
reproduced in full below.

For the vendored set, the pinned upstream revisions and the one local
modification made to keep the copies fully managed are recorded in
[`Hawkynt.FileFormats.Audio/Codecs/VENDORED_AUDIO_CODECS.md`](https://github.com/Hawkynt/CompressionWorkbench/blob/main/Hawkynt.FileFormats.Audio/Codecs/VENDORED_AUDIO_CODECS.md).

This file ships inside the `Hawkynt.FileFormats.Audio` package, so links out of
it are absolute.

## Concentus (Opus)

Source: `lostromb/concentus` — BSD-3-Clause

```
Copyright (c) by various holding parties, including (but not limited to): 
Skype Limited, Xiph.Org Foundation, CSIRO, Microsoft Corporation,
Jean-Marc Valin, Gregory Maxwell, Mark Borgerding, Timothy B. Terriberry,
Logan Stromberg. All rights are reserved by their respective holders.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of Internet Society, IETF or IETF Trust, nor the
   names of specific contributors, may be used to endorse or promote
   products derived from this software without specific prior written
   permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.


This repository and its redistributable packages contain independently compiled
versions of the Opus C reference library, which is maintained by Xiph.org and the
Opus open-source contributors. The source code for these libraries is freely available
at https://gitlab.xiph.org/xiph/opus/-/tags/v1.5.2, and all binaries are being
redistributed to you under the same terms of the general Opus license dictated above.
```

## .NET Ogg Vorbis Encoder

Source: `SteveLillis/.NET-Ogg-Vorbis-Encoder` — MIT

```
MIT License

Copyright (c) 2016 Steve Lillis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## GroovyCodecs (MP3)

Source: `jongoochgithub/GroovyCodecs` — LGPL-3.0

```
                   GNU LESSER GENERAL PUBLIC LICENSE
                       Version 3, 29 June 2007

 Copyright (C) 2007 Free Software Foundation, Inc. <http://fsf.org/>
 Everyone is permitted to copy and distribute verbatim copies
 of this license document, but changing it is not allowed.


  This version of the GNU Lesser General Public License incorporates
the terms and conditions of version 3 of the GNU General Public
License, supplemented by the additional permissions listed below.

  0. Additional Definitions.

  As used herein, "this License" refers to version 3 of the GNU Lesser
General Public License, and the "GNU GPL" refers to version 3 of the GNU
General Public License.

  "The Library" refers to a covered work governed by this License,
other than an Application or a Combined Work as defined below.

  An "Application" is any work that makes use of an interface provided
by the Library, but which is not otherwise based on the Library.
Defining a subclass of a class defined by the Library is deemed a mode
of using an interface provided by the Library.

  A "Combined Work" is a work produced by combining or linking an
Application with the Library.  The particular version of the Library
with which the Combined Work was made is also called the "Linked
Version".

  The "Minimal Corresponding Source" for a Combined Work means the
Corresponding Source for the Combined Work, excluding any source code
for portions of the Combined Work that, considered in isolation, are
based on the Application, and not on the Linked Version.

  The "Corresponding Application Code" for a Combined Work means the
object code and/or source code for the Application, including any data
and utility programs needed for reproducing the Combined Work from the
Application, but excluding the System Libraries of the Combined Work.

  1. Exception to Section 3 of the GNU GPL.

  You may convey a covered work under sections 3 and 4 of this License
without being bound by section 3 of the GNU GPL.

  2. Conveying Modified Versions.

  If you modify a copy of the Library, and, in your modifications, a
facility refers to a function or data to be supplied by an Application
that uses the facility (other than as an argument passed when the
facility is invoked), then you may convey a copy of the modified
version:

   a) under this License, provided that you make a good faith effort to
   ensure that, in the event an Application does not supply the
   function or data, the facility still operates, and performs
   whatever part of its purpose remains meaningful, or

   b) under the GNU GPL, with none of the additional permissions of
   this License applicable to that copy.

  3. Object Code Incorporating Material from Library Header Files.

  The object code form of an Application may incorporate material from
a header file that is part of the Library.  You may convey such object
code under terms of your choice, provided that, if the incorporated
material is not limited to numerical parameters, data structure
layouts and accessors, or small macros, inline functions and templates
(ten or fewer lines in length), you do both of the following:

   a) Give prominent notice with each copy of the object code that the
   Library is used in it and that the Library and its use are
   covered by this License.

   b) Accompany the object code with a copy of the GNU GPL and this license
   document.

  4. Combined Works.

  You may convey a Combined Work under terms of your choice that,
taken together, effectively do not restrict modification of the
portions of the Library contained in the Combined Work and reverse
engineering for debugging such modifications, if you also do each of
the following:

   a) Give prominent notice with each copy of the Combined Work that
   the Library is used in it and that the Library and its use are
   covered by this License.

   b) Accompany the Combined Work with a copy of the GNU GPL and this license
   document.

   c) For a Combined Work that displays copyright notices during
   execution, include the copyright notice for the Library among
   these notices, as well as a reference directing the user to the
   copies of the GNU GPL and this license document.

   d) Do one of the following:

       0) Convey the Minimal Corresponding Source under the terms of this
       License, and the Corresponding Application Code in a form
       suitable for, and under terms that permit, the user to
       recombine or relink the Application with a modified version of
       the Linked Version to produce a modified Combined Work, in the
       manner specified by section 6 of the GNU GPL for conveying
       Corresponding Source.

       1) Use a suitable shared library mechanism for linking with the
       Library.  A suitable mechanism is one that (a) uses at run time
       a copy of the Library already present on the user's computer
       system, and (b) will operate properly with a modified version
       of the Library that is interface-compatible with the Linked
       Version.

   e) Provide Installation Information, but only if you would otherwise
   be required to provide such information under section 6 of the
   GNU GPL, and only to the extent that such information is
   necessary to install and execute a modified version of the
   Combined Work produced by recombining or relinking the
   Application with a modified version of the Linked Version. (If
   you use option 4d0, the Installation Information must accompany
   the Minimal Corresponding Source and Corresponding Application
   Code. If you use option 4d1, you must provide the Installation
   Information in the manner specified by section 6 of the GNU GPL
   for conveying Corresponding Source.)

  5. Combined Libraries.

  You may place library facilities that are a work based on the
Library side by side in a single library together with other library
facilities that are not Applications and are not covered by this
License, and convey such a combined library under terms of your
choice, if you do both of the following:

   a) Accompany the combined library with a copy of the same work based
   on the Library, uncombined with any other library facilities,
   conveyed under the terms of this License.

   b) Give prominent notice with the combined library that part of it
   is a work based on the Library, and explaining where to find the
   accompanying uncombined form of the same work.

  6. Revised Versions of the GNU Lesser General Public License.

  The Free Software Foundation may publish revised and/or new versions
of the GNU Lesser General Public License from time to time. Such new
versions will be similar in spirit to the present version, but may
differ in detail to address new problems or concerns.

  Each version is given a distinguishing version number. If the
Library as you received it specifies that a certain numbered version
of the GNU Lesser General Public License "or any later version"
applies to it, you have the option of following the terms and
conditions either of that published version or of any later version
published by the Free Software Foundation. If the Library as you
received it does not specify a version number of the GNU Lesser
General Public License, you may choose any version of the GNU Lesser
General Public License ever published by the Free Software Foundation.

  If the Library as you received it specifies that a proxy can decide
whether future versions of the GNU Lesser General Public License shall
apply, that proxy's public statement of acceptance of any version is
permanent authorization for you to choose that version for the
Library.
```

## AmrWbLib (AMR-WB)

Source: `PhrSite/SipLib` — BSD-3-Clause

```
Copyright (c) 2024 Philip Reichl

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS “AS IS” AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Derivative Works:

Portions of the code implemented in the Core, Channels, Dtls and Video directories was derived from code in the [SipSorcery](https://github.com/sipsorcery-org/sipsorcery) project. The SipSorcery project is licensed under a BSD-3-Clause licence (the same as with this project).
```

## Nellymoser "Asao" decoder

`Hawkynt.FileFormats.Audio/Codecs/Codec.Nellymoser/` is a port of FFmpeg's `libavcodec/nellymoserdec.c` and the
shared tables and bit allocator in `libavcodec/nellymoser.c`. Those files are MIT
licensed, and the notice travels with the port:

```
NellyMoser audio decoder
Copyright (c) 2007 a840bda5870ba11f19698ff6eb9581dfb0f95fa5,
                   539459aeb7d425140b62a3ec7dbf6dc8e408a306, and
                   520e17cd55896441042b14df2566a6eb610ed444
Copyright (c) 2007 Loic Minier <lool at dooz.org>
                   Benjamin Larsson

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

## Apple Lossless (ALAC)

`Hawkynt.FileFormats.Audio/Codecs/Codec.Alac/` is a port of Apple's open-sourced ALAC reference: the frame
parser in `ALACDecoder.cpp`/`ALACEncoder.cpp`, the dynamic predictor in
`dp_dec.c`/`dp_enc.c`, the adaptive Golomb coder in `ag_dec.c`/`ag_enc.c` and the
stereo decorrelation in `matrix_dec.c`/`matrix_enc.c`. The reference is C++ and so
cannot be taken in whole as a fully managed copy. It is Apache-2.0, and the notice
travels with the port:

```
Copyright (c) 2011 Apple Inc. All rights reserved.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

## FFmpeg (LGPL-2.1-or-later decoders, encoders and demuxer helpers)

Source: `FFmpeg/FFmpeg` — LGPL-2.1-or-later. Each file's licence header was checked at tag
`n7.1` (`b08d7969c550a804a59511c7b83f2dd8cc0499b8`); none of the files below is one of the
GPL-only parts FFmpeg's `LICENSE.md` lists. The revisions the individual conversions were
made from were not recorded.

The codecs below are conversions of the named FFmpeg files into C#. FFmpeg licenses them under
the GNU LGPL version 2.1 "or (at your option) any later version"; this package distributes the
converted code under the GNU LGPL version 3 or later, as that clause allows. The LGPL-3.0 text
is reproduced under GroovyCodecs above. The converted files are modified: they are C#
rewrites, restructured for this package.

| Codec folder | FFmpeg sources | Copyright holders named in those files |
|---|---|---|
| `Codec.Aac` (SBR) | `libavcodec/aacsbr.c`, `aacsbr_template.c`, `aacsbrdata.h` | Robert Swain; Alex Converse |
| `Codec.Ac3` | `libavcodec/ac3dec.c`, `ac3dec_data.c`, `ac3tab.c`, `ac3.c`, `ac3_parser.c`, `eac3dec.c`, `eac3_data.c`, `ac3enc.c` | Fabrice Bellard; Michael Niedermayer; Kartikey Mahendra Bhatt; Bartlomiej Wolowiec; Justin Ruggles; Prakash Punnoor |
| `Codec.AdpcmX`, `Codec.SolDpcm`, the Creative ADPCM path of `FileFormat.Voc` | `libavcodec/adpcm.c`, `adpcm_data.c`, `dpcm.c` | The FFmpeg project |
| `Codec.AmrNb` | `libavcodec/amrnbdec.c`, `amrnbdata.h`, `acelp_vectors.c`, `acelp_pitch_delay.c`, `acelp_filters.c`, `celp_filters.c`, `celp_math.c`, `lsp.c` | Robert Swain; Colin McQuillan; Vladimir Voroshilov; Reynaldo H. Verdejo Pinochet |
| `Codec.AmrWb` (decoder) | `libavcodec/amrwbdec.c`, `amrwbdata.h`, the ACELP helpers above, `libavutil/lfg.c` | Marcelo Galvao Povoa; Michael Niedermayer; Vladimir Voroshilov |
| `Codec.Atrac1` | `libavcodec/atrac1.c`, `atrac1data.h`, `atrac.c`, `sinewin.c` | Maxim Poliakovski; Benjamin Larsson |
| `Codec.Atrac3` | `libavcodec/atrac3.c`, `atrac3data.h`, `atrac.c`; `libavformat/omadec.c` | Maxim Poliakovski; Benjamin Larsson |
| `Codec.BinkAudio` | `libavcodec/binkaudio.c`, `wma_freqs.c` | Peter Ross; Daniel Verkamp |
| `Codec.Bonk` | `libavcodec/bonk.c`, `libavformat/bonk.c` | Paul B Mahol (`libavformat/bonk.c`; the decoder file names no holder) |
| `Codec.Cook` | `libavcodec/cook.c`, `cookdata.h`, `libavutil/lfg.c`, `libavformat/rmdec.c` | Sascha Sommer; Benjamin Larsson; Michael Niedermayer; Fabrice Bellard |
| `Codec.CriHca` | `libavcodec/hcadec.c`, `hca_data.h` | no copyright line in the files (FFmpeg contributors) |
| `Codec.Dfpwm` | `libavcodec/dfpwmdec.c`, `dfpwmenc.c` | Jack Bruienne; Ben "GreaseMonkey" Russell |
| `Codec.Dts` | `libavcodec/dca_core.c`, `dcadec.c`, `dcadata.c`, `dcahuff.c`, `dcadsp.c`, `dca.c`, `synth_filter.c`, `dcaenc.c` | Gildas Bazin; Benjamin Zores; Benjamin Larsson; Konstantin Shishkov; foo86; Michael Niedermayer; Alexander E. Patrakov |
| `Codec.G7231` | `libavcodec/g723_1dec.c`, `g723_1.c`, `g723_1.h`, `celp_math.c`, `mathtables.c` | Benjamin Larsson; Mohamed Naufal Basheer; Michael Niedermayer |
| `Codec.Mace` | `libavcodec/mace.c` | Laszlo Torok |
| `Codec.MonkeysAudio` (alongside the Monkey's Audio SDK below) | `libavcodec/apedec.c` | Benjamin Zores |
| `Codec.Musepack` | `libavcodec/mpc7.c`, `mpc8.c`, `mpc.c`, `mpc7data.h`, `mpc8data.h`, `mpc8huff.h`, `mpcdata.h`, `mpegaudiodsp_data.c`, `mpegaudiodsp_template.c`, `dct32_template.c`, `libavutil/lfg.c`, `libavformat/mpc.c`, `mpc8.c` | Konstantin Shishkov; Fabrice Bellard; Michael Niedermayer |
| `Codec.Nellymoser` (the generator only; the codec is MIT, above) | `libavutil/lfg.c` | Michael Niedermayer |
| `Codec.Qoa` (alongside `qoa.h` below) | `libavcodec/qoadec.c` | no copyright line in the file (FFmpeg contributors) |
| `Codec.Ra144` | `libavcodec/ra144.c`, `ra144dec.c`, `celp_filters.c`, `mathtables.c` | The FFmpeg project; Vitor Sessak; Nick Kurshev |
| `Codec.Ra288` | `libavcodec/ra288.c`, `ra288.h` | The FFmpeg project |
| `Codec.Ralf` | `libavcodec/ralf.c`, `ralfdata.h` | Konstantin Shishkov |
| `Codec.S302M` | `libavcodec/s302m.c` | Laurent Aimar; Baptiste Coudurier |
| `Codec.Sbc` | `libavcodec/sbcdec.c`, `sbc.c`, `sbcdec_data.c` | Aurelien Jacobs; Intel Corporation; Nokia Corporation; Marcel Holtmann; Henryk Ploetz |
| `Codec.Sipr` | `libavcodec/sipr.c`, `siprdata.h`, the ACELP/CELP/LSP helpers above, `libavformat/rmsipr.c` | Vladimir Voroshilov; Vitor Sessak |
| `Codec.Siren` | `libavcodec/siren.c` | Youness Alaoui; Paul B Mahol; Lynne |
| `Codec.SmackerAudio` | `libavcodec/smacker.c`, `libavformat/smacker.c` | Konstantin Shishkov |
| `Codec.Speex` (alongside the Xiph BSD notice below) | `libavcodec/speexdec.c`, `speexdata.h` | see the Speex section below |
| `Codec.TrueSpeech` | `libavcodec/truespeech.c`, `truespeech_data.h` | Konstantin Shishkov |
| `Codec.Tta` | `libavcodec/tta.c`, `ttadsp.c`, `ttadata.c` | Alex Beregszaszi |
| `Codec.WavArc` | `libavcodec/wavarc.c`, `libavformat/wavarc.c` | Paul B Mahol |
| `Codec.Wma` | `libavcodec/wma.c`, `wmadec.c`, `wmadata.h`, `wma_freqs.c`, `aactab.c` | The FFmpeg project; Oded Shimon; Maxim Gavrilov |
| `Codec.WmaLossless` | `libavcodec/wmalosslessdec.c`, `lossless_audiodsp.c` | Baptiste Coudurier, Benjamin Larsson, Ulion; Sascha Sommer; Andreas Öman; Mashiat Sarker Shakkhar; Benjamin Zores |
| `Codec.WmaPro`, `Codec.Xma` | `libavcodec/wmaprodec.c`, `wmaprodata.h`, `wma.c`, `wma_common.c` | Baptiste Coudurier, Benjamin Larsson, Ulion; Sascha Sommer |

The header every one of these files carries:

```
This file is part of FFmpeg.

FFmpeg is free software; you can redistribute it and/or
modify it under the terms of the GNU Lesser General Public
License as published by the Free Software Foundation; either
version 2.1 of the License, or (at your option) any later version.

FFmpeg is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
Lesser General Public License for more details.
```

## Interplay ACM decoder

`Codec.InterplayAcm` is a conversion of FFmpeg's `libavcodec/interplayacm.c`, which is under the
ISC licence rather than the LGPL:

```
Copyright (c) 2004-2008 Marko Kreen
Copyright (c) 2008 Adam Gashlin
Copyright (c) 2015 Paul B Mahol

Permission to use, copy, modify, and distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.
```

## Speex

`Codec.Speex` is a conversion of FFmpeg's native Speex decoder (`libavcodec/speexdec.c`,
`speexdata.h`). Those files carry the Xiph BSD notice of the libspeex code they derive from in
addition to FFmpeg's LGPL-2.1-or-later header (see the FFmpeg section):

```
Copyright 2002-2008  Xiph.org Foundation
Copyright 2002-2008  Jean-Marc Valin
Copyright 2005-2007  Analog Devices Inc.
Copyright 2005-2008  Commonwealth Scientific and Industrial Research Organisation (CSIRO)
Copyright 1993, 2002, 2006 David Rowe
Copyright 2003       EpicGames
Copyright 1992-1994  Jutta Degener, Carsten Bormann

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of the Xiph.org Foundation nor the names of its
contributors may be used to endorse or promote products derived from
this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION OR
CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## Monkey's Audio SDK

`Codec.MonkeysAudio` converts the reference SDK's range coder (`CUnBitArray`, `CBitArray`), the
entropy stage, the predictors (`CPredictorDecompress3950toCurrent`, `CPredictorCompressNormal`),
the `CNNFilter` cascade and `CPrepare`. The SDK is BSD-3-Clause (licence text from
monkeysaudio.com, checked 2026-10-19):

```
Monkey's Audio License Agreement (3-Clause BSD)
Copyright 2000-2026 by Matthew T. Ashland. All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted
provided that the following conditions are met:

Redistributions of source code must retain the above copyright notice, this list of conditions
and the following disclaimer.
Redistributions in binary form must reproduce the above copyright notice, this list of
conditions and the following disclaimer in the documentation and/or other materials provided
with the distribution.
Neither the name of the copyright holder nor the names of its contributors may be used to
endorse or promote products derived from this software without specific prior written
permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA,
OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF
THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## WavPack

`Codec.WavPack` converts the reference library's decorrelation passes (`unpack.c`, `pack.c`), its
entropy word coder (`words.c`), its bit reader (`bits.c`) and its floating-point support. Source:
`dbry/WavPack` — BSD-3-Clause (checked at `cb3b7fff5f566320ab2150d02a5a388c1829ecd9`):

```
                  Copyright (c) 1998 - 2025 David Bryant
                          All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice,
      this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * Neither the name of the copyright holder nor the names of its
      contributors may be used to endorse or promote products derived from
      this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE REGENTS OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## QOA (Quite OK Audio)

`Codec.Qoa` takes its tables, predictor and bit packing from the reference `qoa.h`. Source:
`phoboslab/qoa` — MIT (checked at `bc0589710ff6aa2f50e08fb4d0a815d567bc0f3e`):

```
Copyright (c) 2023, Dominic Szablewski - https://phoboslab.org
SPDX-License-Identifier: MIT

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## VGAudio (CRI HCA inverse MDCT)

`Codec.CriHca/HcaImdct.cs` converts VGAudio's `Mdct.RunImdct`. Source: `Thealexbarney/VGAudio` —
MIT (checked at `9d8f6ea04c83cccccb3dd7851a631bbd53a8dbbe`; the upstream licence file leaves the
holder's name blank, the project's author is Alex Barney):

```
The MIT License (MIT)

Copyright (c) 2016

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## libopus range decoder

`Codec.Opus/OpusRangeDecoder.cs` converts libopus's `celt/entdec.c` and `celt/entcode.c`.
Source: `xiph/opus` — the same BSD-3-Clause licence reproduced under Concentus above (checked at
`51bd2e649f4516d103ab9a7f22a768595621e847`), whose copyright line reads:

```
Copyright 2001-2023 Xiph.Org, Skype Limited, Octasic,
                    Jean-Marc Valin, Timothy B. Terriberry,
                    CSIRO, Gregory Maxwell, Mark Borgerding,
                    Erik de Castro Lopo, Mozilla, Amazon
```

## Nuked-OPL3

`Codec.Opl` takes its operator, envelope, phase, waveform and rhythm logic and its envelope tables
from Nuked-OPL3. Source: `nukeykt/Nuked-OPL3` — LGPL-2.1-or-later (checked at
`765ec962e473aeb767e4cba74ffdc8f588ffbfe8`, version 1.8); distributed here under the LGPL-3.0
reproduced under GroovyCodecs above, as the "any later version" clause allows:

```
Nuked OPL3
Copyright (C) 2013-2020 Nuke.YKT

This file is part of Nuked OPL3.

Nuked OPL3 is free software: you can redistribute it and/or modify
it under the terms of the GNU Lesser General Public License as
published by the Free Software Foundation, either version 2.1
of the License, or (at your option) any later version.

Nuked OPL3 is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU Lesser General Public License for more details.
```

## emu2413

`Codec.Ym2413` transcribes the instrument patch ROM, the rhythm generation, the KSL/multiple
tables and the envelope behaviour from emu2413 v1.5.9. Source: `digital-sound-antiques/emu2413` —
MIT:

```
The MIT License (MIT)

Copyright (c) 2001-2019 Mitsutaka Okazaki

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## CCITT / Sun Microsystems G.721 / G.723 / G.726 reference

`Codec.G72x` converts the Sun Microsystems reference implementation (`g72x.c`, `g721.c`,
`g723_16.c`, `g723_24.c`, `g723_40.c`), which carries this notice:

```
This source code is a product of Sun Microsystems, Inc. and is provided
for unrestricted use.  Users may copy or modify this source code without
charge.

SUN SOURCE CODE IS PROVIDED AS IS WITH NO WARRANTIES OF ANY KIND INCLUDING
THE WARRANTIES OF DESIGN, MERCHANTIBILITY AND FITNESS FOR A PARTICULAR
PURPOSE, OR ARISING FROM A COURSE OF DEALING, USAGE OR TRADE PRACTICE.

Sun source code is provided with no support and without any obligation on
the part of Sun Microsystems, Inc. to assist in its use, correction,
modification or enhancement.

SUN MICROSYSTEMS, INC. SHALL HAVE NO LIABILITY WITH RESPECT TO THE
INFRINGEMENT OF COPYRIGHTS, TRADE SECRETS OR ANY PATENTS BY THIS SOFTWARE
OR ANY PART THEREOF.

In no event will Sun Microsystems, Inc. be liable for any lost revenue
or profits or other special, indirect and consequential damages, even if
Sun has been advised of the possibility of such damages.

Sun Microsystems, Inc.
2550 Garcia Avenue
Mountain View, California  94043
```

## GSM 06.10 reference implementation

`Codec.Gsm610` is converted from the Jutta Degener / Carsten Bormann reference, whose licence
requires that this notice is kept (it also heads `Gsm610Codec.cs`):

```
Copyright 1992, 1993, 1994 by Jutta Degener and Carsten Bormann,
Technische Universitaet Berlin

Any use of this software is permitted provided that this notice is not
removed and that neither the authors nor the Technische Universitaet Berlin
are deemed to have made any representations as to the suitability of this
software for any purpose nor are held responsible for any defects of
this software.  THERE IS ABSOLUTELY NO WARRANTY FOR THIS SOFTWARE.

As a matter of courtesy, the authors request to be informed about uses
this software has found, about bugs in this software, and about any
improvements that may be of general interest.

Berlin, 28.11.1994
Jutta Degener
Carsten Bormann
```
