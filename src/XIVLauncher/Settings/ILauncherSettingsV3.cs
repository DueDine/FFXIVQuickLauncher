using System.Collections.Generic;
using System.IO;
using XIVLauncher.Accounts.Cred;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Xaml;

namespace XIVLauncher.Settings
{
    public interface ILauncherSettingsV3
    {
        #region Launcher Setting

        DirectoryInfo GamePath { get; set; }
        bool AutologinEnabled { get; set; }
        List<AddonEntry> AddonList { get; set; }
        bool UniqueIdCacheEnabled { get; set; }
        string AdditionalLaunchArgs { get; set; }
        bool InGameAddonEnabled { get; set; }
        DalamudLoadMethod? InGameAddonLoadMethod { get; set; }
        bool OtpServerEnabled { get; set; }
        ClientLanguage? Language { get; set; }
        LauncherLanguage? LauncherLanguage { get; set; }
        string CurrentAccountId { get; set; }
        bool? EncryptArguments { get; set; }
        bool? EncryptArgumentsV2 { get; set; }
        DirectoryInfo PatchPath { get; set; }
        bool? AskBeforePatchInstall { get; set; }
        long SpeedLimitBytes { get; set; }
        decimal DalamudInjectionDelayMs { get; set; }
        bool? KeepPatches { get; set; }
        bool? HasComplainedAboutAdmin { get; set; }
        bool? HasComplainedAboutGShadeDxgi { get; set; }
        bool? HasComplainedAboutNoOtp { get; set; }
        string LastVersion { get; set; }
        AcquisitionMethod? PatchAcquisitionMethod { get; set; }
        bool? HasShownAutoLaunchDisclaimer { get; set; }
        string AcceptLanguage { get; set; }
        DpiAwareness? DpiAwareness { get; set; }
        int? VersionUpgradeLevel { get; set; }
        bool? TreatNonZeroExitCodeAsFailure { get; set; }
        bool? ExitLauncherAfterGameExit { get; set; }
        bool? IsFt { get; set; }
        string DalamudRolloutBucket { get; set; }
        bool? AutoStartSteam { get; set; }
        bool? ForceNorthAmerica { get; set; }

        PreserveWindowPosition.WindowPlacement? MainWindowPlacement { get; set; }
        LoginType? SelectedLoginType { get; set; }
        int? SelectedServer { get; set; }
        bool FastLogin { get; set; }
        bool EnableInjector { get; set; }
        bool? EnableBeta { get; set; }
        bool? HasAgreeWeGameUsage { get; set; }
        bool? ShowWeGameTokenLogin { get; set; }
        CredType? CredType { get; set; }
        bool? EnableSkipUpdate { get; set; }
        bool? EnableDebugLog { get; set; }
        #endregion
    }
}
