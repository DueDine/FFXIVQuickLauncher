﻿using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;
using XIVLauncher.Common;
using XIVLauncher.PatchInstaller.Commands;

namespace XIVLauncher.PatchInstaller;

public static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Log.Logger = new LoggerConfiguration()
                     .WriteTo.Console(standardErrorFromLevel: LogEventLevel.Fatal)
                     .WriteTo.File(Path.Combine(Paths.RoamingPath, "patcher.log"))
                     .WriteTo.Debug()
                     .MinimumLevel.Verbose()
                     .CreateLogger();

        var rc = new RootCommand();
        rc.AddCommand(CheckIntegrityCommand.COMMAND);
        rc.AddCommand(InstallCommand.COMMAND);
        rc.AddCommand(IndexCreateCommand.COMMAND);
        rc.AddCommand(IndexCreateIntegrityCommand.COMMAND);
        rc.AddCommand(IndexVerifyCommand.COMMAND);
        rc.AddCommand(IndexRepairCommand.COMMAND);
        rc.AddCommand(IndexUpdateCommand.COMMAND);
        rc.AddCommand(IndexRpcCommand.COMMAND);
        rc.AddCommand(IndexRpcTestCommand.COMMAND);
        rc.AddCommand(RpcCommand.COMMAND);

        var ret = -1;

        try
        {
            ret = await rc.InvokeAsync(args);
            Log.Information("Operation complete.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Operation failed.");
        }

        return ret;
    }
}
