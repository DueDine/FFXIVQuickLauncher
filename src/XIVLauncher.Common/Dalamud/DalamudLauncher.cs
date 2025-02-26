﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Serilog;
using XIVLauncher.Common.PlatformAbstractions;

namespace XIVLauncher.Common.Dalamud
{
    public class DalamudLauncher
    {
        private readonly DalamudLoadMethod loadMethod;
        private readonly DirectoryInfo gamePath;
        private readonly DirectoryInfo configDirectory;
        private readonly DirectoryInfo logPath;
        private readonly ClientLanguage language;
        private readonly IDalamudRunner runner;
        private readonly DalamudUpdater updater;
        private readonly int injectionDelay;
        private readonly bool fakeLogin;
        private readonly bool noPlugin;
        private readonly bool noThirdPlugin;
        private readonly string troubleshootingData;

        public enum DalamudInstallState
        {
            Ok,
            OutOfDate,
        }

        public DalamudLauncher(IDalamudRunner runner, DalamudUpdater updater, DalamudLoadMethod loadMethod, DirectoryInfo gamePath, DirectoryInfo configDirectory, DirectoryInfo logPath,
                               ClientLanguage clientLanguage, int injectionDelay, bool fakeLogin, bool noPlugin, bool noThirdPlugin, string troubleshootingData)
        {
            this.runner = runner;
            this.updater = updater;
            this.loadMethod = loadMethod;
            this.gamePath = gamePath;
            this.configDirectory = configDirectory;
            this.logPath = logPath;
            this.language = clientLanguage;
            this.injectionDelay = injectionDelay;
            this.fakeLogin = fakeLogin;
            this.noPlugin = noPlugin;
            this.noThirdPlugin = noThirdPlugin;
            this.troubleshootingData = troubleshootingData;
        }
        
        public DalamudInstallState HoldForUpdate(DirectoryInfo gamePath)
        {
            Log.Information("[HOOKS] DalamudLauncher::HoldForUpdate(gp:{0})", gamePath.FullName);

            if (this.updater.State != DalamudUpdater.DownloadState.Done)
                this.updater.ShowOverlay();

            while (this.updater.State != DalamudUpdater.DownloadState.Done)
            {
                if (this.updater.State == DalamudUpdater.DownloadState.NoIntegrity)
                {
                    this.updater.CloseOverlay();
                    throw new DalamudRunnerException("Dalamud 完整性检测或更新反复失败, 请检查你的本地网络环境", this.updater.EnsurementException?.InnerException);
                }

                Thread.Yield();
            }

            if (!this.updater.Runner.Exists)
                throw new DalamudRunnerException("Dalamud 本地注入文件不存在, 请重新启动 XIVLauncher 以开始完整性检测与下载流程");

            return DalamudInstallState.Ok;
        }

        public void Inject(int gamePid, bool safeMode = false)
        {
            Log.Information("[HOOKS] DalamudLauncher::Run(gp:{0}, cl:{1})", this.gamePath.FullName, this.language);

            var ingamePluginPath = Path.Combine(this.configDirectory.FullName, "installedPlugins");

            Directory.CreateDirectory(ingamePluginPath);

            var startInfo = new DalamudStartInfo
            {
                Language = language,
                PluginDirectory = ingamePluginPath,
                ConfigurationPath = DalamudSettings.GetConfigPath(this.configDirectory),
                LoggingPath = this.logPath.FullName,
                AssetDirectory = this.updater.AssetDirectory.FullName,
                GameVersion = Repository.Ffxiv.GetVer(gamePath),
                WorkingDirectory = this.updater.Runner.Directory?.FullName,
                DelayInitializeMs = this.injectionDelay,
                TroubleshootingPackData = this.troubleshootingData,
            };

            var launchArguments = new List<string>
            {
                "inject -v",
                $"{gamePid}",
                //$"--all --warn",
                //$"--game=\"{gamePath}\"",
                DalamudInjectorArgs.WorkingDirectory(startInfo.WorkingDirectory),
                DalamudInjectorArgs.ConfigurationPath(startInfo.ConfigurationPath),
                DalamudInjectorArgs.LoggingPath(startInfo.LoggingPath),
                DalamudInjectorArgs.PluginDirectory(startInfo.PluginDirectory),
                DalamudInjectorArgs.AssetDirectory(startInfo.AssetDirectory),
                DalamudInjectorArgs.ClientLanguage((int)startInfo.Language),
                DalamudInjectorArgs.DelayInitialize(startInfo.DelayInitializeMs),
                DalamudInjectorArgs.TsPackB64(Convert.ToBase64String(Encoding.UTF8.GetBytes(startInfo.TroubleshootingPackData))),
            };

            if (safeMode) launchArguments.Add("--no-plugin");

            var psi = new ProcessStartInfo(this.updater.Runner.FullName)
            {
                Arguments = string.Join(" ", launchArguments),
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            var dalamudProcess = Process.Start(psi);
            while (!dalamudProcess.StandardOutput.EndOfStream)
            {
                var line = dalamudProcess.StandardOutput.ReadLine();
                Log.Information(line);
            }
        }
        public Process Run(FileInfo gameExe, string gameArgs, IDictionary<string, string> environment)
        {
            Log.Information("[HOOKS] DalamudLauncher::Run(gp:{0}, cl:{1})", this.gamePath.FullName, this.language);

            var ingamePluginPath = Path.Combine(this.configDirectory.FullName, "installedPlugins");

            Directory.CreateDirectory(ingamePluginPath);

            var startInfo = new DalamudStartInfo
            {
                Language = language,
                PluginDirectory = ingamePluginPath,
                ConfigurationPath = DalamudSettings.GetConfigPath(this.configDirectory),
                LoggingPath = this.logPath.FullName,
                AssetDirectory = this.updater.AssetDirectory.FullName,
                GameVersion = Repository.Ffxiv.GetVer(gamePath),
                WorkingDirectory = this.updater.Runner.Directory?.FullName,
                DelayInitializeMs = this.injectionDelay,
                TroubleshootingPackData = this.troubleshootingData,
            };

            if (this.loadMethod != DalamudLoadMethod.ACLonly)
                Log.Information("[HOOKS] DelayInitializeMs: {0}", startInfo.DelayInitializeMs);

            switch (this.loadMethod)
            {
                case DalamudLoadMethod.EntryPoint:
                    Log.Verbose("[HOOKS] Now running OEP rewrite");
                    break;

                case DalamudLoadMethod.DllInject:
                    Log.Verbose("[HOOKS] Now running DLL inject");
                    break;

                case DalamudLoadMethod.ACLonly:
                    Log.Verbose("[HOOKS] Now running ACL-only fix without injection");
                    break;
            }

            var process = this.runner.Run(this.updater.Runner, this.fakeLogin, this.noPlugin, this.noThirdPlugin, gameExe, gameArgs, environment, this.loadMethod, startInfo);

            this.updater.CloseOverlay();

            if (this.loadMethod != DalamudLoadMethod.ACLonly)
                Log.Information("[HOOKS] Started dalamud!");

            return process;
        }

        // always return true
        public static bool CanRunDalamud(DirectoryInfo gamePath) => true;
    }
}
