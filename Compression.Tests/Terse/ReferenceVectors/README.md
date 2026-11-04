# IBM TERSE reference files

Files written by IBM `AMATERSE` on z/OS, with the dataset contents downloaded in binary,
taken from the Open Mainframe Project's TerseDecompress test data
(<https://github.com/openmainframeproject/tersedecompress-testdata>, Apache-2.0) at commit
`9a0470b55f7bf28a647ee69a6af4a2e15eda1bc1`.

| File | Origin in that repository |
|------|---------------------------|
| `FB.XARGS.PACK`, `FB.XARGS.SPACK` | `TERSED/` — `xargs.1` (Canterbury corpus) in a RECFM=FB dataset, tersed with PACK / SPACK |
| `VB.XARGS.PACK`, `VB.XARGS.SPACK` | `TERSED/` — the same text in a RECFM=VB dataset |
| `FB.GRAMMAR.LSP.PACK`, `FB.GRAMMAR.LSP.SPACK` | `TERSED/` — `grammar.lsp` (Canterbury corpus), RECFM=FB |
| `*.expected` | `ZOSBINARY/` — the untersed dataset in binary, as the reference decoder's binary mode writes it (VB records carry their 4-byte RDW) |

Only binary-mode output is compared; text mode (EBCDIC to ASCII with line ends) is a
presentation of the same records.
