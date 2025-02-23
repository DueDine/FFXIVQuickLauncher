using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using CheapLoc;
using Newtonsoft.Json;
using Serilog;
using Velopack;
using Velopack.Sources;
using XIVLauncher.Common.Util;
using XIVLauncher.Windows;

#nullable enable

namespace XIVLauncher
{
    internal class Updates
    {
        public event Action<bool>? OnUpdateCheckFinished;
        private const string UPDATE_URL = "https://github.com/AtmoOmen/FFXIVQuickLauncher";

#if DEV_SERVER
        private const string LEASE_META_URL = "http://localhost:5025/Launcher/GetLease";
        private const string LEASE_FILE_URL = "http://localhost:5025/Launcher/GetFile";
#else
        private const string LEASE_META_URL = "https://aonyx.ffxiv.wang/Launcher/GetLease";
        private const string LEASE_FILE_URL = "https://aonyx.ffxiv.wang/Launcher/GetFile";
#endif

        private const string TRACK_RELEASE = "Release";
        private const string TRACK_PRERELEASE = "Prerelease";

        public static Lease? UpdateLease { get; private set; }

        [Flags]
        public enum LeaseFeatureFlags
        {
            None = 0,
            GlobalDisableDalamud = 1,
            ForceProxyDalamudAndAssets = 1 << 1,
        }

#pragma warning disable CS8618
        public class Lease
        {
            public bool Success { get; set; }
            public string? Message { get; set; }
            public string? CutOffBootver { get; set; }
            public string FrontierUrl { get; set; }
            public LeaseFeatureFlags Flags { get; set; }

            public string ReleasesList { get; set; }

            public DateTime? ValidUntil { get; set; }
        }
#pragma warning restore CS8618

        public static bool HaveFeatureFlag(LeaseFeatureFlags flag)
        {
            return UpdateLease != null && UpdateLease.Flags.HasFlag(flag);
        }

