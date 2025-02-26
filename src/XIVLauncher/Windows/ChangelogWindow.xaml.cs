using System;
using System.Diagnostics;
using System.Media;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json;
using Serilog;
using XIVLauncher.Common;
using XIVLauncher.Common.Util;
using XIVLauncher.Support;
using XIVLauncher.Windows.ViewModel;
using HttpUtility = System.Web.HttpUtility;

namespace XIVLauncher.Windows
{
    /// <summary>
    /// Interaction logic for ErrorWindow.xaml
    /// </summary>
    public partial class ChangelogWindow : Window
    {
        public class VersionMeta
        {
            [JsonProperty("version")]
            public string Version { get; set; }

            [JsonProperty("url")]
            public string Url { get; set; }

            [JsonProperty("changelog")]
            public string Changelog { get; set; }

            [JsonProperty("when")]
            public DateTime When { get; set; }
        }

        public class ReleaseMeta
        {
            [JsonProperty("releaseVersion")]
            public VersionMeta ReleaseVersion { get; set; }

            [JsonProperty("prereleaseVersion")]
            public VersionMeta PrereleaseVersion { get; set; }
        }

        private ChangeLogWindowViewModel Model => this.DataContext as ChangeLogWindowViewModel;

        public ChangelogWindow()
        {
            InitializeComponent();

            this.DiscordButton.Click += SupportLinks.OpenDiscordChannel;

            var vm = new ChangeLogWindowViewModel();
            DataContext = vm;

            this.ChangeLogText.Text = vm.ChangelogLoadingLoc;

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        public void UpdateVersion(string version)
        {
            UpdateNotice.Text = string.Format(Model.UpdateNoticeLoc, version);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public new void Show()
        {
            SystemSounds.Asterisk.Play();
            base.Show();
        }
    }
}
