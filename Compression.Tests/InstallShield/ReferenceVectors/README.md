# InstallShield reference cabinets

`data1.hdr` / `data1.cab` are unshield's own regression case for CVE-2015-1386 (a cabinet whose
directory names climb out of the extraction directory), copied unchanged from
https://github.com/twogood/unshield/tree/main/test/v5/CVE-2015-1386 (MIT licence, Copyright (c) 2003
David Eriksson); `CMakeLists.txt` is unshield's test definition for it, which expects unshield to
refuse the extraction.

The real cabinet sets unshield tests against (`test/v0/*/test_data.zip`, 3.6 to 27 MB of
third-party installers, with unshield's expected MD5 list beside each) are too large to keep here;
`InstallShieldCabTests` checks them when `CWB_INSTALLSHIELD_FIXTURES` points at a directory holding
one sub-directory per set (the unpacked zip plus its `.md5` file).
