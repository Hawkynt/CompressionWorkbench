[Setup]
AppName=CWB Test
AppVersion=1.0
DefaultDirName={autopf}\CWB Test
Uninstallable=no
PrivilegesRequired=lowest
Compression=none
TimeStampsInUTC=yes
WizardImageFile=compiler:@IMG@
WizardSmallImageFile=compiler:@SMALL@
OutputDir=out
OutputBaseFilename=ref

[Files]
Source: "in\hello.txt"; DestDir: "{app}"
Source: "in\sub\data.bin"; DestDir: "{app}\sub"
