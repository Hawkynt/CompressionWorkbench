# OneNote packages (`.onepkg`) on disk

A OneNote package is a Microsoft Cabinet (MS-CAB) holding one notebook: its table of contents
(`*.onetoc2`) and its sections (`*.one`). No separate specification exists; PRONOM `fmt/987`
identifies it as `MSCF`, cabinet format 1.3, with a member named `*.onetoc*`. This page records
what Microsoft's own packages contain, measured on the 38 distinct `.onepkg` files Microsoft shipped
in Office 2007 to 2013 installers (sample notebooks `NOTEBOOKnn` and the OneNote guide `ONGUIDE`, in
Czech, English, Lithuanian, Russian and Spanish), and why CompressionWorkbench reads these packages
but does not create them.

## Layout, as measured on all 38 packages

| Field | Value in every package |
| --- | --- |
| Cabinet version | 1.3 |
| Header flags | `0x0004` (reserve present) only: no previous/next cabinet |
| Reserve sizes | 20 header bytes, 0 per folder, 0 per data block |
| Header reserve | `UInt32 0x00100000`, `UInt32` offset of the signature (= `cbCabinet`), `UInt32` signature size, 8 zero bytes |
| After the cabinet | a PKCS #7 SignedData blob (DER, starts `30 82`) that ends the file: an Authenticode signature by Microsoft |
| Compression | every folder LZX with a 2^15 window (`typeCompress 0x0F03`) |
| LZX stream header | x86 call translation announced in every one of the 96 folders |
| Data blocks | 32768 uncompressed bytes each, the last of a folder shorter |
| Folders | 1 to 8; a member never spans two folders (no continuation entries); a folder holds 89 KiB to 3.5 MiB of member data |
| Members | exactly one `.onetoc2`; in 32 packages it is the first member, with attributes `0x21` (read-only, archive); in 25 packages every `.one` section has `0x20` (archive) |
| Member names | UTF-8 bytes, although the `_A_NAME_IS_UTF` attribute is not set |

Every package extracts to exactly the sections 7-Zip extracts from it (SHA-256 of every member equal),
and contains exactly one `.onetoc2`.

## Why creation stays refused

Byte-for-byte reproduction of a Microsoft package from its extracted members is impossible:

1. **The signature.** Each package ends in Microsoft's Authenticode signature over the cabinet, and
   its header reserve points at it. A writer cannot produce that signature without Microsoft's
   private key, and a package without it is no longer the reference file.
2. **The LZX bits.** LZX defines how a stream decodes, not which of the many valid encodings of the
   same data an encoder picks (block boundaries and types, tree shapes, match choices). The compressed
   data blocks are one particular encoder's choices; reproducing them would require that encoder's
   exact decisions, which no specification describes.

A cabinet of the same members that we could write (unsigned, our own LZX or MSZIP encoding) is a valid
CAB that 7-Zip and `expand` extract, but nothing on hand proves that OneNote imports it as a notebook:
OneNote is not available here to act as the oracle. Until it is, `OnePkg` stays read-only.

## The reference packages

They are Microsoft's files and are not committed. `OnePkgMicrosoftPackagesTests` runs against a folder
of them named by `CWB_ONEPKG_DIR`. Sources are paths on discmaster.textfiles.com (`/file/<path>`).

