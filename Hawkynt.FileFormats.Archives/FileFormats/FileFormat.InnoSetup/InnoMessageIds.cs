namespace FileFormat.InnoSetup;

/// <summary>
/// The message identifiers of Inno Setup 6.0.x (message table "Inno Setup Messages (6.0.0) (u)"),
/// in the order of TSetupMessageID: a message table holds exactly these, in this order. Copied from
/// Inno Setup 6.0.5's MsgIDs.pas (Inno Setup License, see THIRD-PARTY-NOTICE.InnoSetup.txt).
/// </summary>
internal static class InnoMessageIds {
  public static readonly string[] Names = [
    "AbortRetryIgnoreCancel", "AbortRetryIgnoreSelectAction", "AbortRetryIgnoreIgnore", "AbortRetryIgnoreRetry", "AboutSetupMenuItem", "AboutSetupMessage",
    "AboutSetupNote", "AboutSetupTitle", "AdminPrivilegesRequired", "ApplicationsFound", "ApplicationsFound2", "BadDirName32",
    "BadGroupName", "BeveledLabel", "BrowseDialogLabel", "BrowseDialogTitle", "ButtonBack", "ButtonBrowse",
    "ButtonCancel", "ButtonFinish", "ButtonInstall", "ButtonNewFolder", "ButtonNext", "ButtonNo",
    "ButtonNoToAll", "ButtonOK", "ButtonWizardBrowse", "ButtonYes", "ButtonYesToAll", "CannotContinue",
    "CannotInstallToNetworkDrive", "CannotInstallToUNCPath", "ChangeDiskTitle", "ClickFinish", "ClickNext", "CloseApplications",
    "CompactInstallation", "ComponentSize1", "ComponentSize2", "ComponentsDiskSpaceGBLabel", "ComponentsDiskSpaceMBLabel", "ConfirmDeleteSharedFile2",
    "ConfirmDeleteSharedFileTitle", "ConfirmTitle", "ConfirmUninstall", "CustomInstallation", "DirDoesntExist", "DirDoesntExistTitle",
    "DirExists", "DirExistsTitle", "DirNameTooLong", "DiskSpaceGBLabel", "DiskSpaceMBLabel", "DiskSpaceWarning",
    "DiskSpaceWarningTitle", "DontCloseApplications", "ErrorChangingAttr", "ErrorCloseApplications", "ErrorCopying", "ErrorCreatingDir",
    "ErrorCreatingTemp", "ErrorExecutingProgram", "ErrorFunctionFailed", "ErrorFunctionFailedNoCode", "ErrorFunctionFailedWithMessage", "ErrorIniEntry",
    "ErrorInternal2", "ErrorOpeningReadme", "ErrorReadingExistingDest", "ErrorReadingSource", "ErrorRegCreateKey", "ErrorRegisterServer",
    "ErrorRegisterTypeLib", "ErrorRegOpenKey", "ErrorRegSvr32Failed", "ErrorRegWriteKey", "ErrorRenamingTemp", "ErrorReplacingExistingFile",
    "ErrorRestartingComputer", "ErrorRestartReplace", "ErrorTitle", "ErrorTooManyFilesInDir", "ExistingFileNewer", "ExistingFileReadOnly2",
    "ExistingFileReadOnlyRetry", "ExistingFileReadOnlyKeepExisting", "ExitSetupMessage", "ExitSetupTitle", "FileAbortRetryIgnoreSkipNotRecommended", "FileAbortRetryIgnoreIgnoreNotRecommended",
    "FileExists", "FileNotInDir2", "FinishedHeadingLabel", "FinishedLabel", "FinishedLabelNoIcons", "FinishedRestartLabel",
    "FinishedRestartMessage", "FullInstallation", "GroupNameTooLong", "HelpTextNote", "IncorrectPassword", "InfoAfterClickLabel",
    "InfoAfterLabel", "InfoBeforeClickLabel", "InfoBeforeLabel", "InformationTitle", "InstallingLabel", "InvalidDirName",
    "InvalidDrive", "InvalidGroupName", "InvalidParameter", "InvalidPath", "LastErrorMessage", "LdrCannotCreateTemp",
    "LdrCannotExecTemp", "LicenseAccepted", "LicenseLabel", "LicenseLabel3", "LicenseNotAccepted", "MustEnterGroupName",
    "NewFolderName", "NoProgramGroupCheck2", "NoRadio", "NotOnThisPlatform", "NoUninstallWarning", "NoUninstallWarningTitle",
    "OnlyAdminCanUninstall", "OnlyOnTheseArchitectures", "OnlyOnThisPlatform", "PasswordEditLabel", "PasswordLabel1", "PasswordLabel3",
    "PathLabel", "PowerUserPrivilegesRequired", "PrepareToInstallNeedsRestart", "PreparingDesc", "PreviousInstallNotCompleted", "PrivilegesRequiredOverrideTitle",
    "PrivilegesRequiredOverrideInstruction", "PrivilegesRequiredOverrideText1", "PrivilegesRequiredOverrideText2", "PrivilegesRequiredOverrideAllUsers", "PrivilegesRequiredOverrideAllUsersRecommended", "PrivilegesRequiredOverrideCurrentUser",
    "PrivilegesRequiredOverrideCurrentUserRecommended", "ReadyLabel1", "ReadyLabel2a", "ReadyLabel2b", "ReadyMemoComponents", "ReadyMemoDir",
    "ReadyMemoGroup", "ReadyMemoTasks", "ReadyMemoType", "ReadyMemoUserInfo", "RunEntryExec", "RunEntryShellExec",
    "SelectComponentsDesc", "SelectComponentsLabel2", "SelectDirBrowseLabel", "SelectDirDesc", "SelectDirectoryLabel", "SelectDirLabel3",
    "SelectDiskLabel2", "SelectLanguageLabel", "SelectLanguageTitle", "SelectStartMenuFolderBrowseLabel", "SelectStartMenuFolderDesc", "SelectStartMenuFolderLabel3",
    "SelectTasksDesc", "SelectTasksLabel2", "SetupAborted", "SetupAlreadyRunning", "SetupAppRunningError", "SetupAppTitle",
    "SetupFileCorrupt", "SetupFileCorruptOrWrongVer", "SetupFileMissing", "SetupLdrStartupMessage", "SetupWindowTitle", "SharedFileNameLabel",
    "SharedFileLocationLabel", "ShowReadmeCheck", "ShutdownBlockReasonInstallingApp", "ShutdownBlockReasonUninstallingApp", "SourceDoesntExist", "SourceIsCorrupted",
    "StatusClosingApplications", "StatusCreateDirs", "StatusCreateIcons", "StatusCreateIniEntries", "StatusCreateRegistryEntries", "StatusExtractFiles",
    "StatusRegisterFiles", "StatusRestartingApplications", "StatusRollback", "StatusSavingUninstall", "StatusRunProgram", "StatusUninstalling",
    "TranslatorNote", "UninstallAppFullTitle", "UninstallAppTitle", "UninstallDataCorrupted", "UninstallDisplayNameMark", "UninstallDisplayNameMarks",
    "UninstallDisplayNameMark32Bit", "UninstallDisplayNameMark64Bit", "UninstallDisplayNameMarkAllUsers", "UninstallDisplayNameMarkCurrentUser", "UninstalledAll", "UninstalledAndNeedsRestart",
    "UninstalledMost", "UninstallAppRunningError", "UninstallNotFound", "UninstallOnlyOnWin64", "UninstallOpenError", "UninstallStatusLabel",
    "UninstallUnknownEntry", "UninstallUnsupportedVer", "UserInfoDesc", "UserInfoName", "UserInfoNameRequired", "UserInfoOrg",
    "UserInfoSerial", "WelcomeLabel1", "WelcomeLabel2", "WindowsServicePackRequired", "WindowsVersionNotSupported", "WinVersionTooHighError",
    "WinVersionTooLowError", "WizardInfoAfter", "WizardInfoBefore", "WizardInstalling", "WizardLicense", "WizardPassword",
    "WizardPreparing", "WizardReady", "WizardSelectDir", "WizardSelectComponents", "WizardSelectProgramGroup", "WizardSelectTasks",
    "WizardUninstalling", "WizardUserInfo", "YesRadio",
  ];
}
