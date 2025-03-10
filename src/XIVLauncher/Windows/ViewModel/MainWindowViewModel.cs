using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.CommandLine;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Castle.Core.Internal;
using CheapLoc;
using FfxivArgLauncher;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Addon;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Exceptions;
using XIVLauncher.Common.Game.Patch;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Game.Patch.PatchList;
using XIVLauncher.Common.PlatformAbstractions;
using XIVLauncher.Common.Util;
using XIVLauncher.Common.Windows;
using XIVLauncher.Game;
using XIVLauncher.PlatformAbstractions;
using XIVLauncher.Support;
using XIVLauncher.Xaml;

namespace XIVLauncher.Windows.ViewModel
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly Window _window;

        private readonly Task<GateStatus> loginStatusTask;
        private bool refetchLoginStatus = false;

        public bool IsLoggingIn;

        public Launcher Launcher { get; private set; }

        public AccountManager AccountManager { get; private set; } = App.AccountManager;

        public Action Activate;
        public Action Hide;
        public Action ReloadHeadlines;

        public string Password { get; set; }
        public SdoArea[] SdoAreas { get; set; }

        public enum AfterLoginAction
        {
            Start,
            StartWithoutDalamud,
            StartWithoutPlugins,
            StartWithoutThird,
            UpdateOnly,
            Repair,
            CancelLogin,
            ForceQR
        };

        public MainWindowViewModel(Window window)
        {
            _window = window;

            SetupLoc();

            StartLoginCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.Start), () => !IsLoggingIn);
            LoginNoStartCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.UpdateOnly), () => !IsLoggingIn);
            LoginNoDalamudCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.StartWithoutDalamud), () => !IsLoggingIn);
            LoginNoPluginsCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.StartWithoutPlugins), () => !IsLoggingIn);
            LoginNoThirdCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.StartWithoutThird), () => !IsLoggingIn);
            LoginRepairCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.Repair), () => !IsLoggingIn);
            LoginCancelCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.CancelLogin));
            LoginForceQRCommand = new SyncCommand(GetLoginFunc(AfterLoginAction.ForceQR));
            InjectGameCommand = new SyncCommand(obj => { this.TryInjectGame(); });
            var frontierUrl = Updates.UpdateLease?.FrontierUrl;
#if DEBUG || RELEASENOUPDATE
            // FALLBACK
            frontierUrl ??= "https://launcher.finalfantasyxiv.com/v650/index.html?rc_lang={0}&time={1}";