| Package | SHA-256 | Source |
| --- | --- | --- |
| `NOTEBOOK04.ONEPKG_1029` | `161ca996de59886ef37828f3db74e9944094b6e77b6109887f7253e56b2d815e` | `43067/JNP_2008-19_cd.bin/data/programy/MS_Office2007SaD.exe/OnoteLR.cab/NOTEBOOK04.ONEPKG_1029` |
| `NOTEBOOK10.ONEPKG_1049` | `8d55f00ed04a7786e7f0511dd06d6631e7835f6da37d46e3b55d0ff15216dde0` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/NOTEBOOK10.ONEPKG_1049` |
| `NOTEBOOK03.ONEPKG_3082` | `8c14991e36deb6b8d099de0b77d243b03157c0aafc192c13988cb7e37fec3629` | `12642/2015.02.mail.altrad.com.tar/mail.altrad.com/TEST/office 2007 SPANICH/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK03.ONEPKG_3082` |
| `NOTEBOOK07.ONEPKG_3082` | `6cc2c0ba5989ba85269444dda17c802fb843cf31a27c7fc96ad3cb3fc767dd1d` | `12729/186.179.109.206.tar/186.179.109.206/Instaladores/Office2007.rar/Office2007/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK07.ONEPKG_3082` |
| `ONGUIDE.ONEPKG_1033` | `df8eaacdd3b036a64be7a2c23fbdc4cd02f69a8dd95df2ed81b21f469cf84df7` | `43087/JNP_2010-03_DVD.iso/Data/MS Office 2010/X16-19332.exe/OnoteLR.cab/ONGUIDE.ONEPKG_1033` |
| `NOTEBOOK03.ONEPKG_1033` | `ea1c9916a52f54056975228cab2540fff703e9979c56e2d7f64dcf633d8a40ad` | `37246/Issue 239 - January 2008 - DPCS0108DVD.ISO/Microsoft/Microsoft OneNote 2007/X12-30151.exe/OnoteLR.cab/NOTEBOOK03.ONEPKG_1033` |
| `ONGUIDE.ONEPKG_3082` | `5375a3519ded18dfad8291ce06459a8d830250f12ca5ce78bc177e21569a936d` | `12729/186.179.109.206.tar/186.179.109.206/Instaladores/Office2007.rar/Office2007/ONENOTE.ES-ES/ONOTELR.CAB/ONGUIDE.ONEPKG_3082` |
| `NOTEBOOK03.ONEPKG_1029` | `36f1aa1ebbcf336b755902195d3e12f8434b490dcec89b7d0a980ae521150f30` | `43093/JNP_2010-09_DVD.iso/data/MS Office 2010/X16-32004.exe/OnoteLR.cab/NOTEBOOK03.ONEPKG_1029` |
| `NOTEBOOK03.ONEPKG_1049` | `f4b4825efc81b935f1a1e574d2e909af52b41d2c92728a5c436572cc453b0913` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/NOTEBOOK03.ONEPKG_1049` |
| `NOTEBOOK04.ONEPKG_1049` | `01ea5bd2cd3bf9ae44f9c22858666756c72b76ffee5d86f6b6a7e8d01ae08ccf` | `46008/DKDVD-0308.ISO/DVD/MS/office2007sp1-kb936982-fullfile-ru-ru.exe/MAINMUIsp1-ru-ru/PATCH_CAB/NOTEBOOK04.ONEPKG_1049` |
| `ONGUIDE.ONEPKG_1033` | `6b67748d2344bf3e8fd7c0fe57b08c714fed6ce363b12c62b0ad5ffb31ed53ea` | `18096/ftp.tcs3.com.tar/ftp.tcs3.com/DRIVERS/Audio/Office2010/OneNote.en-us/OnoteLR.cab/ONGUIDE.ONEPKG_1033` |
| `NOTEBOOK06.ONEPKG_1049` | `e3b5d072e43691ba1c46b67203525ba3b79cc5a6a75f6d7649b58848756aa201` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/NOTEBOOK06.ONEPKG_1049` |
| `NOTEBOOK04.ONEPKG_3082` | `d3a286f2363aa19dcb3d64e21c0ef77f3cdda9d51d70f6d3c36d3bcad48d3939` | `12642/2015.02.mail.altrad.com.tar/mail.altrad.com/TEST/office 2007 SPANICH/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK04.ONEPKG_3082` |
| `NOTEBOOK10.ONEPKG_3082` | `916d09eca63720a4a06a316e8e4673116f82408c3e40f5be1103b9a29eafee28` | `12729/186.179.109.206.tar/186.179.109.206/Instaladores/Office2007.rar/Office2007/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK10.ONEPKG_3082` |
| `NOTEBOOK03.ONEPKG_1033` | `1da416d7254efaa6a0dad0e04d985f7f64d52c03b4b8c16417b5f6239335b9f6` | `18151/ftp.morenci.k12.mi.us.tar/ftp.morenci.k12.mi.us/Microsoft Office 2013/SW_DVD5_Office_Professional_Plus_2013_W32_English_MLF_X18-55138.ISO/onenote.en-us/onotelr.cab/NOTEBOOK03.ONEPKG_1033` |
| `NOTEBOOK10.ONEPKG_1029` | `bcb5a5bd0a88626f49a9d8da2189a35797a2bd7b90129eebd2d58c0650309542` | `43067/JNP_2008-19_cd.bin/data/programy/MS_Office2007SaD.exe/OnoteLR.cab/NOTEBOOK10.ONEPKG_1029` |
| `NOTEBOOK10.ONEPKG_1049` | `bc3f82d3d25b22d658685e675eddc39c39abd861f264b2149cc5a37f755cb1c3` | `46006/DKDVD-0208.ISO/DVD/MS/office2007sp1-kb936982-fullfile-ru-ru.exe/MAINMUIsp1-ru-ru/PATCH_CAB/NOTEBOOK10.ONEPKG_1049` |
| `NOTEBOOK06.ONEPKG_3082` | `9a29b3e8dbbaf434c3cb178c28e0645edbdfa0d18d6a6156699f2d6325d34de4` | `12729/186.179.109.206.tar/186.179.109.206/Instaladores/Office2007.rar/Office2007/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK06.ONEPKG_3082` |
| `NOTEBOOK03.ONEPKG_1033` | `f7b2654ca34ae58bc9e389bafb6231ee6e3a81d4aaed2f6fcacf3af11ac9ecfd` | `18096/ftp.tcs3.com.tar/ftp.tcs3.com/DRIVERS/Audio/Office2010/OneNote.en-us/OnoteLR.cab/NOTEBOOK03.ONEPKG_1033` |
| `NOTEBOOK10.ONEPKG_1033` | `0f8d2e2aaf52d809d3473dfe6ff2d4ab1b92320d6bd47b938061adff65e749e8` | `37246/Issue 239 - January 2008 - DPCS0108DVD.ISO/Microsoft/Microsoft OneNote 2007/X12-30151.exe/OnoteLR.cab/NOTEBOOK10.ONEPKG_1033` |
| `NOTEBOOK07.ONEPKG_1029` | `9834ba35073b653b2e4f452fbd551385aeb9877e7edde3b55f0bbd86dc5fa7e4` | `43077/JNP_2009-05_cd.bin/data/programy/MS_OfficeSaD_trial60.exe/OnoteLR.cab/NOTEBOOK07.ONEPKG_1029` |
| `NOTEBOOK01.ONEPKG_1029` | `553e45e2faed8b830544ed27de37e0b156fcdb8f8d77364358d86c32a95306ce` | `43067/JNP_2008-19_cd.bin/data/programy/MS_Office2007SaD.exe/OnoteLR.cab/NOTEBOOK01.ONEPKG_1029` |
| `NOTEBOOK03.ONEPKG_1029` | `1187ea887b70f1c91f214e8e34c48194f07f8afc979bc550d6369c143e34f02e` | `43077/JNP_2009-05_cd.bin/data/programy/MS_OfficeSaD_trial60.exe/OnoteLR.cab/NOTEBOOK03.ONEPKG_1029` |
| `NOTEBOOK06.ONEPKG_1049` | `a5a44f2ce1624aedfa38ef58d949e4767dd9944928a6f403173eec7c1ceb3154` | `46006/DKDVD-0208.ISO/DVD/MS/office2007sp1-kb936982-fullfile-ru-ru.exe/MAINMUIsp1-ru-ru/PATCH_CAB/NOTEBOOK06.ONEPKG_1049` |
| `NOTEBOOK01.ONEPKG_3082` | `2f5aa3312613750d21f129061d52fb34fbc0f79f8b747f37bac57936d7099f38` | `12642/2015.02.mail.altrad.com.tar/mail.altrad.com/TEST/office 2007 SPANICH/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK01.ONEPKG_3082` |
| `ONGUIDE.ONEPKG_1029` | `c44e6e0a034b8e7d2306067799a6703567de7874fd1398bce3eea46729e94dd3` | `36707/PCWorld_2008-10_DVD.iso/komercni software/komercni-office/X12-46463.exe/OnoteLR.cab/ONGUIDE.ONEPKG_1029` |
| `NOTEBOOK07.ONEPKG_1049` | `765d33fb8b8801418d15ece7d12977cded354fbd17b48a7324364724bfb5bda7` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/NOTEBOOK07.ONEPKG_1049` |
| `NOTEBOOK11.ONEPKG_1049` | `c05f44daf8df1ea40aebe03b1d33e9127c6198730376ed81b564aadaf082553e` | `46008/DKDVD-0308.ISO/DVD/MS/office2007sp1-kb936982-fullfile-ru-ru.exe/MAINMUIsp1-ru-ru/PATCH_CAB/NOTEBOOK11.ONEPKG_1049` |
| `ONGUIDE.ONEPKG_1063` | `94267f8a0e8657ea658135beb61722348b35e119548e02ae8a92881428aa6cff` | `37298/CA12.iso/Darbas/X16-32021.exe/OnoteLR.cab/ONGUIDE.ONEPKG_1063` |
| `NOTEBOOK05.ONEPKG_1029` | `70fed8b07be072a2d745e960a44961124c45a6e7d7ad1aa8e746168a907aec88` | `43077/JNP_2009-05_cd.bin/data/programy/MS_OfficeSaD_trial60.exe/OnoteLR.cab/NOTEBOOK05.ONEPKG_1029` |
| `NOTEBOOK01.ONEPKG_1049` | `d7c774213774a5987651269d75f5e171fa3f122271bef3d579f74cf7a90dba05` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/NOTEBOOK01.ONEPKG_1049` |
| `NOTEBOOK11.ONEPKG_3082` | `fd280d55c403428b7329d44c2e6368b67ee2eea77e1d9d4fd08d916b274fd810` | `12642/2015.02.mail.altrad.com.tar/mail.altrad.com/TEST/office 2007 SPANICH/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK11.ONEPKG_3082` |
| `ONGUIDE.ONEPKG_1033` | `d6b43c2ab80a0510967859da363a41395b257280ba32622ab17ed9a5f8b69c4c` | `37246/Issue 239 - January 2008 - DPCS0108DVD.ISO/Microsoft/Microsoft OneNote 2007/X12-30151.exe/OnoteLR.cab/ONGUIDE.ONEPKG_1033` |
| `NOTEBOOK06.ONEPKG_1029` | `2eebc69d79395cd4fb2f951bcee0ef396eba0f9544e8d0bf432870ab1c4c62cf` | `43067/JNP_2008-19_cd.bin/data/programy/MS_Office2007SaD.exe/OnoteLR.cab/NOTEBOOK06.ONEPKG_1029` |
| `NOTEBOOK03.ONEPKG_1049` | `4b99b8502fe55b8ffdccf0253273d23d48394a4fa6ab82237ad3b2def025d9df` | `46008/DKDVD-0308.ISO/DVD/MS/office2007sp1-kb936982-fullfile-ru-ru.exe/MAINMUIsp1-ru-ru/PATCH_CAB/NOTEBOOK03.ONEPKG_1049` |
| `NOTEBOOK05.ONEPKG_3082` | `e102555f9dab902b6f2d5fa1276a1a2e64089fce42414251644d97a4567461b2` | `12642/2015.02.mail.altrad.com.tar/mail.altrad.com/TEST/office 2007 SPANICH/ONENOTE.ES-ES/ONOTELR.CAB/NOTEBOOK05.ONEPKG_3082` |
| `NOTEBOOK11.ONEPKG_1033` | `db5e2fae9790b28eb7da6ec402dc728530ade09ed97fad35346f6a094e07b680` | `37246/Issue 239 - January 2008 - DPCS0108DVD.ISO/Microsoft/Microsoft OneNote 2007/X12-30151.exe/OnoteLR.cab/NOTEBOOK11.ONEPKG_1033` |
| `ONGUIDE.ONEPKG_1049` | `ed7f7542b5190957b309fed815bd8b72fc11027d0d98a7627b477c6ad7e5d47b` | `45588/CP113 2007.12.iso/SOFT/MsOfficeOneNote2007/X12-45117.exe/OnoteLR.cab/ONGUIDE.ONEPKG_1049` |