        public async Task Run(bool downloadPrerelease, ChangelogWindow? changelogWindow)
        {
#if RELEASENOUPDATE
            OnUpdateCheckFinished?.Invoke(true);
            return;
#endif
            // GitHub requires TLS 1.2, we need to hardcode this for Windows 7
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            try
            {
                var updateOptions = new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = true };
                var updateSource = new GithubSource(UPDATE_URL, null, true);
                var mgr = new UpdateManager(updateSource, updateOptions);

                // check for new version
                var newRelease = await mgr.CheckForUpdatesAsync();

                if (newRelease != null)
                {
                    var changelog = newRelease.TargetFullRelease.NotesMarkdown;
                    // download new version
                    await mgr.DownloadUpdatesAsync(newRelease);

                    if (changelogWindow == null)
                    {
                        Log.Error("changelogWindow was null");
                        mgr.ApplyUpdatesAndRestart(newRelease);
                        return;
                    }

                    try
                    {
                        changelogWindow.Dispatcher.Invoke(() =>
                        {
                            changelogWindow.UpdateVersion(newRelease.TargetFullRelease.Version.ToString());
                            changelogWindow.ChangeLogText.Text = changelog;
                            changelogWindow.Show();
                            changelogWindow.Closed += (_, _) =>
                            {
                                // install new version and restart app
                                mgr.ApplyUpdatesAndRestart(newRelease);
                            };
                        });

                        OnUpdateCheckFinished?.Invoke(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not show changelog window");
                    }
                }
                else
                {
                    this.OnUpdateCheckFinished?.Invoke(true);
                }

                #region OldSquirrel

                //                 var updateResult = await LeaseUpdateManager(downloadPrerelease).ConfigureAwait(false);
                //                 UpdateLease = updateResult.Lease;
                //
                //                 // Log feature flags
                //                 try
                //                 {
                //                     var flags = string.Join(", ", Enum.GetValues(typeof(LeaseFeatureFlags))
                //                                                       .Cast<LeaseFeatureFlags>()
                //                                                       .Where(f => UpdateLease.Flags.HasFlag(f))
                //                                                       .Select(f => f.ToString()));
                //
                //                     Log.Information("Feature flags: {Flags}", flags);
                //                 }
                //                 catch (Exception ex)
                //                 {
                //                     Log.Error(ex, "Could not log feature flags");
                //                 }
                //
                //                 using var updateManager = updateResult.Manager;
                //
                //                 // TODO: is this allowed?
                //                 SquirrelAwareApp.HandleEvents(
                //                     onInitialInstall: v => updateManager.CreateShortcutForThisExe(),
                //                     onAppUpdate: v => updateManager.CreateShortcutForThisExe(),
                //                     onAppUninstall: v =>
                //                     {
                //                         updateManager.RemoveShortcutForThisExe();
                //
                //                         if (CustomMessageBox.Show(Loc.Localize("UninstallQuestion", "Sorry to see you go!\nDo you want to delete all of your saved settings, plugins and passwords?"), "XIVLauncher",
                //                                 MessageBoxButton.YesNo, MessageBoxImage.Question, false, false)
                //                             == MessageBoxResult.Yes)
                //                         {
                //                             try
                //                             {
                //                                 var mgr = new AccountManager(App.Settings);
                //
                //                                 foreach (var account in mgr.Accounts.ToArray())
                //                                 {
                //                                     account.Password = null;
                //                                     mgr.RemoveAccount(account);
                //                                 }
                //                             }
                //                             catch (Exception ex)
                //                             {
                //                                 Log.Error(ex, "Uninstall: Could not delete passwords");
                //                             }
                //
                //                             try
                //                             {
                //                                 // Let's just give this a shot, probably not going to work 100% but
                //                                 // there's not super much we can do about it right now
                //                                 Directory.Delete(Paths.RoamingPath, true);
                //                             }
                //                             catch (Exception ex)
                //                             {
                //                                 Log.Error(ex, "Uninstall: Could not delete roaming directory");
                //                             }
                //                         }
                //                     });
                //
                //                 await updateManager.CheckForUpdate().ConfigureAwait(false);
                //                 ReleaseEntry? newRelease = await updateManager.UpdateApp().ConfigureAwait(false);
                //
                //                 if (newRelease != null)
                //                 {
                //                     try
                //                     {
                //                         // Reset UID cache after updating
                //                         App.UniqueIdCache.Reset();
                //                     }
                //                     catch
                //                     {
                //                         // ignored
                //                     }
                //
                //                     if (changelogWindow == null)
                //                     {
                //                         Log.Error("changelogWindow was null");
                //                         UpdateManager.RestartApp();
                //                         return;
                //                     }
                //
                //                     try
                //                     {
                //                         changelogWindow.Dispatcher.Invoke(() =>
                //                         {
                //                             changelogWindow.UpdateVersion(newRelease.Version.ToString());
                //                             changelogWindow.Show();
                //                             changelogWindow.Closed += (_, _) =>
                //                             {
                //                                 UpdateManager.RestartApp();
                //                             };
                //                         });
                //
                //                         OnUpdateCheckFinished?.Invoke(false);
                //                     }
                //                     catch (Exception ex)
                //                     {
                //                         Log.Error(ex, "Could not show changelog window");
                //                         UpdateManager.RestartApp();
                //                     }
                //                 }
                // #if !XL_NOAUTOUPDATE
                //                 else
                //                     OnUpdateCheckFinished?.Invoke(true);
                // #endif

                #endregion
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Update failed");

                if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue && (int)httpRequestException.StatusCode is 403 or 444 or 522)
                {
                    {
                        CustomMessageBox.Show("错误: " + $"服务器返回了错误代码 {httpRequestException.StatusCode}.\n你的IP可能被WAF封禁, 请前往频道进行上报." + Environment.NewLine + Loc.Localize("updatefailureerror", "XIVLauncher failed to check for updates. This may be caused by internet connectivity issues. Wait a few minutes and try again.\nDisable your VPN, if you have one. You may also have to exclude XIVLauncher from your antivirus.\nIf this continues to fail after several minutes, please check out the FAQ."),
                                              "XIVLauncherCN",
                                              MessageBoxButton.OK,
                                              MessageBoxImage.Error, showOfficialLauncher: true);
                    }
                }
                else
                {
                    CustomMessageBox.Show("错误: " + ex.Message + Environment.NewLine + Loc.Localize("updatefailureerror", "XIVLauncher failed to check for updates. This may be caused by internet connectivity issues. Wait a few minutes and try again.\nDisable your VPN, if you have one. You may also have to exclude XIVLauncher from your antivirus.\nIf this continues to fail after several minutes, please check out the FAQ."),
                                          "XIVLauncherCN",
                                          MessageBoxButton.OK,
                                          MessageBoxImage.Error, showOfficialLauncher: true);
                }

                Environment.Exit(1);
            }

            // Reset security protocol after updating
            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
        }
    }
}

#nullable restore