#endif

            Launcher = App.GlobalSteamTicket == null
                ? new(App.Steam, App.UniqueIdCache, CommonSettings.Instance, frontierUrl)
                : new(App.GlobalSteamTicket, App.UniqueIdCache, CommonSettings.Instance, frontierUrl);

            // Tried and failed to get this from the theme
            var worldStatusBrushOk = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xf3));
            WorldStatusIconColor = worldStatusBrushOk;

            // Grey out world status icon while deferred check is running
            WorldStatusIconColor = new SolidColorBrush(Color.FromRgb(38, 38, 38));

            //this.loginStatusTask = Launcher.GetLoginStatus();
            //this.loginStatusTask.ContinueWith((resultTask) =>
            //{
            //    try
            //    {
            //        var brushToSet = resultTask.Result.Status ? worldStatusBrushOk : null;
            //        WorldStatusIconColor = brushToSet ?? new SolidColorBrush(Color.FromRgb(242, 24, 24));
            //    }
            //    catch
            //    {
            //        // ignored
            //    }
            //});
        }

        private Action<object> GetLoginFunc(AfterLoginAction action)
        {
            return p =>
            {
                if (action == AfterLoginAction.CancelLogin)
                {
                    CancelLogin();
                    return;
                }
                if (this.IsLoggingIn)
                    return;

                if (action == AfterLoginAction.Start) LoginMessage = String.Empty;

                if (IsAutoLogin && App.Settings.HasShownAutoLaunchDisclaimer.GetValueOrDefault(false) == false)
                {
                    CustomMessageBox.Builder
                                    .NewFrom(Loc.Localize("AutoLoginIntro", "You are enabling Auto-Login.\nThis means that XIVLauncher will always log you in with the current account and you will not see this window.\n\nTo change settings and accounts, you have to hold the shift button on your keyboard while clicking the XIVLauncher icon."))
                                    .WithParentWindow(_window)
                                    .Show();

                    App.Settings.HasShownAutoLaunchDisclaimer = true;
                }

                if (GameHelpers.CheckIsGameOpen() && action == AfterLoginAction.Repair)
                {
                    CustomMessageBox.Builder
                                    .NewFrom(Loc.Localize("GameIsOpenRepairError", "The game and/or the official launcher are open. XIVLauncher cannot repair the game if this is the case.\nPlease close them and try again."))
                                    .WithImage(MessageBoxImage.Exclamation)
                                    .WithParentWindow(_window)
                                    .Show();

                    return;
                }

                if (action == AfterLoginAction.Repair)
                {
                    var res = CustomMessageBox.Builder
                                              .NewFrom(Loc.Localize("GameRepairDisclaimer", "XIVLauncher will now try to find corrupted game files and repair them.\nIf you use any TexTools mods, this will replace all of them and restore the game to its initial state.\n\nDo you want to continue?"))
                                              .WithButtons(MessageBoxButton.YesNo)
                                              .WithImage(MessageBoxImage.Question)
                                              .WithParentWindow(_window)
                                              .Show();

                    if (res == MessageBoxResult.No)
                        return;
                }

                TryLogin(this.GuiLoginType.LoginType, this.Username, this.Password, IsFastLogin, IsReadWegameInfo, action);
            };
        }

        private async Task<LoginData> ReadWegameInfo(string username, string targetAreaId)
        {
            try
            {
                Process.Start(new ProcessStartInfo()
                {
                    FileName = "wegame://StartFor=2000340",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not Launch WeGame");
            }

            var pidList = AppUtil.GetGameProcessIds();
            var argReader = new RemoteArgReader();
            await argReader.Start();
            while (true)
            {
                if (loginCts.IsCancellationRequested)
                {
                    argReader.Stop(false);
                    return null;
                }
                await Task.Delay(1000);
                var newPidList = AppUtil.GetGameProcessIds().Except(pidList);
                this.LoginMessage = $"请使用WeGame启动需要读取的FFXIV";
#if DEBUG
                newPidList = AppUtil.GetGameProcessIds();
#endif
                if (newPidList.Count() == 0)
                    continue;
                var pid = newPidList.First();
                await argReader.OpenProcess(pid);
                var data = await argReader.ReadArgs();
#if DEBUG
                this.LoginMessage = $"读取成功";
#endif
                argReader.Stop(true);
                return data;

            }
        }

        public enum LoginCard
        {
            Logining = 0,
            MainPage = 1,
            ScanQrCode = 2,
        }
        public void SwitchCard(LoginCard i)
        {
            _window.Dispatcher.Invoke(
                () =>
                {
                    this.CancelLogin();
                    this.LoginCardTransitionerIndex = (int)i;
                }
                );
        }

        public void TryLogin(LoginType loginType, string username, string password, bool doingAutoLogin, bool readWeGameInfo, AfterLoginAction action)
        {
            if (this.IsLoggingIn)
                return;
            //if (username == null) username = string.Empty;
            if (_window.Dispatcher != Dispatcher.CurrentDispatcher)
            {
                _window.Dispatcher.Invoke(() => TryLogin(loginType, username, password, doingAutoLogin, readWeGameInfo, action));
                return;
            }

            LoadingDialogCancelButtonVisibility = Visibility.Collapsed;

            IsEnabled = false;
            //LoginCardTransitionerIndex = 0;
            var currentCard = (LoginCard)LoginCardTransitionerIndex;
            this.SwitchCard(loginType == LoginType.SdoQrCode ? LoginCard.ScanQrCode : LoginCard.Logining);
            this.loginCts = new CancellationTokenSource();
            IsLoggingIn = true;

            Task.Run(() =>
            {
                try
                {
                    Login(loginType, username, password, doingAutoLogin, readWeGameInfo, action).Wait();
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Builder.NewFromUnexpectedException(ex, "GetLoginFunc/Task")
                                    .WithParentWindow(_window)
                                    .Show();
                }

                this.SwitchCard(currentCard);
                IsLoggingIn = false;
                IsEnabled = true;

                ReloadHeadlines();
                Activate();
            });
        }

        public const string PresudoPassword = "********假的密码********";
        private async Task Login(LoginType loginType, string username, string inputPassword, bool doingAutoLogin, bool readWeGameInfo, AfterLoginAction action)
        {
            ProblemCheck.RunCheck(_window);

            var bootRes = await HandleBootCheck().ConfigureAwait(false);

            if (!bootRes)
                return;

            var cutoffText = Loc.Localize("KillswitchText", "XIVLauncher cannot start the game at this time, as there were changes to the login process during a recent patch." +
                                                            "\nWe need to adjust to these changes and verify that our adjustments are safe before we can re-enable the launcher. Please try again later." +
                                                            "\n\nWe apologize for these circumstances.\n\nYou can use the \"Official Launcher\" button below to start the official launcher." +
                                                            "\n");

            if (!string.IsNullOrEmpty(Updates.UpdateLease?.CutOffBootver) && !EnvironmentSettings.IsNoKillswitch)
            {
                var bootver = SeVersion.Parse(Repository.Boot.GetVer(App.Settings.GamePath));
                var cutoff = SeVersion.Parse(Updates.UpdateLease.CutOffBootver);

                if (bootver > cutoff)
                {
                    CustomMessageBox.Show(cutoffText, "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.None, showHelpLinks: false, showDiscordLink: true, showOfficialLauncher: true);

                    Environment.Exit(0);
                    return;
                }
            }

            if (Area == null || Area.Areaid == "-1")
            {
                CustomMessageBox.Show(
                    "未能获取到服务器列表,无法登陆",
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                return;
            }

            if (Repository.Ffxiv.GetVer(App.Settings.GamePath) == Constants.BASE_GAME_VERSION &&
                App.Settings.UniqueIdCacheEnabled)
            {
                CustomMessageBox.Show(
                    Loc.Localize("UidCacheInstallError",
                                 "You enabled the UID cache in the patcher settings.\nThis setting does not allow you to reinstall the game.\n\nIf you want to reinstall the game, please take care to disable it first."),
                    "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);

                return;
            }

            if (!doingAutoLogin) App.Settings.AutologinEnabled = IsAutoLogin;
            App.Settings.FastLogin = IsFastLogin;
            // TODO: 太jb乱了，得重构
            var finalLoginType = loginType;
            var serect = string.Empty;
            inputPassword = (inputPassword == PresudoPassword) ? string.Empty : inputPassword?.Trim();

            var accountType = loginType switch
            {
                LoginType.WeGameSid => XivAccountType.WeGameSid,
                LoginType.WeGameToken => XivAccountType.WeGame,
                LoginType.SdoStatic or LoginType.SdoSlide or LoginType.SdoQrCode => XivAccountType.Sdo
            };

            try
            {
                var savedAccount = AccountManager.Accounts.FirstOrDefault(x => x.UserName == username && x.AccountType == accountType);
                switch (loginType)
                {
                    case LoginType.SdoStatic:
                        if (!inputPassword.IsNullOrEmpty())
                        {
                            serect = inputPassword;
                        }
                        else if (savedAccount != null)
                        {
                            serect = await AccountManager.Decrypt(savedAccount.Password);
                        }
                        ArgumentException.ThrowIfNullOrEmpty(username, "静态登录用户名");
                        ArgumentException.ThrowIfNullOrEmpty(serect, "静态登录密码");
                        finalLoginType = LoginType.SdoStatic;
                        break;
                    case LoginType.WeGameSid:
                        if (!App.Settings.HasAgreeWeGameUsage.GetValueOrDefault(false))
                        {
                            var readWeGameUsageAsk = CustomMessageBox.Builder
                                .NewFrom(
                                """
                        为保障您的账号安全，请在使用本功能前仔细阅读以下内容：
                        🔐 功能原理说明
                        本工具通过读取最终幻想14游戏中WeGame平台生成的会话密钥实现快速启动功能，不会对WeGame客户端进行任何修改，也不会获取您的WeGame账号密码等敏感信息。
                        ⚠️ 注意事项
                        会话密钥具有较长有效期，建议您：
                        定期通过WeGame官方客户端登录以刷新密钥
                        避免在公共/共享设备使用本功能
                        发现异常登录时立即通过WeGame重置密钥
                        本工具不会且无法主动更新会话密钥，密钥有效性完全依赖WeGame平台的生成机制

                        点击【确认使用】即表示您已理解：妥善保管设备安全是密钥有效性的最终保障，建议每30天通过官方客户端完整登录一次以保持最佳安全性
                        """)
                                .WithImage(MessageBoxImage.Warning)
                                .WithButtons(MessageBoxButton.YesNo)
                                .WithYesButtonText("确认使用")
                                .WithCaption("WeGame SID登录功能说明")
                                .WithYesCountdown(15)
                                .WithParentWindow(_window)
                                .Show();

                            if (readWeGameUsageAsk == MessageBoxResult.No)
                            {
                                App.Settings.HasAgreeWeGameUsage = false;
                                return;
                            }
                            else
                            {
                                App.Settings.HasAgreeWeGameUsage = true;
                            }
                        }

                        doingAutoLogin = true;
                        if (!readWeGameInfo && savedAccount != null)
                        {
                            serect = await AccountManager.Decrypt(savedAccount.TestSID);
                        }

                        readWeGameInfo = username.IsNullOrEmpty() || serect.IsNullOrEmpty();

                        if (readWeGameInfo)
                        {
                            var loginData = await ReadWegameInfo(username, Area.Areaid);
                            if (loginData == null) { return; }
                            if (loginData.SndaID.IsNullOrEmpty() || loginData.SessionId.IsNullOrEmpty())
                            {
                                throw new Exception("获取WeGame登录信息失败");
                            }
                            username = loginData.SndaID;
                            serect = loginData.SessionId;
                            var areaId = loginData.Args.Where(x => x.Contains("AreaID=")).Select(x => x.Split('=')[1]).First();
                            Area = this.SdoAreas.FirstOrDefault(x => x.Areaid == areaId);
                        }
                        finalLoginType = LoginType.WeGameSid;
                        break;
                    case LoginType.WeGameToken:
                        if (inputPassword.IsNullOrEmpty())
                        {
                            serect = await AccountManager.CredProvider.Decrypt(savedAccount.AutoLoginSessionKey);
                            finalLoginType = LoginType.AutoLoginSession;
                        }
                        if (serect.IsNullOrEmpty())
                        {
                            serect = inputPassword;
                            finalLoginType = LoginType.WeGameToken;
                        }
                        ArgumentException.ThrowIfNullOrEmpty(serect, "自动登录密钥或者Token");
                        break;
                    case LoginType.SdoSlide:
                        if (savedAccount != null && doingAutoLogin)
                        {
                            serect = await AccountManager.Decrypt(savedAccount.AutoLoginSessionKey);
                            finalLoginType = LoginType.AutoLoginSession;
                        }
                        if (serect.IsNullOrEmpty())
                        {
                            finalLoginType = LoginType.SdoSlide;
                        }
                        ArgumentException.ThrowIfNullOrEmpty(username, "一键滑动登录用户名");
                        break;
                    case LoginType.SdoQrCode:
                        break;
                }
            }

            catch (Exception ex)
            {
                if (ex is ArgumentException argEx)
                {
                    Log.Error(ex, "Failed to encrypt text");
                    CustomMessageBox.Show(
                    $"{argEx.ParamName}不能为空",
                        "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                else
                {
                    throw;
                }
            }


            var loginResult = await TryLoginToGame(finalLoginType, loginType, username, serect, doingAutoLogin, action).ConfigureAwait(false);

            if (loginResult == null)
                return;
            loginResult.Area = Area;
            if (loginResult.State == Launcher.LoginState.NeedsPatchGame && action != AfterLoginAction.Repair)
            {
                // 如果需要打补丁且登陆异常，登陆异常状态会覆盖掉NeedsPatchGame，除非和国际服一样，登陆成功才能获取到补丁信息
                // 所以直接改成打完补丁再登陆一遍算了
                // 其实把补丁写到PendingPatches里面也行，通过PendingPatches是否为空来判定是否打补丁
                // 但是考虑到tgt的有效期也就十分钟(大概)
                // 网烂硬盘卡的人打完补丁，tgt也失效了，还得重新登陆
                // 所以还是打好补丁再登陆吧
                action = AfterLoginAction.UpdateOnly;
            }

            if (action != AfterLoginAction.UpdateOnly)
            {
                if (loginResult.State == Launcher.LoginState.Ok)
                //if (true)
                {
                    var accountToSave = new XivAccount()
                    {
                        AutoLogin = loginType == LoginType.WeGameSid || doingAutoLogin,
                        LoginAccount = loginResult.OauthLogin.InputUserId,
                        SndaId = loginResult.OauthLogin.SndaId,
                    };

                    accountToSave.AccountType = loginType switch
                    {
                        LoginType.WeGameSid => XivAccountType.WeGameSid,
                        LoginType.WeGameToken => XivAccountType.WeGame,
                        LoginType.SdoStatic or LoginType.SdoSlide or LoginType.SdoQrCode => XivAccountType.Sdo
                    };

                    accountToSave.AreaName = Area.AreaName;

                    if (doingAutoLogin && accountToSave.AccountType != XivAccountType.WeGameSid)
                    {
                        accountToSave.AutoLoginSessionKey = await AccountManager.Encrypt(loginResult.OauthLogin.AutoLoginSessionKey);
                        if (finalLoginType == LoginType.SdoStatic)
                        {
                            accountToSave.Password = await AccountManager.Encrypt(serect);
                        }
                    }

                    if (accountToSave.AccountType == XivAccountType.WeGameSid)
                    {
                        accountToSave.TestSID = await AccountManager.Encrypt(serect);
                        //accountToSave.TestSID = await AccountManager.CredProvider.Encrypt("password");
                    }
                    accountToSave.GenerateId();
                    AccountManager.AddAccount(accountToSave);
                    AccountManager.CurrentAccount = accountToSave;
                    AccountManager.Save();
                }
            }


            Log.Information("[LR] {State} {NumPatches} {Playable}",
                        loginResult.State,
                        loginResult.PendingPatches?.Length,
                        loginResult.OauthLogin?.Playable);
            await AccountManager.CredProvider.ClearCache();
            serect = null;
            //return;
            if (await TryProcessLoginResult(loginResult, false, action).ConfigureAwait(false))
            {
                if (App.Settings.ExitLauncherAfterGameExit ?? true)
                    Environment.Exit(0);
            }
        }

        private async Task<bool> CheckGateStatus()
        {
            return true;
            GateStatus? gateStatus = null;

            try
            {
                gateStatus = await Launcher.GetGateStatus(App.Settings.Language.GetValueOrDefault(ClientLanguage.English)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not obtain gate status");
            }

            if (gateStatus == null)
            {
                CustomMessageBox.Builder.NewFrom(Loc.Localize("GateUnreachable", "The login servers could not be reached. This usually indicates that the game is under maintenance, or that your connection to the login servers is unstable.\n\nPlease try again later."))
                                .WithImage(MessageBoxImage.Asterisk)
                                .WithButtons(MessageBoxButton.OK)
                                .WithShowHelpLinks(true)
                                .WithCaption("XIVLauncher")
                                .WithParentWindow(_window)
                                .Show();

                return false;
            }

            if (!gateStatus.Status)
            {
                var message = Loc.Localize("GateClosed", "The game is currently under maintenance. Please try again later or see official sources for more information.");

                if (gateStatus.Message != null)
                {
                    var gateMessage = gateStatus.Message.Aggregate("", (current, s) => current + s + "\n");

                    if (!string.IsNullOrEmpty(gateMessage))
                        message = gateMessage;
                }

                var builder = CustomMessageBox.Builder.NewFrom(message)
                                              .WithImage(MessageBoxImage.Asterisk)
                                              .WithButtons(MessageBoxButton.OK)
                                              .WithCaption("XIVLauncher")
                                              .WithParentWindow(_window);

                if (gateStatus.News != null && gateStatus.News.Count > 0)
                {
                    var description = gateStatus.News.Aggregate("", (current, s) => current + s + "\n");

                    if (!string.IsNullOrEmpty(description))
                        builder.WithDescription(description);
                }

                builder.Show();

                return false;
            }

            return true;
        }

        private CancellationTokenSource loginCts;
        public void CancelLogin()
        {
            if (this.loginCts != null)
            {
                Log.Information("取消登陆");
                this.loginCts.Cancel();
            }
        }

        private static BitmapImage ConvertByteArrayToBitmapImage(byte[] imageData)
        {
            if (imageData == null || imageData.Length == 0) return null;

            var bitmapImage = new BitmapImage();
            using (var stream = new MemoryStream(imageData))
            {
                stream.Seek(0, SeekOrigin.Begin); // 确保流的位置在起始处
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad; // 加载后立即释放流
                bitmapImage.StreamSource = stream;
                bitmapImage.EndInit();
                bitmapImage.Freeze(); // 可选：跨线程使用时冻结对象
            }
            return bitmapImage;
        }

        private async Task<Launcher.LoginResult> TryLoginToGame(
            LoginType type,
            LoginType fallbackLoginType,
            string username,
            string serect,
            bool autoLogin,
            AfterLoginAction action
            )
        {
            bool? loginStatus = null;

            try
            {
                var enableUidCache = App.Settings.UniqueIdCacheEnabled;
                var gamePath = App.Settings.GamePath;

                //if (action == AfterLoginAction.Repair)
                //    return await this.Launcher.LoginSdo(username, password, otp, isSteam, false, gamePath, true, App.Settings.IsFt.GetValueOrDefault(false)).ConfigureAwait(false);
                //else
                //    return await this.Launcher.LoginSdo(username, password, otp, isSteam, enableUidCache, gamePath, false, App.Settings.IsFt.GetValueOrDefault(false)).ConfigureAwait(false);
                var checkResult = await Launcher.CheckGameUpdate(Area, gamePath, action == AfterLoginAction.Repair);
                if (checkResult.State == Launcher.LoginState.NeedsPatchGame || action == AfterLoginAction.UpdateOnly)
                    return checkResult;

                if (type == LoginType.AutoLoginSession)
                {
                    try
                    {
                        return await this.Launcher.LoginBySessionKey(username, autoLoginSessionKey: serect).ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        Log.Error("LoginBySessionKey failed, fallback to {fallbackLoginType}:{ex}", fallbackLoginType, e);
                        type = fallbackLoginType;
                    }
                }

                switch (type)
                {
                    case LoginType.SdoStatic:
                        return await Launcher.LoginBySdoStatic(username, password: serect).ConfigureAwait(false);

                    case LoginType.SdoSlide:
                        return await Launcher.LoginBySlide(username, autoLogin, this.loginCts, (code) =>
                        {
                            Log.Information($"叨鱼确认码:{code}");
                            this.LoginMessage = $"确认码: {code}";
                        }).ConfigureAwait(false);

                    case LoginType.SdoQrCode:
                        return await Launcher.LoginByScanQrCode(autoLogin, this.loginCts, (qrBytes) =>
                        {
                            this.QrCodeBitmapImage = ConvertByteArrayToBitmapImage(qrBytes);
                        }).ConfigureAwait(false);

                    case LoginType.WeGameToken:
                        return await Launcher.LoginByWeGameToken(username, token: serect, autoLogin).ConfigureAwait(false);

                    case LoginType.WeGameSid:
                        return await Launcher.LoginBySid(username, sid: serect).ConfigureAwait(false);

                    default:
                        throw new Exception($"Known LoginType:{type}");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "StartGame failed... (LoginStatus={0})", loginStatus);

                var msgbox = new CustomMessageBox.Builder()
                             .WithCaption(Loc.Localize("LoginNoOauthTitle", "Login issue"))
                             .WithImage(MessageBoxImage.Error)
                             .WithShowHelpLinks(true)
                             .WithShowDiscordLink(true)
                             .WithParentWindow(_window);

                if (ex is SdoLoginException sdoLoginEx)
                {
                    if (this.loginCts.IsCancellationRequested)
                    {
                        Log.Information($"手动取消登录");
                        this.loginCts.Dispose();
                        this.loginCts = null;
                        return null;
                    }
                    if (sdoLoginEx.RemoveAutoLoginSessionKey)
                    {
                        Log.Information($"快速登录失败,清除SessionKey:{username}");
                        var account = this.AccountManager.Accounts.First(x => x.UserName == username);
                        account.AutoLoginSessionKey = null;
                        this.AccountManager.Save(account);
                    }

                    msgbox = new CustomMessageBox.Builder()
                            .WithCaption($"{Loc.Localize("LoginNoOauthTitle", "Login issue")}: {sdoLoginEx.ErrorCode}")
                            .WithImage(MessageBoxImage.Question)
                            .WithParentWindow(_window)
                            .WithText(sdoLoginEx.Message);
                    msgbox.Show();
                    return null;
                }

                bool disableAutoLogin = false;

                var steamMaintenanceInfo = string.Empty;
                if (DateTime.UtcNow.DayOfWeek == DayOfWeek.Tuesday && DateTime.UtcNow.Hour >= 15 && DateTime.UtcNow.Hour < 24)
                {
                    steamMaintenanceInfo = Loc.Localize("SteamMaintenanceInfo",
                        "It's also possible that the Steam servers may be undergoing maintenance at the moment. Maintenance is scheduled every Tuesday and may take up to 20 minutes.\n\nPlease try again later.");
                }

                if (ex is IOException)
                {
                    msgbox
                        .WithText(Loc.Localize("LoginIoErrorSummary",
                            "Could not locate game data files."))
                        .WithAppendText("\n\n")
                        .WithAppendText(Loc.Localize("LoginIoErrorActionable",
                            "This may mean that the game path set in XIVLauncher isn't preset, e.g. on a disconnected drive or network storage. Please check the game path in the XIVLauncher settings."));
                }
                else if (ex is InvalidVersionFilesException)
                {
                    msgbox.WithTextFormatted(Loc.Localize("LoginInvalidVersionFiles",
                        "Version information could not be read from your game files.\n\nYou need to reinstall or repair the game files. Right click the login button in XIVLauncher, and choose \"Repair Game\"."), ex.Message);
                }
                else if (ex is SteamTicketNullException)
                {
                    var steamTicketWarning = Loc.Localize("LoginSteamNullTicket",
                                                          "Steam did not authenticate you. This is likely a temporary issue with Steam and you may just have to try again in a few minutes.\n\nIf the issue persists, please make sure that Steam is running and that you are logged in with the account tied to your SE ID.\nIf you play using the Free Trial, please check the \"Using Free Trial account\" checkbox in the \"Game Settings\" tab of the XIVLauncher settings.");

                    if (!string.IsNullOrEmpty(steamMaintenanceInfo))
                        steamTicketWarning += "\n\n" + steamMaintenanceInfo;

                    msgbox.WithText(steamTicketWarning);
                }
                else if (ex is SteamException)
                {
                    msgbox.WithTextFormatted(Loc.Localize("LoginSteamIssue",
                        "Could not authenticate with Steam. Please make sure that Steam is running and that you are logged in with the account tied to your SE ID.\nIf you play using the Free Trial, please check the \"Using Free Trial account\" checkbox in the \"Game Settings\" tab of the XIVLauncher settings.\n\nContext: {0}"), ex.Message);

                    if (ex.InnerException != null)
                        msgbox.WithAppendDescription(ex.InnerException.ToString());
                }
                else if (ex is SteamWrongAccountException wrongAccountException)
                {
                    var locMsg = Loc.Localize("LoginSteamWrongAccount",
                        "The account you are logging in to is not the one that is linked to the Steam account on your PC. You can only log in with the account tied to your SE ID while using this Steam account.\n\nPlease log into matching accounts. The account that is linked to Steam is \"{0}\" - make sure there are no typos.");
                    locMsg = string.Format(locMsg, wrongAccountException.ImposedUserName);

                    msgbox.WithText(locMsg);
                }
                else if (ex is SteamLinkNeededException)
                {
                    msgbox.WithText(Loc.Localize("LoginSteamLinkNeeded", "Before starting the game with this account, you need to link it to your Steam account with the official launcher.\nPlease link your accounts and try again. You can do so by clicking the \"Official Launcher\" button."))
                          .WithShowOfficialLauncher();
                }
                else if (ex is OauthLoginException oauthLoginException)
                {
                    disableAutoLogin = true;
                    LoginMessage = "";
                    QRDialog.CloseQRWindow(_window);
                    if (string.IsNullOrWhiteSpace(oauthLoginException.OauthErrorMessage))
                    {
                        msgbox.WithText(Loc.Localize("LoginGenericError",
                            "Could not log into your SE account.\nPlease check your username and password."));
                    }
                    else
                    {
                        msgbox.WithText(oauthLoginException.OauthErrorMessage
                                                           .Replace("\\r\\n", "\n")
                                                           .Replace("\r\n", "\n"));
                    }

                    //msgbox.WithAppendText("\n\n");
                    //if (otp == string.Empty)
                    //    msgbox.WithAppendTextFormatted(Loc.Localize("LoginGenericErrorCheckOtpUse",
                    //        "If you're using OTP, then tick on \"{0}\" checkbox and try again."), OtpLoc);
                    //else
                    //    msgbox.WithAppendText(Loc.Localize("LoginGenericErrorCheckOtp",
                    //        "Double check whether your OTP device's clock is correct.\nIf you have recently logged in, then try logging in again in 30 seconds."));
                }
                // If GateStatus is not set (even gate server could not be contacted) or GateStatus is true (gate server says everything's fine but could not contact login servers)
                else if (ex is HttpRequestException || ex is TaskCanceledException || ex is WebException)
                {
                    msgbox.WithText(Loc.Localize("LoginWebExceptionContent",
                        "XIVLauncher could not establish a connection to the game servers.\n\nThis may be a temporary issue, or a problem with your internet connection. Please try again later."));
                }
                else if (ex is InvalidResponseException iex)
                {
                    Log.Error("Invalid response from server! Context: {Message}\n{Document}", ex.Message, iex.Document);

                    msgbox.WithText(Loc.Localize("LoginGenericServerIssue",
                        "The server has sent an invalid response. This is known to occur during outages or when servers are under heavy load.\nPlease wait a minute and try again, or try using the official launcher.\n\nYou can learn more about outages on the Lodestone."));
                }
                // Actual unexpected error; show error details
                else
                {
                    disableAutoLogin = true;
                    msgbox.WithShowNewGitHubIssue(true)
                          .WithAppendDescription(ex.ToString())
                          .WithAppendSettingsDescription("Login")
                          .WithAppendText("\n\n")
                          .WithAppendText(Loc.Localize("CheckLoginInfoNotAdditionally",
                              "Please check your login information or try again."));
                }

                if (disableAutoLogin && App.Settings.AutologinEnabled)
                {
                    msgbox.WithAppendText(Loc.Localize("LoginNoOauthAutologinHint", "\n\nAuto-Login has been disabled."));
                    App.Settings.AutologinEnabled = false;
                }

                msgbox.Show();
                return null;
            }
        }

        private async Task<bool> TryProcessLoginResult(Launcher.LoginResult loginResult, bool isSteam, AfterLoginAction action)
        {
            if (loginResult.State == Launcher.LoginState.NoService)
            {
                CustomMessageBox.Show(
                    Loc.Localize("LoginNoServiceMessage",
                        "This account isn't eligible to play the game. Please make sure that you have an active subscription and that it is paid up.\n\nIf you bought the game on Steam, make sure to check the \"Use Steam service account\" checkbox while logging in.\nIf Auto-Login is enabled, hold shift while starting to access settings."),
                    "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error, showHelpLinks: false, showDiscordLink: false, parentWindow: _window);

                return false;
            }

            if (loginResult.State == Launcher.LoginState.NoTerms)
            {
                CustomMessageBox.Show(
                    Loc.Localize("LoginAcceptTermsMessage",
                        "Please accept the Terms of Use in the official launcher."),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, showOfficialLauncher: true, parentWindow: _window);

                return false;
            }

            if (loginResult.State == Launcher.LoginState.NeedsPatchBoot)
            {
                CustomMessageBox.Show(
                    Loc.Localize("EverythingIsFuckedMessage",
                        "Certain essential game files were modified/broken by a third party and the game can neither update nor start.\nYou have to reinstall the game to continue.\n\nIf this keeps happening, please contact us via Discord."),
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);

                return false;
            }

            if (action == AfterLoginAction.Repair)
            {
                try
                {
                    if (loginResult.State == Launcher.LoginState.NeedsPatchGame)
                    {
                        if (!await RepairGame(loginResult).ConfigureAwait(false))
                            return false;

                        loginResult.State = Launcher.LoginState.Ok;
                    }
                    else
                    {
                        CustomMessageBox.Show(
                            Loc.Localize("LoginRepairResponseIsNotNeedsPatchGame",
                                "The server sent an incorrect response - the repair cannot proceed."),
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);

                        return false;
                    }
                }
                catch (Exception ex)
                {
                    /*
                     * We should never reach here.
                     * If server responds badly, then it should not even have reached this point, as error cases should have been handled before.
                     * If RepairGame was unsuccessful, then it should have handled all of its possible errors, instead of propagating it upwards.
                     */
                    CustomMessageBox.Builder.NewFrom(ex, "TryProcessLoginResult/Repair").WithParentWindow(_window).Show();

                    return false;
                }
            }

            if (loginResult.State == Launcher.LoginState.NeedsPatchGame)
            {
                if (App.Settings.AskBeforePatchInstall ?? true)
                {
                    var selfPatchAsk = CustomMessageBox.Show(
                        Loc.Localize("PatchInstallDisclaimer",
                            "A new patch has been found that needs to be installed before you can play.\nDo you wish for XIVLauncher to install it?"),
                        "Out of date", MessageBoxButton.YesNo, MessageBoxImage.Information, parentWindow: _window);

                    if (selfPatchAsk == MessageBoxResult.No)
                        return false;
                }

                if (!await InstallGamePatch(loginResult).ConfigureAwait(false))
                {
                    Log.Error("patchSuccess != true");
                    return false;
                }

                loginResult.State = Launcher.LoginState.Ok;
            }

            if (action == AfterLoginAction.UpdateOnly)
            {
                CustomMessageBox.Show(
                    Loc.Localize("LoginNoStartOk",
                        "An update check was executed and any pending updates were installed."), "XIVLauncherCN",
                    MessageBoxButton.OK, MessageBoxImage.Information, showHelpLinks: false, showDiscordLink: false, parentWindow: _window);

                return false;
            }

            if (loginResult.State == Launcher.LoginState.NeedRetry)
            {
                Log.Error("loginResult.State == NeedRetry");
                CustomMessageBox.Show(
                    Loc.Localize("LoginNeedRetry",
                                 "登录失败,建议尝试重新扫码登录."), "XIVLauncherCN",
                    MessageBoxButton.OK, MessageBoxImage.Information, showHelpLinks: false, showDiscordLink: false, parentWindow: _window);
                return false;
            }

            if (CustomMessageBox.AssertOrShowError(loginResult.State == Launcher.LoginState.Ok, "TryProcessLoginResult: loginResult.State should have been Launcher.LoginState.Ok", parentWindow: _window))
                return false;

#if !DEBUG
            //if (!await CheckGateStatus().ConfigureAwait(false))
            //    return false;
#endif

            Hide();

            while (true)
            {
                List<Exception> exceptions = new();

                try
                {
                    if (loginResult.OauthLogin.SessionId.IsNullOrEmpty() || loginResult.OauthLogin.SndaId.IsNullOrEmpty())
                    {
                        Log.Error($"SID或SNDAID为空，取消登录");
                        CustomMessageBox.Show("SID或SNDAID为空", "Error", MessageBoxButton.OK, MessageBoxImage.Error, showOfficialLauncher: true, parentWindow: _window);
                        return false;
                    }

                    using var process = await StartGameAndAddon(
                        loginResult,
                        isSteam,
                        action == AfterLoginAction.StartWithoutDalamud || Updates.HaveFeatureFlag(Updates.LeaseFeatureFlags.GlobalDisableDalamud),
                        action == AfterLoginAction.StartWithoutThird,
                        action == AfterLoginAction.StartWithoutPlugins).ConfigureAwait(false);

                    if (process == null)
                        return false;

                    if (process.ExitCode != 0 && (App.Settings.TreatNonZeroExitCodeAsFailure ?? false))
                    {
                        switch (new CustomMessageBox.Builder()
                                .WithTextFormatted(
                                    Loc.Localize("LaunchGameNonZeroExitCode",
                                        "It looks like the game has exited with a fatal error. Do you want to relaunch the game?\n\nExit code: 0x{0:X8}"),
                                    (uint)process.ExitCode)
                                .WithImage(MessageBoxImage.Exclamation)
                                .WithShowHelpLinks(true)
                                .WithShowDiscordLink(true)
                                .WithShowNewGitHubIssue(true)
                                .WithButtons(MessageBoxButton.YesNoCancel)
                                .WithDefaultResult(MessageBoxResult.Yes)
                                .WithCancelResult(MessageBoxResult.No)
                                .WithYesButtonText(Loc.Localize("LaunchGameRelaunch", "_Relaunch"))
                                .WithNoButtonText(Loc.Localize("LaunchGameClose", "_Close"))
                                .WithCancelButtonText(Loc.Localize("LaunchGameDoNotAskAgain", "_Don't ask again"))
                                .WithParentWindow(_window)
                                .Show())
                        {
                            case MessageBoxResult.Yes:
                                continue;

                            case MessageBoxResult.No:
                                return true;

                            case MessageBoxResult.Cancel:
                                App.Settings.TreatNonZeroExitCodeAsFailure = false;
                                return true;
                        }
                    }

                    return true;
                }
                catch (AggregateException ex)
                {
                    Log.Error(ex, "StartGameAndError resulted in one or more exceptions.");

                    exceptions.Add(ex.Flatten().InnerException);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "StartGameAndError resulted in an exception.");

                    exceptions.Add(ex);
                }

                var builder = new CustomMessageBox.Builder()
                              .WithImage(MessageBoxImage.Error)
                              .WithShowHelpLinks(true)
                              .WithShowDiscordLink(true)
                              .WithShowNewGitHubIssue(true)
                              .WithButtons(MessageBoxButton.YesNo)
                              .WithDefaultResult(MessageBoxResult.No)
                              .WithCancelResult(MessageBoxResult.No)
                              .WithYesButtonText(Loc.Localize("LaunchGameRetry", "_Try again"))
                              .WithNoButtonText(Loc.Localize("LaunchGameClose", "_Close"))
                              .WithParentWindow(_window);

                //NOTE(goat): This HAS to handle all possible exceptions from StartGameAndAddon!!!!!
                List<string> summaries = new();
                List<string> actionables = new();
                List<string> descriptions = new();

                foreach (var exception in exceptions)
                {
                    switch (exception)
                    {
                        case DalamudRunnerException:
                        case GameExitedException:
                            var count = 0;

                            foreach (var processName in new string[] { "ffxiv_dx11", "ffxiv" })
                            {
                                foreach (var process in Process.GetProcessesByName(processName))
                                {
                                    count++;
                                    process.Dispose();
                                }
                            }

                            if (count >= 2)
                            {
                                summaries.Add(Loc.Localize("MultiboxDeniedWarningSummary",
                                    "You can't launch more than two instances of the game by default."));
                                actionables.Add(string.Format(
                                    Loc.Localize("MultiboxDeniedWarningActionable",
                                        "Please check if there is an instance of the game that did not close correctly. (Detected: {0})"),
                                    count));
                                descriptions.Add(null);

                                builder.WithButtons(MessageBoxButton.YesNoCancel)
                                       .WithDefaultResult(MessageBoxResult.Yes)
                                       .WithCancelButtonText(Loc.Localize("LaunchGameKillThenRetry", "_Kill then try again"));
                            }
                            else
                            {
                                summaries.Add(Loc.Localize("GameExitedPrematurelyErrorSummary",
                                    "XIVLauncher could not start the game correctly."));
                                descriptions.Add(null);

                                var actionableText = Loc.Localize("GameExitedPrematurelyErrorActionable",
                                    "This may be a temporary issue. Please try restarting your PC.\nIt is possible that your game installation is not valid - you can repair your game installation by right clicking the Login button and choosing \"Repair game\".");
                                actionableText += Loc.Localize("GameExitedPrematurelyErrorAV",
                                    "\nThis issue could also be caused by your Antivirus program mistakenly marking XIVLauncher as malicious. You may have to add exclusions to its settings - please check our FAQ for more information.");

                                actionables.Add(actionableText);
                            }

                            builder.WithShowNewGitHubIssue(false);

                            break;

                        case BinaryNotPresentException:
                            summaries.Add(Loc.Localize("BinaryNotPresentErrorSummary",
                                "Could not find the game executable."));
                            actionables.Add(Loc.Localize("BinaryNotPresentErrorActionable",
                                "This might be caused by your antivirus. You may have to reinstall the game."));
                            descriptions.Add(null);
                            break;

                        case IOException:
                            summaries.Add(Loc.Localize("LoginIoErrorSummary",
                                "Could not locate game data files."));
                            summaries.Add(Loc.Localize("LoginIoErrorActionable",
                                "This may mean that the game path set in XIVLauncher isn't preset, e.g. on a disconnected drive or network storage. Please check the game path in the XIVLauncher settings."));
                            descriptions.Add(exception.ToString());
                            break;

                        case Win32Exception win32Exception:
                            summaries.Add(string.Format(
                                Loc.Localize("UnexpectedErrorSummary",
                                    "Unexpected error has occurred. ({0})"),
                                $"0x{(uint)win32Exception.HResult:X8}: {win32Exception.Message}"));
                            actionables.Add(Loc.Localize("UnexpectedErrorActionable",
                                "Please report this error."));
                            descriptions.Add(exception.ToString());
                            break;

                        default:
                            summaries.Add(string.Format(
                                Loc.Localize("UnexpectedErrorSummary",
                                    "Unexpected error has occurred. ({0})"),
                                exception.Message));
                            actionables.Add(Loc.Localize("UnexpectedErrorActionable",
                                "Please report this error."));
                            descriptions.Add(exception.ToString());
                            break;
                    }
                }

                if (exceptions.Count == 1)
                {
                    builder.WithText($"{summaries[0]}\n\n{actionables[0]}")
                           .WithDescription(descriptions[0]);
                }
                else
                {
                    builder.WithText(Loc.Localize("MultipleErrors", "Multiple errors have occurred."));

                    for (var i = 0; i < summaries.Count; i++)
                    {
                        builder.WithAppendText($"\n{i + 1}. {summaries[i]}\n    => {actionables[i]}");
                        if (string.IsNullOrWhiteSpace(descriptions[i]))
                            continue;
                        builder.WithAppendDescription($"########## Exception {i + 1} ##########\n{descriptions[i]}\n\n");
                    }
                }

                if (descriptions.Any(x => x != null))
                    builder.WithAppendSettingsDescription("Login");

                switch (builder.Show())
                {
                    case MessageBoxResult.Yes:
                        continue;

                    case MessageBoxResult.No:
                        return false;

                    case MessageBoxResult.Cancel:
                        for (var pass = 0; pass < 8; pass++)
                        {
                            var allKilled = true;

                            foreach (var processName in new string[] { "ffxiv_dx11", "ffxiv" })
                            {
                                foreach (var process in Process.GetProcessesByName(processName))
                                {
                                    allKilled = false;

                                    try
                                    {
                                        process.Kill();
                                    }
                                    catch (Exception ex2)
                                    {
                                        Log.Warning(ex2, "Could not kill process (PID={0}, name={1})", process.Id, process.ProcessName);
                                    }
                                    finally
                                    {
                                        process.Dispose();
                                    }
                                }
                            }

                            if (allKilled)
                                break;
                        }

                        Task.Delay(1000).Wait();
                        continue;
                }
            }
        }

        private async Task<bool> RepairGame(Launcher.LoginResult loginResult)
        {
            var doLogin = false;
            var mutex = new Mutex(false, "XivLauncherIsPatching");

            if (mutex.WaitOne(0, false))
            {
                Debug.Assert(loginResult.PendingPatches != null, "loginResult.PendingPatches != null");
                Debug.Assert(loginResult.PendingPatches.Length != 0, "loginResult.PendingPatches.Length != 0");

                Log.Information("STARTING REPAIR");

                if (!AppUtil.TryYellOnGameFilesBeingOpen(_window, n => n switch
                    {
                        1 => Loc.Localize("GameRepairProcessExitRequired1",
                            "Close the following application to repair the game."),
                        _ => string.Format(Loc.Localize("GameRepairProcessExitRequiredPlural",
                            "Close the following applications to repair the game.")),
                    }))
                    return false;

                using var verify = new PatchVerifier(CommonSettings.Instance, loginResult, TimeSpan.FromMilliseconds(100), Constants.MaxExpansion);

                Hide();
                IsEnabled = false;

                var progressDialog = _window.Dispatcher.Invoke(() =>
                {
                    var d = new GameRepairProgressWindow(verify);
                    if (_window.IsVisible)
                        d.Owner = _window;
                    d.Show();
                    d.Activate();
                    return d;
                });

                for (bool doVerify = true; doVerify;)
                {
                    progressDialog.Dispatcher.Invoke(progressDialog.Show);

                    verify.Start();
                    await verify.WaitForCompletion().ConfigureAwait(false);

                    progressDialog.Dispatcher.Invoke(progressDialog.Hide);

                    switch (verify.State)
                    {
                        case PatchVerifier.VerifyState.Done:
                            switch (CustomMessageBox.Builder
                                .NewFrom(verify.NumBrokenFiles switch
                                {
                                    0 => Loc.Localize("GameRepairSuccess0", "All game files seem to be valid."),
                                    1 => Loc.Localize("GameRepairSuccess1", "XIVLauncher has successfully repaired 1 game file."),
                                    _ => string.Format(Loc.Localize("GameRepairSuccessPlural", "XIVLauncher has successfully repaired {0} game files."), verify.NumBrokenFiles),
                                })
                                .WithAppendText(verify.MovedFiles.Count switch
                                {
                                    0 => "",
                                    1 => "\n\n" + string.Format(Loc.Localize("GameRepairSuccessMoved1", "Additionally, 1 file that did not come with the original game installation has been moved to {0}.\nIf you were using ReShade, you will have to reinstall it."), verify.MovedFileToDir),
                                    _ => "\n\n" + string.Format(Loc.Localize("GameRepairSuccessMovedPlural", "Additionally, {0} files that did not come with the original game installation have been moved to {1}.\nIf you were using ReShade, you will have to reinstall it."), verify.MovedFiles.Count, verify.MovedFileToDir),
                                })
                                .WithDescription(verify.MovedFiles.Any() ? string.Join("\n", verify.MovedFiles.Select(x => $"* {x}")) : null)
                                .WithImage(MessageBoxImage.Information)
                                .WithButtons(MessageBoxButton.YesNoCancel)
                                .WithYesButtonText(Loc.Localize("GameRepairSuccess_LaunchGame", "_Launch game"))
                                .WithNoButtonText(Loc.Localize("GameRepairSuccess_VerifyAgain", "_Verify again"))
                                .WithCancelButtonText(Loc.Localize("GameRepairSuccess_Close", "_Close"))
                                .WithParentWindow(_window)
                                .Show())
                            {
                                case MessageBoxResult.Yes:
                                    doLogin = true;
                                    doVerify = false;
                                    break;
                                case MessageBoxResult.No:
                                    doLogin = false;
                                    doVerify = true;
                                    break;
                                case MessageBoxResult.Cancel:
                                    doLogin = doVerify = false;
                                    break;
                            }
                            break;

                        case PatchVerifier.VerifyState.Error:
                            doLogin = false;
                            if (verify.LastException is NoVersionReferenceException)
                            {
                                doVerify = CustomMessageBox.Builder
                                    .NewFrom(Loc.Localize("NoVersionReferenceError",
                                        "The version of the game you are on cannot be repaired by XIVLauncher yet, as reference information is not yet available.\nPlease try again later."))
                                    .WithImage(MessageBoxImage.Exclamation)
                                    .WithButtons(MessageBoxButton.OKCancel)
                                    .WithOkButtonText(Loc.Localize("GameRepairSuccess_TryAgain", "_Try again"))
                                    .WithParentWindow(_window)
                                    .Show() == MessageBoxResult.OK;
                            }
                            // Seemingly no better way to detect this, probably brittle if this is localized
                            else if (verify.LastException != null && verify.LastException.ToString().Contains("Data error"))
                            {
                                doVerify = new CustomMessageBox.Builder()
                                           .WithText(Loc.Localize("GameRepairError_DataError", "Your hard drive reported an error while checking game files. XIVLauncher cannot repair this installation, as the error may indicate a physical issue with your hard drive.\nPlease check your drive's health, or try to update its firmware.\nReinstalling the game in a new location may solve this issue temporarily."))
                                           .WithExitOnClose(CustomMessageBox.ExitOnCloseModes.DontExitOnClose)
                                           .WithImage(MessageBoxImage.Error)
                                           .WithShowHelpLinks(true)
                                           .WithShowDiscordLink(true)
                                           .WithShowNewGitHubIssue(false)
                                           .WithButtons(MessageBoxButton.OKCancel)
                                           .WithOkButtonText(Loc.Localize("GameRepairSuccess_TryAgain", "_Try again"))
                                           .WithParentWindow(_window)
                                           .Show() == MessageBoxResult.OK;
                            }
                            else
                            {
                                doVerify = CustomMessageBox.Builder
                                    .NewFrom(verify.LastException, "PatchVerifier")
                                    .WithAppendText("\n\n")
                                    .WithAppendText(Loc.Localize("GameRepairError", "An error occurred while repairing the game files.\nYou may have to reinstall the game."))
                                    .WithImage(MessageBoxImage.Exclamation)
                                    .WithButtons(MessageBoxButton.OKCancel)
                                    .WithOkButtonText(Loc.Localize("GameRepairSuccess_TryAgain", "_Try again"))
                                    .WithParentWindow(_window)
                                    .Show() == MessageBoxResult.OK;
                            }
                            break;

                        case PatchVerifier.VerifyState.Cancelled:
                            doLogin = doVerify = false;
                            break;
                    }
                }

                progressDialog.Dispatcher.Invoke(progressDialog.Close);
                mutex.Close();
                mutex = null;
            }
            else
            {
                CustomMessageBox.Show(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."), "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
            }

            return doLogin;
        }

        private Task<bool> InstallGamePatch(Launcher.LoginResult loginResult)
        {
            if (loginResult.State != Launcher.LoginState.NeedsPatchGame)
                throw new ArgumentException(@"loginResult.State != Launcher.LoginState.NeedsPatchGame", nameof(loginResult));

            if (loginResult.PendingPatches == null)
                throw new ArgumentException(@"loginResult.PendingPatches == null", nameof(loginResult));

            if (loginResult.PendingPatches.Length == 0)
                throw new ArgumentException(@"loginResult.PendingPatches.Length == 0", nameof(loginResult));

            return TryHandlePatchAsync(Repository.Ffxiv, loginResult.PendingPatches, loginResult.UniqueId);
        }

        private void PatcherOnFail(PatchListEntry patch, string context)
        {
            var dlFailureLoc = Loc.Localize("PatchManDlFailure",
                "XIVLauncher could not verify the downloaded game files. Please restart and try again.\n\nThis usually indicates a problem with your internet connection.\n\nContext: {0}\n{1}");

            var sdoPatchMissingFailureLoc = Loc.Localize("SdoPatchMissing",
                "游戏补丁列表的早期补丁被删除，导致无法通过补丁安装游戏，请手动安装游戏客户端并设置包含 game 文件夹的游戏路径。\nContext: {0}\n{1}");


            Environment.Exit(0);
        }

        private void InstallerOnFail()
        {
            try
            {
                // Reset UID cache, we need users to log in again
                App.UniqueIdCache.Reset();
            }
            catch
            {
                // ignored
            }

            CustomMessageBox.Show(
                Loc.Localize("PatchInstallerInstallFailed", "The patch installer ran into an error.\nPlease report this error.\n\nPlease try again or use the official launcher."),
                "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error);

            Environment.Exit(0);
        }

        bool IsInjecting = false;
        public void TryInjectGame()
        {
            if (this.IsInjecting)
                return;
            //if (username == null) username = string.Empty;
            if (_window.Dispatcher != Dispatcher.CurrentDispatcher)
            {
                _window.Dispatcher.Invoke(() => TryInjectGame());
                return;
            }
            IsLoadingDialogOpen = true;
            LoadingDialogMessage = "注入灵魂中...";
            Task.Run(() =>
            {
                try
                {
                    if (!PlatformHelpers.IsElevated())
                    {
                        Log.Error($"当前XLCN并非管理器权限");
                        var proc = new ProcessStartInfo
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Environment.CurrentDirectory,
                            FileName = Process.GetCurrentProcess().MainModule.FileName,
                            Verb = "runas",
                            Arguments = "--inject"
                        };

                        Process.Start(proc);
                        Environment.Exit(0);
                    }

                    if (InjectGame())
                    {
                        var dialog = CustomMessageBox.Builder
                            .NewFrom("注入完成,是否退出XIVLauncherCN?")
                            .WithButtons(MessageBoxButton.YesNo)
                            .WithCaption("XIVLauncherCN")
                            .WithParentWindow(_window)
                            .Show();
                        if (dialog == MessageBoxResult.Yes)
                        {
                            Log.CloseAndFlush();
                            Environment.Exit(0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    CustomMessageBox.Builder.NewFromUnexpectedException(ex, "InjectGame")
                                    .WithParentWindow(_window)
                                    .Show();
                }
                IsLoadingDialogOpen = false;
                IsInjecting = false;
                Activate();
            });
        }
        public bool InjectGame(bool noThird = false, bool noPlugins = false)
        {
            var pidList = AppUtil.GetGameProcessIds();
            if (pidList.Count() == 0)
            {
                CustomMessageBox.Show($"没有找到对应的游戏进程, 请检查后重试", "注入失败");
                return false;
            }

            var gamePid = pidList.First();
            var gameExePath = Process.GetProcessById(gamePid).MainModule?.FileName;
            var gameExeFolder = Path.GetDirectoryName(gameExePath);
            var gamePath = (new DirectoryInfo(gameExeFolder!)).Parent;
            Log.Information($"GameExePath:{gameExePath},GameExeFolder:{gameExeFolder},GamePath;{gamePath}");
            if (!DalamudLauncher.CanRunDalamud(gamePath))
            {
                CustomMessageBox.Show($"""
                    {Loc.Localize("DalamudIncompatible", "Dalamud was not yet updated for your current game version.\nThis is common after patches, so please be patient or ask on the Discord for a status update!")}
                    GameExePath:{gameExePath}
                    GameExeFolder:{gameExeFolder}
                    GamePath:{gamePath}
                    GameVersion:{Repository.Ffxiv.GetVer(gamePath)}
                    """,
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Asterisk);
                return false;
            }


            var dalamudLauncher = new DalamudLauncher(new WindowsDalamudRunner(), App.DalamudUpdater, DalamudLoadMethod.DllInject,
                gamePath,
                new DirectoryInfo(Paths.RoamingPath),
                new DirectoryInfo(Paths.RoamingPath),
                App.Settings.Language.GetValueOrDefault(ClientLanguage.ChineseSimplified),
                (int)App.Settings.DalamudInjectionDelayMs,
                false,
                noPlugins,
                noThird,
                Troubleshooting.GetTroubleshootingJson());

            var dalamudOk = false;

            var dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();

            try
            {
                dalamudCompatCheck.EnsureCompatibility();
            }
            catch (IDalamudCompatibilityCheck.NoRedistsException ex)
            {
                Log.Error(ex, "No Dalamud Redists found");

                CustomMessageBox.Show(
                    Loc.Localize("DalamudVc2019RedistError",
                        "The XIVLauncher in-game addon needs the Microsoft Visual C++ 2015-2019 redistributable to be installed to continue. Please install it from the Microsoft homepage."),
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
            }
            catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
            {
                Log.Error(ex, "Architecture not supported");

                CustomMessageBox.Show(
                    Loc.Localize("DalamudArchError",
                        "Dalamud cannot run your computer's architecture. Please make sure that you are running a 64-bit version of Windows.\nIf you are using Windows on ARM, please make sure that x64-Emulation is enabled for XIVLauncher."),
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
            }

            try
            {
                var dalamudStatus = dalamudLauncher.HoldForUpdate(App.Settings.GamePath);
                dalamudOk = dalamudStatus == DalamudLauncher.DalamudInstallState.Ok;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't DalamudLauncher::HoldForUpdate()");

                var ensurementErrorMessage = Loc.Localize("DalamudEnsurementError",
                                                          "Could not download necessary data files to use Dalamud and plugins.\nThis could be a problem with your internet connection, or might be caused by your antivirus application blocking necessary files. The game will start, but you will not be able to use plugins.\n\nPlease check our FAQ for more information.");

                if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue && (int)httpRequestException.StatusCode is 403 or 444 or 522)
                    ensurementErrorMessage = "错误: " + $"服务器返回了错误代码 {httpRequestException.StatusCode}.\n你的IP可能被WAF封禁, 请前往频道进行上报." + Environment.NewLine + ensurementErrorMessage;
                else
                    ensurementErrorMessage = "错误: " + ex.Message + Environment.NewLine + ensurementErrorMessage;

                CustomMessageBox.Builder
                                .NewFrom(ensurementErrorMessage)
                                .WithImage(MessageBoxImage.Warning)
                                .WithButtons(MessageBoxButton.OK)
                                .WithShowHelpLinks()
                                .WithParentWindow(_window)
                                .Show();
            }

            Troubleshooting.LogTroubleshooting();
            if (!dalamudOk)
            {
                CustomMessageBox.Show($"Dalamud尚未下载完成", "注入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            dalamudLauncher.Inject(gamePid, noPlugins);
            return true;
        }

        public async Task<Process> StartGameAndAddon(Launcher.LoginResult loginResult, bool isSteam, bool forceNoDalamud, bool noThird, bool noPlugins)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            var dalamudLauncher = new DalamudLauncher(new WindowsDalamudRunner(), App.DalamudUpdater, App.Settings.InGameAddonLoadMethod.GetValueOrDefault(DalamudLoadMethod.DllInject),
                App.Settings.GamePath,
                new DirectoryInfo(Paths.RoamingPath),
                new DirectoryInfo(Paths.RoamingPath),
                App.Settings.Language.GetValueOrDefault(ClientLanguage.English),
                (int)App.Settings.DalamudInjectionDelayMs,
                false,
                noPlugins,
                noThird,
                Troubleshooting.GetTroubleshootingJson());

            var dalamudOk = false;

            var dalamudCompatCheck = new WindowsDalamudCompatibilityCheck();

            try
            {
                dalamudCompatCheck.EnsureCompatibility();
            }
            catch (IDalamudCompatibilityCheck.NoRedistsException ex)
            {
                Log.Error(ex, "No Dalamud Redists found");

                CustomMessageBox.Show(
                    Loc.Localize("DalamudVc2019RedistError",
                        "The XIVLauncher in-game addon needs the Microsoft Visual C++ 2015-2019 redistributable to be installed to continue. Please install it from the Microsoft homepage."),
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
            }
            catch (IDalamudCompatibilityCheck.ArchitectureNotSupportedException ex)
            {
                Log.Error(ex, "Architecture not supported");

                CustomMessageBox.Show(
                    Loc.Localize("DalamudArchError",
                        "Dalamud cannot run your computer's architecture. Please make sure that you are running a 64-bit version of Windows.\nIf you are using Windows on ARM, please make sure that x64-Emulation is enabled for XIVLauncher."),
                    "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
            }

            if (App.Settings.InGameAddonEnabled && !forceNoDalamud)
            {
                try
                {
                    var dalamudStatus = dalamudLauncher.HoldForUpdate(App.Settings.GamePath);
                    dalamudOk = dalamudStatus == DalamudLauncher.DalamudInstallState.Ok;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Couldn't DalamudLauncher::HoldForUpdate()");

                    var ensurementErrorMessage = Loc.Localize("DalamudEnsurementError",
                                                              "Could not download necessary data files to use Dalamud and plugins.\nThis could be a problem with your internet connection, or might be caused by your antivirus application blocking necessary files. The game will start, but you will not be able to use plugins.\n\nPlease check our FAQ for more information.");

                    if (ex is HttpRequestException httpRequestException && httpRequestException.StatusCode.HasValue && (int)httpRequestException.StatusCode is 403 or 444 or 522)
                        ensurementErrorMessage = "错误: " + $"服务器返回了错误代码 {httpRequestException.StatusCode}.\n你的IP可能被WAF封禁, 请前往频道进行上报." + Environment.NewLine + ensurementErrorMessage;
                    else
                        ensurementErrorMessage = "错误: " + ex.Message + Environment.NewLine + ensurementErrorMessage;

                    CustomMessageBox.Builder
                                    .NewFrom(ensurementErrorMessage)
                                    .WithImage(MessageBoxImage.Warning)
                                    .WithButtons(MessageBoxButton.OK)
                                    .WithShowHelpLinks()
                                    .WithParentWindow(_window)
                                    .Show();
                }
            }

            var gameRunner = new WindowsGameRunner(dalamudLauncher, dalamudOk, App.DalamudUpdater.Runtime);
            stopwatch.Stop();
            if (stopwatch.Elapsed > TimeSpan.FromMinutes(5))
            {
                CustomMessageBox.Show("会话已过期,请重新登录", "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Exclamation, parentWindow: _window);
                return null;
            }
            // We won't do any sanity checks here anymore, since that should be handled in StartLogin
            var launched = this.Launcher.LaunchGameSdo(gameRunner,
                                                       loginResult.OauthLogin.SessionId,
                                                       loginResult.OauthLogin.SndaId,
                                                       Area.Areaid,
                                                       Area.AreaLobby,
                                                       Area.AreaGm,
                                                       Area.AreaConfigUpload,
                                                       App.Settings.AdditionalLaunchArgs,
                                                       App.Settings.GamePath,
                                                       App.Settings.EncryptArgumentsV2.GetValueOrDefault(true),
                                                       App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware));
            // var launched = this.Launcher.LaunchGame(gameRunner,
            //     loginResult.UniqueId,
            //     loginResult.OauthLogin.Region,
            //     loginResult.OauthLogin.MaxExpansion,
            //     isSteam,
            //     App.Settings.AdditionalLaunchArgs,
            //     App.Settings.GamePath,
            //     App.Settings.Language.GetValueOrDefault(ClientLanguage.English),
            //     App.Settings.EncryptArguments.GetValueOrDefault(false),
            //     App.Settings.DpiAwareness.GetValueOrDefault(DpiAwareness.Unaware));

            Troubleshooting.LogTroubleshooting();

            if (launched is not Process gameProcess)
            {
                Log.Information("GameProcess was null...");
                IsLoggingIn = false;
                return null;
            }

            var addonMgr = new AddonManager();

            try
            {
                App.Settings.AddonList ??= new List<AddonEntry>();

                var addons = App.Settings.AddonList.Where(x => x.IsEnabled).Select(x => x.Addon).Cast<IAddon>().ToList();

                addonMgr.RunAddons(gameProcess.Id, addons);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Builder
                                .NewFrom(ex, "Addons")
                                .WithAppendText("\n\n")
                                .WithAppendText(Loc.Localize("AddonLoadError",
                                                             "This could be caused by your antivirus, please check its logs and add any needed exclusions."))
                                .WithParentWindow(_window)
                                .Show();

                IsLoggingIn = false;

                addonMgr.StopAddons();
            }

            Log.Debug("Waiting for game to exit");
            await Task.Run(() => gameProcess.WaitForExit()).ConfigureAwait(false);
            Log.Verbose("Game has exited");

            if (addonMgr.IsRunning)
                addonMgr.StopAddons();

            try
            {
                if (App.Steam.IsValid)
                {
                    App.Steam.Shutdown();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not shut down Steam");
            }

            return gameProcess;
        }

        public void OnWindowClosed(object sender, object args)
        {
            Application.Current.Shutdown();
        }

        public void OnWindowClosing(object sender, CancelEventArgs args)
        {
            if (IsLoggingIn)
                args.Cancel = true;
        }

        //private void PersistAccount(string username, string password)
        //{
        //    if (username.IsNullOrEmpty()) username = String.Empty;

        //    if (AccountManager.CurrentAccount != null && AccountManager.CurrentAccount.UserName.Equals(username) &&
        //        AccountManager.CurrentAccount.Password != password &&
        //        AccountManager.CurrentAccount.AutoLogin)
        //        AccountManager.UpdatePassword(AccountManager.CurrentAccount, password);

        //    if (AccountManager.CurrentAccount != null && AccountManager.CurrentAccount.UserName.Equals(username))
        //        AccountManager.CurrentAccount.AreaID = Area.Areaid;

        //    if (AccountManager.CurrentAccount == null ||
        //        AccountManager.CurrentAccount.Id != $"{username}-{IsOtp}-{IsSteam}")
        //        try
        //        {
        //            if (AccountManager.CurrentAccount != null && AccountManager.CurrentAccount.UserName.Equals(username, StringComparison.Ordinal) &&
        //                AccountManager.CurrentAccount.Password != password &&
        //                AccountManager.CurrentAccount.SavePassword)
        //                AccountManager.UpdatePassword(AccountManager.CurrentAccount, password);

        //            if (AccountManager.CurrentAccount == null ||
        //                AccountManager.CurrentAccount.Id != $"{username}-{IsOtp}-{IsSteam}")
        //            {
        //                var accountToSave = new XivAccount(username)
        //                {
        //                    Password = password,
        //                    SavePassword = true,
        //                    //UseOtp = IsOtp,
        //                    //UseSteamServiceAccount = IsSteam,
        //                    AreaName = Area.AreaName
        //                };

        //                AccountManager.AddAccount(accountToSave);

        //                AccountManager.CurrentAccount = accountToSave;
        //            }
        //        }
        //        catch (Win32Exception ex)
        //        {
        //            CustomMessageBox.Builder
        //                            .NewFrom(Loc.Localize("PersistAccountError",
        //                                                  "XIVLauncher could not save your account information. This is likely caused by having too many saved accounts in the Windows Credential Manager.\nPlease try removing some of them."))
        //                            .WithAppendDescription(ex.ToString())
        //                            .WithShowHelpLinks()
        //                            .WithImage(MessageBoxImage.Warning)
        //                            .WithButtons(MessageBoxButton.OK)
        //                            .WithParentWindow(_window)
        //                            .Show();
        //        }
        //}

        private async Task<bool> HandleBootCheck()
        {
            try
            {
                if (App.Settings.PatchPath is { Exists: false })
                {
                    App.Settings.PatchPath = null;
                }

                App.Settings.PatchPath ??= new DirectoryInfo(Path.Combine(Paths.RoamingPath, "patches"));
                //PatchListEntry[] bootPatches = null;
                //try
                //{
                //    bootPatches = await this.Launcher.CheckBootVersion(App.Settings.GamePath).ConfigureAwait(false);
                //}
                //catch (Exception ex)
                //{
                //    Log.Error(ex, "Unable to check boot version.");
                //    CustomMessageBox.Show(Loc.Localize("CheckBootVersionError", "XIVLauncher was not able to check the boot version for the select game installation. This can happen if a maintenance is currently in progress or if your connection to the version check server is not available. Please report this error if you are able to login with the official launcher, but not XIVLauncher."), "XIVLauncher", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);

                //    return false;
                //}

                //if (bootPatches == null)
                //    return true;

                //return await TryHandlePatchAsync(Repository.Boot, bootPatches, null).ConfigureAwait(false);
                return true;
                // Debug.Assert(bootPatches != null);
                // if (bootPatches.Length == 0)
                //     return true;

                // return await TryHandlePatchAsync(Repository.Boot, bootPatches, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CustomMessageBox.Builder
                    .NewFrom(ex, nameof(HandleBootCheck))
                    .WithAppendText("\n\n")
                    .WithAppendText(Loc.Localize("BootPatchFailure", "Could not patch boot."))
                    .WithParentWindow(_window)
                    .Show();
                Environment.Exit(0);

                return false;
            }
        }

        private async Task<bool> TryHandlePatchAsync(Repository repository, PatchListEntry[] pendingPatches, string sid)
        {
            using var mutex = new Mutex(false, "XivLauncherIsPatching");

            if (!mutex.WaitOne(0, false))
            {
                CustomMessageBox.Show(Loc.Localize("PatcherAlreadyInProgress", "XIVLauncher is already patching your game in another instance. Please check if XIVLauncher is still open."), "XIVLauncherCN", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                Environment.Exit(0);
                return false; // This line will not be run.
            }

            if (GameHelpers.CheckIsGameOpen())
            {
                while (GameHelpers.CheckIsGameOpen())
                {
                    if (CustomMessageBox
                        .Builder
                        .NewFrom(Loc.Localize("GameIsOpenError",
                            "The game and/or the official launcher are open. XIVLauncher cannot patch the game if this is the case.\nPlease close the official launcher and try again."))
                        .WithImage(MessageBoxImage.Exclamation)
                        .WithButtons(MessageBoxButton.OKCancel)
                        .WithOkButtonText(Loc.Localize("Refresh", "_Refresh"))
                        .WithDefaultResult(MessageBoxResult.OK)
                        .Show() == MessageBoxResult.Cancel)
                    {
                        return false;
                    }
                }
            }

            if (!AppUtil.TryYellOnGameFilesBeingOpen(_window, n => n switch
                {
                    1 => Loc.Localize("GameUpdateExitRequired1",
                        "Close the following application to patch the game."),
                    _ => string.Format(Loc.Localize("GameUpdateExitRequiredPlural",
                        "Close the following applications to patch the game.")),
                }))
                return false;

            using var installer = new Common.Game.Patch.PatchInstaller(App.Settings.KeepPatches ?? false);
            var patcher = new PatchManager(App.Settings.PatchAcquisitionMethod ?? AcquisitionMethod.Aria, App.Settings.SpeedLimitBytes,
                repository, pendingPatches, App.Settings.GamePath, App.Settings.PatchPath, installer, this.Launcher, sid);
            patcher.OnFail += this.PatcherOnFail;
            installer.OnFail += this.InstallerOnFail;

            Hide();

            PatchDownloadDialog progressDialog = _window.Dispatcher.Invoke(() =>
            {
                var d = new PatchDownloadDialog(patcher);
                if (_window.IsVisible)
                    d.Owner = _window;
                d.Show();
                d.Activate();
                return d;
            });

            try
            {
                await patcher.PatchAsync(new FileInfo(Path.Combine(Paths.RoamingPath, "aria2.log"))).ConfigureAwait(false);
                return true;
            }
            catch (PatchInstallerException ex)
            {
                var message = Loc.Localize("PatchManNoInstaller",
                    "The patch installer could not start correctly.\n{0}\n\nIf you have denied access to it, please try again. If this issue persists, please contact us via Discord.");

                CustomMessageBox.Show(string.Format(message, ex.Message), "XIVLauncher Error", MessageBoxButton.OK,
                    MessageBoxImage.Error, parentWindow: _window);
            }
            catch (NotEnoughSpaceException sex)
            {
                var bytesRequired = ApiHelpers.BytesToString(sex.BytesRequired);
                var bytesFree = ApiHelpers.BytesToString(sex.BytesFree);

                switch (sex.Kind)
                {
                    case NotEnoughSpaceException.SpaceKind.Patches:
                        CustomMessageBox.Show(string.Format(Loc.Localize("FreeSpaceError", "There is not enough space on your drive to download patches.\n\nYou can change the location patches are downloaded to in the settings.\n\nRequired:{0}\nFree:{1}"), bytesRequired, bytesFree), "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                        break;

                    case NotEnoughSpaceException.SpaceKind.AllPatches:
                        CustomMessageBox.Show(string.Format(Loc.Localize("FreeSpaceErrorAll", "There is not enough space on your drive to download all patches.\n\nYou can change the location patches are downloaded to in the XIVLauncher settings.\n\nRequired:{0}\nFree:{1}"), bytesRequired, bytesFree), "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                        break;

                    case NotEnoughSpaceException.SpaceKind.Game:
                        CustomMessageBox.Show(string.Format(Loc.Localize("FreeSpaceGameError", "There is not enough space on your drive to install patches.\n\nYou can change the location the game is installed to in the settings.\n\nRequired:{0}\nFree:{1}"), bytesRequired, bytesFree), "XIVLauncher Error", MessageBoxButton.OK, MessageBoxImage.Error, parentWindow: _window);
                        break;

                    default:
                        Debug.Assert(false, "HandlePatchAsync:Invalid NotEnoughSpaceException.SpaceKind value.");
                        break;
                }
            }
            catch (Exception ex)
            {
                CustomMessageBox.Builder.NewFromUnexpectedException(ex, "HandlePatchAsync")
                                .WithParentWindow(_window)
                                .Show();
            }
            finally
            {
                progressDialog.Dispatcher.Invoke(() =>
                {
                    progressDialog.Hide();
                    progressDialog.Close();
                });
            }

            return false;
        }

        #region Commands

        public ICommand StartLoginCommand { get; set; }

        public ICommand LoginNoStartCommand { get; set; }

        public ICommand LoginNoDalamudCommand { get; set; }

        public ICommand LoginNoPluginsCommand { get; set; }

        public ICommand LoginNoThirdCommand { get; set; }

        public ICommand LoginRepairCommand { get; set; }

        public ICommand LoginCancelCommand { get; set; }

        public ICommand LoginForceQRCommand { get; set; }

        public ICommand InjectGameCommand { get; set; }

        #endregion

        #region Bindings

        private bool _isAutoLogin;
        public bool IsAutoLogin
        {
            get => _isAutoLogin;
            set
            {
                _isAutoLogin = value;
                OnPropertyChanged(nameof(IsAutoLogin));
            }
        }

        private bool _isFastLogin;
        public bool IsFastLogin
        {
            get => _isFastLogin;
            set
            {
                _isFastLogin = value;
                OnPropertyChanged(nameof(IsFastLogin));
            }
        }

        private bool _isReadWegameInfo;
        public bool IsReadWegameInfo
        {
            get => _isReadWegameInfo;
            set
            {
                _isReadWegameInfo = value;
                OnPropertyChanged(nameof(IsReadWegameInfo));
            }
        }

        private bool _isOtp;
        public bool IsOtp
        {
            get => _isOtp;
            set
            {
                _isOtp = value;
                OnPropertyChanged(nameof(IsOtp));
            }
        }

        private bool _isSteam;
        public bool IsSteam
        {
            get => _isSteam;
            set
            {
                _isSteam = value;
                OnPropertyChanged(nameof(IsSteam));
            }
        }

        private string _username;
        public string Username
        {
            get => _username;
            set
            {
                _username = value;
                OnPropertyChanged(nameof(Username));
            }
        }

        private GuiLoginType _guiLoginType;
        public GuiLoginType GuiLoginType
        {
            get => _guiLoginType;
            set
            {
                _guiLoginType = value;
                OnPropertyChanged(nameof(GuiLoginType));
            }
        }

        private SdoArea _area;
        public SdoArea Area
        {
            get => _area;
            set
            {
                _area = value;
                Log.Debug($"Area Change:{_area} -> {value}");
                OnPropertyChanged(nameof(Area));
            }
        }

        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                OnPropertyChanged(nameof(IsEnabled));
            }
        }

        private int _loginCardTransitionerIndex;
        public int LoginCardTransitionerIndex
        {
            get => _loginCardTransitionerIndex;
            set
            {
                _loginCardTransitionerIndex = value;
                OnPropertyChanged(nameof(LoginCardTransitionerIndex));
            }
        }

        private bool _isLoadingDialogOpen;
        public bool IsLoadingDialogOpen
        {
            get => _isLoadingDialogOpen;
            set
            {
                _isLoadingDialogOpen = value;
                OnPropertyChanged(nameof(IsLoadingDialogOpen));
            }
        }

        private Visibility _loadingDialogCancelButtonVisibility;
        public Visibility LoadingDialogCancelButtonVisibility
        {
            get => _loadingDialogCancelButtonVisibility;
            set
            {
                _loadingDialogCancelButtonVisibility = value;
                OnPropertyChanged(nameof(LoadingDialogCancelButtonVisibility));
            }
        }

        private string _loadingDialogMessage;
        public string LoadingDialogMessage
        {
            get => _loadingDialogMessage;
            set
            {
                _loadingDialogMessage = value;
                OnPropertyChanged(nameof(LoadingDialogMessage));
            }
        }

        private string _loginMessage;
        public string LoginMessage
        {
            get => _loginMessage;
            set
            {
                _loginMessage = value;
                OnPropertyChanged(nameof(LoginMessage));
            }
        }

        private SolidColorBrush _worldStatusIconColor;
        public SolidColorBrush WorldStatusIconColor
        {
            get => _worldStatusIconColor;
            set
            {
                _worldStatusIconColor = value;
                OnPropertyChanged(nameof(WorldStatusIconColor));
            }
        }

        private BitmapImage _qrCodeBitmapImage;
        public BitmapImage QrCodeBitmapImage
        {
            get => _qrCodeBitmapImage;
            set
            {
                _qrCodeBitmapImage = value;
                OnPropertyChanged(nameof(QrCodeBitmapImage));
            }
        }

        #endregion

        #region Localization

        private void SetupLoc()
        {
            LoginUsernameLoc = Loc.Localize("LoginBoxUsername", "Username");
            LoginPasswordLoc = Loc.Localize("LoginBoxPassword", "Password");
            AutoLoginLoc = Loc.Localize("LoginBoxAutoLogin", "Log in automatically");
            OtpLoc = Loc.Localize("LoginBoxOtp", "Use One-Time-Passwords");
            SteamLoc = Loc.Localize("LoginBoxSteam", "Use Steam service account");
            LoginLoc = Loc.Localize("LoginBoxLogin", "Log in");
            LoginNoStartLoc = Loc.Localize("LoginBoxNoStartLogin", "Update without starting");
            LoginRepairLoc = Loc.Localize("LoginBoxRepairLogin", "Repair game files");
            LoginTooltipLoc = Loc.Localize("LoginBoxLoginTooltip", "Log in with the provided credentials");
            LoginNoDalamudLoc = Loc.Localize("LoginBoxNoDalamudLogin", "Start w/o Dalamud");
            LoginNoPluginsLoc = Loc.Localize("LoginBoxNoPluginLogin", "Start w/o any Plugins");
            LoginNoThirdLoc = Loc.Localize("LoginBoxNoThirdLogin", "Start w/o Custom Repo Plugins");
            LoginTooltipLoc = Loc.Localize("LoginBoxLoginTooltip", "Log in with the provided credentials");
            LaunchOptionsLoc = Loc.Localize("LoginBoxLaunchOptions", "Additional launch options");
            WaitingForMaintenanceLoc = Loc.Localize("LoginBoxWaitingForMaint", "Waiting for maintenance to be over...");
            CancelWithShortcutLoc = Loc.Localize("CancelWithShortcut", "_Cancel");
            OpenAccountSwitcherLoc = Loc.Localize("OpenAccountSwitcher", "Open Account Switcher");
            SettingsLoc = Loc.Localize("Settings", "Settings");
            WorldStatusLoc = Loc.Localize("WorldStatus", "World Status");
            MaintenanceQueue = Loc.Localize("MaintenanceQueue", "Wait for maintenance to be over");
            IsLoggingInLoc = Loc.Localize("LoadingDialogIsLoggingIn", "Logging in...");
        }

        public string LoginUsernameLoc { get; private set; }
        public string LoginPasswordLoc { get; private set; }
        public string AutoLoginLoc { get; private set; }
        public string OtpLoc { get; private set; }
        public string SteamLoc { get; private set; }
        public string LoginLoc { get; private set; }
        public string LoginNoStartLoc { get; private set; }
        public string LoginNoDalamudLoc { get; private set; }
        public string LoginNoPluginsLoc { get; private set; }
        public string LoginNoThirdLoc { get; private set; }
        public string LoginRepairLoc { get; private set; }
        public string WaitingForMaintenanceLoc { get; private set; }
        public string CancelWithShortcutLoc { get; private set; }
        public string LoginTooltipLoc { get; private set; }
        public string LaunchOptionsLoc { get; private set; }
        public string OpenAccountSwitcherLoc { get; private set; }
        public string SettingsLoc { get; private set; }
        public string WorldStatusLoc { get; private set; }
        public string MaintenanceQueue { get; private set; }
        public string IsLoggingInLoc { get; private set; }

        #endregion

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            var handler = PropertyChanged;
            handler?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
