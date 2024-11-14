using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Castle.Core.Internal;
using CheapLoc;
using Serilog;
using XIVLauncher.Accounts;
using XIVLauncher.Common;
using XIVLauncher.Common.Dalamud;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Game.Patch.Acquisition;
using XIVLauncher.Common.Windows;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using XIVLauncher.Xaml;
using Timer = System.Timers.Timer;

namespace XIVLauncher.Windows
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Timer _bannerChangeTimer;
        private Headlines _headlines;
        private IReadOnlyList<Banner> _banners;
        private BitmapImage[] _bannerBitmaps;
        private int _currentBannerIndex;
        private bool _everShown = false;

        private SdoArea[] _sdoAreas;
        class BannerDotInfo
        {
            public bool Active { get; set; }
            public int Index { get; set; }
        }

        private List<int> oldPidList = new();

        private ObservableCollection<BannerDotInfo> _bannerDotList;

        private Timer _maintenanceQueueTimer;

        private AccountManager _accountManager;

        private MainWindowViewModel Model => this.DataContext as MainWindowViewModel;
        private readonly Launcher _launcher;

        public MainWindow()
        {
            InitializeComponent();

            this.DataContext = new MainWindowViewModel(this);
            _launcher = Model.Launcher;

            Closed += Model.OnWindowClosed;
            Closing += Model.OnWindowClosing;

            Model.LoginCardTransitionerIndex = 1;

            Model.Activate += () => this.Dispatcher.Invoke(() =>
            {
                this.Show();
                this.Activate();
                this.Focus();
            });

            Model.Hide += () => this.Dispatcher.Invoke(() =>
            {
                this.Hide();
            });

            Model.ReloadHeadlines += () => Task.Run(SetupHeadlines);

            NewsListView.ItemsSource = new List<News>
            {
                new News
                {
                    Title = Loc.Localize("NewsLoading", "Loading..."),
                    Tag = "DlError"
                }
            };

#if !XL_NOAUTOUPDATE
            Title += " v" + AppUtil.GetAssemblyVersion();
#else
            Title += " " + AppUtil.GetGitHash();
#endif

#if !XL_NOAUTOUPDATE
            if (EnvironmentSettings.IsDisableUpdates)
#endif
            {
                Title += " - UNSUPPORTED VERSION - NO UPDATES - COULD DO BAD THINGS";
            }

#if DEBUG
            Title += " - Debugging";
#endif

            if (EnvironmentSettings.IsWine)
                Title += " - Wine on Linux";

            if (App.Settings.LauncherLanguage == LauncherLanguage.Russian)
            {
                AccountSwitcherButton.Background = App.UaBrush;
                AccountSwitcherButton.BorderBrush = App.UaBrush;
            }
        }

        private async Task SetupServers()
        {
            try
            {
                _sdoAreas = await SdoArea.Get();
            }
            catch (Exception ex)
            {
                _sdoAreas = new SdoArea[1] { new SdoArea { AreaName = "获取大区失败", Areaid = "-1" } };
                throw ex;
            }
            finally
            {
                Dispatcher.BeginInvoke(new Action(async () =>
                {
                    ServerSelection.ItemsSource = _sdoAreas;
                    ServerSelection.SelectedIndex = App.Settings.SelectedServer.GetValueOrDefault(0);
                }));
            }
        }

        private async Task SetupHeadlines()
        {
            try
            {
                _bannerChangeTimer?.Stop();

                //await Headlines.GetWorlds(_launcher, App.Settings.Language.GetValueOrDefault(ClientLanguage.English));
                //_banners = await Headlines.GetBanners(_launcher, App.Settings.Language.GetValueOrDefault(ClientLanguage.English), App.Settings.ForceNorthAmerica.GetValueOrDefault(false))
                //                          .ConfigureAwait(false);
                //await Headlines.GetMessage(_launcher, App.Settings.Language.GetValueOrDefault(ClientLanguage.English), App.Settings.ForceNorthAmerica.GetValueOrDefault(false))
                //               .ConfigureAwait(false);
                _headlines = await Headlines.GetNews(_launcher, App.Settings.Language.GetValueOrDefault(ClientLanguage.ChineseSimplified), App.Settings.ForceNorthAmerica.GetValueOrDefault(false))
                                            .ConfigureAwait(false);
                _banners = this._headlines.Banner;

                _bannerBitmaps = new BitmapImage[_banners.Count];
                _bannerDotList = new();

                for (var i = 0; i < _banners.Count; i++)
                {
                    var imageBytes = await _launcher.DownloadAsLauncher(_banners[i].LsbBanner.ToString(), App.Settings.Language.GetValueOrDefault(ClientLanguage.ChineseSimplified));

                    using var stream = new MemoryStream(imageBytes);

                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = stream;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    _bannerBitmaps[i] = bitmapImage;
                    _bannerDotList.Add(new() { Index = i });
                }

                _bannerDotList[0].Active = true;

                _ = this.Dispatcher.BeginInvoke(new Action(() =>
                {
                    this.BannerImage.Source = this._bannerBitmaps[0];
                    this.BannerDot.ItemsSource = this._bannerDotList;
                }));

                _bannerChangeTimer = new Timer { Interval = 5000 };

                _bannerChangeTimer.Elapsed += (o, args) =>
                {
                    _bannerDotList.ToList().ForEach(x => x.Active = false);

                    if (_currentBannerIndex + 1 > _banners.Count - 1)
                        _currentBannerIndex = 0;
                    else
                        _currentBannerIndex++;

                    _bannerDotList[_currentBannerIndex].Active = true;

                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        BannerImage.Source = _bannerBitmaps[_currentBannerIndex];
                        BannerDot.ItemsSource = _bannerDotList.ToList();
                    }));
                };

                _bannerChangeTimer.AutoReset = true;
                _bannerChangeTimer.Start();

                _ = Dispatcher.BeginInvoke(new Action(() => { NewsListView.ItemsSource = _headlines.News; }));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not get news");
                _ = Dispatcher.BeginInvoke(new Action(() =>
                {
                    NewsListView.ItemsSource = new List<News> { new News { Title = Loc.Localize("NewsDlFailed", "Could not download news data."), Tag = "DlError" } };
                }));
            }
        }

        private void SetupInjector()
        {
            try
            {
                var startInfo = new DalamudStartInfo
                {
                    ConfigurationPath = DalamudSettings.GetConfigPath(new DirectoryInfo(Paths.RoamingPath)),
                    LoggingPath = Paths.RoamingPath,
                    PluginDirectory = Path.Combine(Paths.RoamingPath, "installedPlugins"),
                    Language = ClientLanguage.ChineseSimplified,
                    DelayInitializeMs = (int)App.Settings.DalamudInjectionDelayMs,
                    GameVersion = Repository.Ffxiv.GetVer(App.Settings.GamePath)
                };

                Task.Run(() =>
                {
                    var first = true;

                    while (true)
                    {
                        Thread.Sleep(1000);

                        if (!App.Settings.EnableInjector) continue;
                        if (App.DalamudUpdater?.Runner is null) continue;

                        if (App.DalamudUpdater.State is not DalamudUpdater.DownloadState.Done)
                        {
                            App.DalamudUpdater.ShowOverlay();
                            continue;
                        }

                        App.DalamudUpdater.CloseOverlay();
                        var workingDirectory = App.DalamudUpdater.Runner.Directory?.FullName;
                        startInfo.WorkingDirectory = workingDirectory;
                        startInfo.AssetDirectory = App.DalamudUpdater.AssetDirectory.FullName;

                        var newPidList = GetGameProcess();

                        var newHash = string.Join(", ", newPidList).GetHashCode();
                        var oldHash = string.Join(", ", oldPidList).GetHashCode();

                        if (oldHash != newHash)
                        {
                            if (newPidList.Except(oldPidList).Any())
                            {
                                foreach (var pid in newPidList.Except(oldPidList))
                                {
                                    Log.Information($"Detected new game pid: {pid}");

                                    if (first)
                                    {
                                        first = false;
                                        var result = CustomMessageBox.Show($"检测到已经存在游戏进程{pid},即将自动注入,是否要注入?", "自动注入", MessageBoxButton.YesNo);
                                        if (result == MessageBoxResult.No) continue;
                                    }

                                    if (Process.GetProcessById(pid).MainModule?.FileName != Path.Combine(App.Settings.GamePath.FullName, "game", "ffxiv_dx11.exe"))
                                    {
                                        var result = CustomMessageBox.Show($"即将注入进程{pid},游戏路径与设置中的路径不符,是否注入?", "自动注入", MessageBoxButton.YesNo);
                                        if (result == MessageBoxResult.No) continue;
                                    }

                                    Log.Information("Start to inject game, pid = {pid}", pid);
                                    WindowsDalamudRunner.Inject(new FileInfo(Path.Combine(workingDirectory!, "Dalamud.Injector.exe")),
                                                                pid, new Dictionary<string, string>(), DalamudLoadMethod.DllInject, startInfo);
                                }
                            }

                            oldPidList = newPidList;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Setup Injector Error");
                throw;
            }
        }

        private static List<int> GetGameProcess()
        {
            return Process.GetProcesses().Where(process =>
            {
                if (process.ProcessName == "ffxiv_dx11")
                {
                    return !process.MainWindowTitle.Contains("FINAL FANTASY XIV"); //非国际服
                }

                return false;
            }).ToList().ConvertAll(process => process.Id).ToList();
        }

        private const int CURRENT_VERSION_LEVEL = 2;

        private void SetDefaults()
        {
            // Set the default patch acquisition method
            App.Settings.PatchAcquisitionMethod ??=
                EnvironmentSettings.IsWine ? AcquisitionMethod.NetDownloader : AcquisitionMethod.Aria;

            // Set the default Dalamud injection method
            App.Settings.InGameAddonLoadMethod ??= EnvironmentSettings.IsWine
                ? DalamudLoadMethod.DllInject
                : DalamudLoadMethod.EntryPoint;

            // Clean up invalid addons
            if (App.Settings.AddonList != null)
                App.Settings.AddonList = App.Settings.AddonList.Where(x => !string.IsNullOrEmpty(x.Addon.Path)).ToList();


            App.Settings.AskBeforePatchInstall ??= true;

            App.Settings.DpiAwareness ??= DpiAwareness.Unaware;

            App.Settings.TreatNonZeroExitCodeAsFailure ??= false;
            App.Settings.ExitLauncherAfterGameExit ??= true;

            App.Settings.IsFt = false;
            App.Settings.UniqueIdCacheEnabled = false;
            App.Settings.EncryptArguments = false;

            App.Settings.AutoStartSteam ??= false;

            App.Settings.ForceNorthAmerica ??= false;

            var versionLevel = App.Settings.VersionUpgradeLevel.GetValueOrDefault(0);

            while (versionLevel < CURRENT_VERSION_LEVEL)
            {
                switch (versionLevel)
                {
                    case 0:
                        // Check for RTSS & Special K injectors
                        try
                        {
                            var hasRtss = Process.GetProcesses().Any(x =>
                                x.ProcessName.ToLowerInvariant().Contains("rtss") ||
                                x.ProcessName.ToLowerInvariant().Contains("skifsvc64"));

                            if (hasRtss)
                            {
                                App.Settings.DalamudInjectionDelayMs = 4000;
                                Log.Information("RTSS/SpecialK detected, setting delay");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Could not check for RTSS/SpecialK");
                        }

                        break;

                    // 5.12.2022: Bad main window placement when using auto-launch
                    case 1:
                        App.Settings.MainWindowPlacement = null;
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                versionLevel++;
            }

            App.Settings.VersionUpgradeLevel = versionLevel;


        }

        public void Initialize()
        {
#if DEBUG
            var fakeStartMenuItem = new MenuItem
            {
                Header = "Fake start"
            };
            fakeStartMenuItem.Click += FakeStart_OnClick;

            LoginContextMenu.Items.Add(fakeStartMenuItem);
#endif

            this.SetDefaults();

            Model.IsFastLogin = App.Settings.FastLogin;
            LoginPassword.IsEnabled = LoginPassword.IsVisible;
            Model.EnableInjector = App.Settings.EnableInjector;

            _accountManager = new AccountManager(App.Settings);
            if (this._accountManager.CurrentAccount != null && !_accountManager.CurrentAccount.Password.IsNullOrEmpty()) ShowPassword_OnClick(null, null);

            var savedAccount = _accountManager.CurrentAccount;

            if (App.Settings.UniqueIdCacheEnabled && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                App.UniqueIdCache.Reset();
                Console.Beep(523, 150); // Feedback without popup
            }

            if (App.GlobalIsDisableAutologin)
            {
                Log.Information("Autologin was disabled globally, saving into settings...");
                App.Settings.AutologinEnabled = false;
            }

            if (App.Settings.AutologinEnabled && savedAccount != null && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                Log.Information("Engaging Autologin...");
                Model.TryLogin(savedAccount.UserName, savedAccount.Password,
                    savedAccount.UseOtp,
                    savedAccount.UseSteamServiceAccount, true, MainWindowViewModel.AfterLoginAction.Start);

                return;
            }
            else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || bool.Parse(Environment.GetEnvironmentVariable("XL_NOAUTOLOGIN") ?? "false"))
            {
                App.Settings.AutologinEnabled = false;
                AutoLoginCheckBox.IsChecked = false;
            }

            if (App.Settings.GamePath?.Exists != true)
            {
                var setup = new FirstTimeSetup();
                setup.ShowDialog();

                // If the user didn't reach the end of the setup, we should quit
                if (!setup.WasCompleted)
                {
                    Environment.Exit(0);
                    return;
                }

                SettingsControl.ReloadSettings();
            }

            Task.Run(async () =>
            {
                await SetupServers();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (savedAccount != null)
                        SwitchAccount(savedAccount, false);
                }));
                await SetupHeadlines();
                Troubleshooting.LogTroubleshooting();
            });

            this.Dispatcher.InvokeAsync(this.SetupInjector);

            Log.Information("MainWindow initialized.");

            Show();
            Activate();

            _everShown = true;
        }

        private void BannerCard_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines != null) Process.Start(new ProcessStartInfo(_banners[_currentBannerIndex].Link.ToString()) { UseShellExecute = true });
        }

        private void NewsListView_OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_headlines == null)
                return;

            if (!(NewsListView.SelectedItem is News item))
                return;

            if (!string.IsNullOrEmpty(item.Url))
            {
                Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
            }
            //else
            //{
            //    string url;

            //    switch (App.Settings.Language)
            //    {
            //        case ClientLanguage.Japanese:
            //            url = "https://jp.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        case ClientLanguage.English when GameHelpers.IsRegionNorthAmerica():
            //            url = "https://na.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        case ClientLanguage.English:
            //            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        case ClientLanguage.German:
            //            url = "https://de.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        case ClientLanguage.French:
            //            url = "https://fr.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        case ClientLanguage.ChineseSimplified:
            //            url = "https://na.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;

            //        default:
            //            url = "https://eu.finalfantasyxiv.com/lodestone/news/detail/";
            //            break;
            //    }

            //    Process.Start(url + item.Id);
            //}
        }

        private void WorldStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.Settings.Language == ClientLanguage.ChineseSimplified) Process.Start(new ProcessStartInfo("https://ff.web.sdo.com/web8/index.html#/servers") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("https://is.xivup.com/") { UseShellExecute = true });
        }

        private void QueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            if (_maintenanceQueueTimer == null)
                SetupMaintenanceQueueTimer();

            Model.LoadingDialogCancelButtonVisibility = Visibility.Visible;
            Model.LoadingDialogMessage = Model.WaitingForMaintenanceLoc;
            Model.IsLoadingDialogOpen = true;

            _maintenanceQueueTimer.Start();

            // Manually fire the first event, avoid waiting the first timer interval
            Task.Run(() =>
            {
                OnMaintenanceQueueTimerEvent(null, null);
            });
        }

        private void SetupMaintenanceQueueTimer()
        {
            // This is a good indicator that we should clear the UID cache
            App.UniqueIdCache.Reset();

            _maintenanceQueueTimer = new Timer
            {
                Interval = 20000
            };

            _maintenanceQueueTimer.Elapsed += OnMaintenanceQueueTimerEvent;
        }

        private async void OnMaintenanceQueueTimerEvent(Object source, System.Timers.ElapsedEventArgs e)
        {
            var bootPatches = await _launcher.CheckBootVersion(App.Settings.GamePath);

            var gateStatus = false;

            try
            {
                //gateStatus = Task.Run(() => _launcher.GetGateStatus(App.Settings.Language.GetValueOrDefault(ClientLanguage.English))).Result.Status;
            }
            catch
            {
                // ignored
            }

            var hasBootPatch = bootPatches.Length > 0;
            if (gateStatus || hasBootPatch)
            {
                if (hasBootPatch)
                {
                    CustomMessageBox.Show(Loc.Localize("MaintenanceQueueBootPatch",
                        "A patch for the official launcher was detected.\nThis usually means that there is a patch for the game as well.\n\nYou will now be logged in."), "XIVLauncherCN", parentWindow: this);
                }

                Dispatcher.Invoke(() =>
                {
                    QuitMaintenanceQueueButton_OnClick(null, null);

                    Model.TryLogin(Model.Username, LoginPassword.Password, Model.IsOtp, Model.IsSteam, false, MainWindowViewModel.AfterLoginAction.Start);
                });

                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 150);
                Thread.Sleep(25);
                Console.Beep(523, 300);
                Thread.Sleep(150);
                Console.Beep(415, 300);
                Thread.Sleep(150);
                Console.Beep(466, 300);
                Thread.Sleep(150);
                Console.Beep(523, 300);
                Thread.Sleep(25);
                Console.Beep(466, 150);
                Thread.Sleep(25);
                Console.Beep(523, 900);
            }
        }

        private void QuitMaintenanceQueueButton_OnClick(object sender, RoutedEventArgs e)
        {
            //_maintenanceQueueTimer.Stop();
            Model.EnableInjector = false;
        }

        private void Card_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
                return;

            if (Model.IsLoggingIn)
                return;

            Model.StartLoginCommand.Execute(null);
        }

        private void AccountSwitcherButton_OnClick(object sender, RoutedEventArgs e)
        {
            var switcher = new AccountSwitcher(_accountManager);

            var locationFromScreen = AccountSwitcherButton.PointToScreen(new Point(0, 0));
            var source = PresentationSource.FromVisual(this);

            if (source != null)
            {
                var targetPoints = source.CompositionTarget!.TransformFromDevice.Transform(locationFromScreen);

                switcher.WindowStartupLocation = WindowStartupLocation.Manual;
                switcher.Left = targetPoints.X - 15;
                switcher.Top = targetPoints.Y - 15;
            }

            switcher.OnAccountSwitchedEventHandler += OnAccountSwitchedEventHandler;

            switcher.Show();
        }

        private void OnAccountSwitchedEventHandler(object sender, XivAccount e)
        {
            SwitchAccount(e, true);
        }

        private void SwitchAccount(XivAccount account, bool saveAsCurrent)
        {
            Model.Username = account.UserName;
            Model.IsOtp = account.UseOtp;
            Model.IsSteam = account.UseSteamServiceAccount;
            Model.IsAutoLogin = App.Settings.AutologinEnabled;
            Model.Area = _sdoAreas.Where(x => x.Areaid == account.AreaID).FirstOrDefault();

            if (account.SavePassword)
                LoginPassword.Password = account.Password;

            if (saveAsCurrent)
            {
                _accountManager.CurrentAccount = account;
            }
        }

        private void SettingsControl_OnSettingsDismissed(object sender, EventArgs e)
        {
            Task.Run(SetupHeadlines);
        }

        private void FakeStart_OnClick(object sender, RoutedEventArgs e)
        {
            _ = Model.StartGameAndAddon(new Launcher.LoginResult
            {
                OauthLogin = new Launcher.OauthLoginResult
                {
                    MaxExpansion = 5,
                    Playable = true,
                    Region = 0,
                    SessionId = "0",
                    TermsAccepted = true
                },
                State = Launcher.LoginState.Ok,
                UniqueId = "0"
            }, false, false, false, false).ConfigureAwait(false);
        }

        private void LoginPassword_OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (this.DataContext != null)
                ((MainWindowViewModel)this.DataContext).Password = ((PasswordBox)sender).Password;
        }

        private void RadioButton_MouseEnter(object sender, MouseEventArgs e)
        {
            ((RadioButton)sender).IsChecked = true;
            _currentBannerIndex = _bannerDotList.FirstOrDefault(x => x.Active)?.Index ?? _currentBannerIndex;
            Dispatcher.BeginInvoke(new Action(() => BannerImage.Source = _bannerBitmaps[_currentBannerIndex]));

            _bannerChangeTimer.Stop();
        }

        private void RadioButton_MouseLeave(object sender, MouseEventArgs e)
        {
            _bannerChangeTimer.Start();
        }

        private void SettingsControl_OnCloseMainWindowGracefully(object sender, EventArgs e)
        {
            Close();
        }

        private void MainWindow_OnClosing(object sender, CancelEventArgs e)
        {
            if (!_everShown)
                return;

            try
            {
                PreserveWindowPosition.SaveWindowPosition(this);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't save window position");
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            try
            {
                PreserveWindowPosition.RestorePosition(this);

                // Restore the size of the window to what we expect it to be
                // There's no better way to do it that doesn't make me wanna off myself
                Width = 700;
                Height = 376;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't restore window position");
            }
        }

        private void ServerSelection_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (this.DataContext != null) 
                ((MainWindowViewModel)this.DataContext).Area = (SdoArea)((ComboBox)sender).SelectedItem;
            App.Settings.SelectedServer = ((ComboBox)sender).SelectedIndex;
        }

        private void LoginUsername_OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (this.DataContext != null)
                ((MainWindowViewModel)this.DataContext).Username = ((TextBox)sender).Text;
        }

        private void FastLoginCheckBox_OnClick(object sender, RoutedEventArgs e)
        {
            //if (Model.IsFastLogin)
            //{
            //    LoginPassword.Password = String.Empty;
            //}
            //else
            //{
            //    LoginPassword.Password = _accountManager.CurrentAccount?.Password;
            //}
        }

        private void ShowPassword_OnClick(object sender, RoutedEventArgs e)
        {
            if (LoginPassword.Visibility == Visibility.Collapsed)
            {
                LoginPassword.Visibility = Visibility.Visible;
                LoginPassword.IsEnabled = true;
                LoginPassword.Password = _accountManager.CurrentAccount?.Password;
            }
            else
            {
                LoginPassword.Visibility = Visibility.Collapsed;
                LoginPassword.Password = string.Empty;
                LoginPassword.IsEnabled = false;
            }
        }

        private void EnableInjector_OnClick(object sender, RoutedEventArgs e)
        {
            Model.EnableInjector = true;
            if (App.DalamudUpdater is not null) App.DalamudUpdater.ShowOverlay();
            Model.LoadingDialogCancelButtonVisibility = Visibility.Visible;
            Model.LoadingDialogMessage = "正在使用自动注入模式";
        }
    }
}
