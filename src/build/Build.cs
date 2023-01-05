﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Build.Evaluation;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Utilities.Collections;
using Serilog;
using static AppVersionTask;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static RestoreTask;
using Project = Nuke.Common.ProjectModel.Project;

#pragma warning disable IDE1006 // Naming Styles
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Publish);

    [Parameter("Configuration to build - Default is 'Release'")]
    readonly Configuration Configuration = Configuration.Release;

    [Parameter("The target platform")]
    readonly PlatformTarget[] PlatformTargets;

    [Parameter("Runs unit tests")]
    readonly bool RunTests;

    [Solution]
    readonly Solution Solution;

    Target PreliminaryCheck => _ => _
        .Before(Clean)
        .Executes(() =>
        {
            if (PlatformTargets == null || PlatformTargets.Length == 0)
            {
                Assert.Fail("Parameter `--platform-targets` is missing. Please check `build.sh --help`.");
            }

            if (PlatformTargets.Contains(PlatformTarget.WindowsUwp) && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 0, 0))
            {
                Assert.Fail("To build Windows UWP app, you need to run on Windows 10 or later.");
            }

            Log.Information("Preliminary checks are successful.");
        });

    Target Clean => _ => _
        .DependsOn(PreliminaryCheck)
        .Executes(() =>
        {
            if (!Debugger.IsAttached)
            {
                RootDirectory.GlobDirectories("bin", "obj", "publish").ForEach(DeleteDirectory);
            }
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(async () =>
        {
            if (!Debugger.IsAttached)
            {
                await RestoreDependenciesAsync(RootDirectory);
            }
        });

    Target SetVersion => _ => _
        .DependentFor(Publish)
        .After(Restore)
        .OnlyWhenDynamic(() => Configuration == Configuration.Release)
        .Executes(() =>
        {
            SetAppVersion(RootDirectory);
        });

#pragma warning disable IDE0051 // Remove unused private members
    Target UnitTests => _ => _
        .DependentFor(Publish)
        .After(Restore)
        .After(SetVersion)
        .OnlyWhenDynamic(() => RunTests)
        .Executes(() =>
        {
            RootDirectory
                .GlobFiles("**/*Tests.csproj")
                .ForEach(f =>
                    DotNetTest(s => s
                    .SetProjectFile(f)
                    .SetConfiguration(Configuration)
                    .SetVerbosity(DotNetVerbosity.Quiet)));
        });
#pragma warning restore IDE0051 // Remove unused private members

    Target Publish => _ => _
        .DependsOn(SetVersion)
        .DependsOn(Restore)
        .Executes(() =>
        {
            if (PlatformTargets.Contains(PlatformTarget.WindowsUwp))
            {
                RootDirectory
                    .GlobFiles("**/*.wapproj")
                    .ForEach(f =>
                    {
                        Log.Information($"Building {f}...");
                        MSBuild(s => s
                            .SetTargetPath(f)
                            .SetConfiguration(Configuration)
                            .SetTargets("Build")
                            .AddProperty("AppxBundlePlatforms", "x86|x64|arm64")
                            .AddProperty("AppxPackageDir", RootDirectory / "publish" / "MSIX")
                            .AddProperty("AppxPackageSigningEnabled", false)
                            .AddProperty("AppxSymbolPackageEnabled", true)
                            .AddProperty("AppxBundle", "Always")
                            .AddProperty("UapAppxPackageBuildMode", "StoreUpload")
                            .SetProcessArgumentConfigurator(_ => _.Add($"/bl:\"{RootDirectory / "bin" / "msbuild.binlog"}\""))
                            .SetVerbosity(MSBuildVerbosity.Quiet)
                            .SetMaxCpuCount(1)
                            .EnableRestore()
                            // This is dummy but necessary otherwise MSBuild tries to use Any CPU, which doesn't work for UWP.
                            .SetTargetPlatform(MSBuildTargetPlatform.x86));
                    });
            }

            if (PlatformTargets.Contains(PlatformTarget.CLI))
            {
                foreach (DotnetParameters dotnetParameters in GetDotnetParametersForCliApp())
                {
                    Log.Information($"Publishing {dotnetParameters.ProjectOrSolutionPath + "-" + dotnetParameters.TargetFramework + "-" + dotnetParameters.RuntimeIdentifier}...");
                    DotNetPublish(s => s
                        .SetProject(dotnetParameters.ProjectOrSolutionPath)
                        .SetConfiguration(Configuration)
                        .SetFramework(dotnetParameters.TargetFramework)
                        .SetRuntime(dotnetParameters.RuntimeIdentifier)
                        .SetSelfContained(dotnetParameters.Portable)
                        .SetPublishSingleFile(true)
                        .SetPublishReadyToRun(true)
                        .SetPublishTrimmed(dotnetParameters.Portable)
                        .SetVerbosity(DotNetVerbosity.Quiet)
                        .SetOutput(RootDirectory / "publish" / dotnetParameters.OutputPath));
                }
            }
        });

    IEnumerable<DotnetParameters> GetDotnetParametersForCliApp()
    {
        string publishProject = "DevToys.CLI";
        Project project = Solution.GetProject(publishProject);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                // TODO: I believe that on macOS, PublishTrimmed may be mandatory?
                yield return new DotnetParameters(project.Path, "osx-x64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "osx-x64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "osx-arm64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "osx-arm64", targetFramework, portable: true);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (string targetFramework in project.GetTargetFrameworks())
            {
                yield return new DotnetParameters(project.Path, "win10-x64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win10-x64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "win10-arm64", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win10-arm64", targetFramework, portable: true);

                yield return new DotnetParameters(project.Path, "win10-x86", targetFramework, portable: false);
                yield return new DotnetParameters(project.Path, "win10-x86", targetFramework, portable: true);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // TODO
        }
    }
#pragma warning restore IDE1006 // Naming Styles
}