using System.Security.Cryptography;
using System.Reflection;
using System.Text;
using MacroCore.Runtime;
using MacroLauncher;
using MacroPlayer;
using MacroRecorder.Services;

namespace MacroCore.Tests;

public sealed class ProjectSandboxAndSecurityGateTests
{
    private static string Root => TestProjectEnvironment.Root;
    private static string Dev => TestProjectEnvironment.DevelopmentRoot;
    private static ProjectSandboxPaths Paths => ProjectSandboxPaths.Create(Root, "security-gate-tests");

    [Fact] public void ProjectRootResolved() => Assert.Equal(Path.GetFullPath(Root).TrimEnd('\\'), Paths.ProjectRoot);
    [Fact] public void DevelopmentRootResolved() => Assert.Equal(Path.GetFullPath(Dev), Paths.DevelopmentRoot);
    [Fact] public void RunRootInsideProject() => Assert.True(ProjectSandboxGuard.IsWithin(Root, Paths.RunRoot));
    [Fact] public void TempInsideRun() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.RunRoot, Paths.Temp));
    [Fact] public void TmpInsideRunEnvironment() => Assert.Equal(Paths.Temp, Paths.ChildEnvironment["TMP"]);
    [Fact] public void TmpDirInsideRunEnvironment() => Assert.Equal(Paths.Temp, Paths.ChildEnvironment["TMPDIR"]);
    [Fact] public void UserProfileInsideBuildProfile() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.BuildProfileRoot, Paths.UserProfile));
    [Fact] public void AppDataInsideBuildProfile() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.BuildProfileRoot, Paths.AppData));
    [Fact] public void LocalAppDataInsideBuildProfile() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.BuildProfileRoot, Paths.LocalAppData));
    [Fact] public void DotNetCliHomeInsideBuildProfile() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.BuildProfileRoot, Paths.DotNetCliHome));
    [Fact] public void NugetPackagesInsideDevelopment() => Assert.True(ProjectSandboxGuard.IsWithin(Dev, Paths.NuGetPackages));
    [Fact] public void NugetHttpCacheInsideDevelopment() => Assert.True(ProjectSandboxGuard.IsWithin(Dev, Paths.NuGetHttpCache));
    [Fact] public void TestResultsInsideDevelopment() => Assert.True(ProjectSandboxGuard.IsWithin(Dev, Paths.TestResults));
    [Fact] public void SimulatedDesktopInsideRun() => Assert.True(ProjectSandboxGuard.IsWithin(Paths.RunRoot, Paths.SimulatedDesktop));
    [Fact] public void RejectParentTraversal() => Assert.Throws<InvalidOperationException>(() => ProjectSandboxGuard.EnsureAllowed(Paths, Path.Combine(Paths.RunRoot, "..", "escape")));
    [Fact] public void RejectUncPath() => Assert.Throws<InvalidOperationException>(() => ProjectSandboxGuard.EnsureAllowed(Paths, @"\\server\share\escape"));
    [Fact] public void RejectOutsideProject() => Assert.Throws<InvalidOperationException>(() => ProjectSandboxGuard.EnsureAllowed(Paths, @"C:\outside\escape"));
    [Fact] public void AllowProgramState() => Assert.Equal(Path.GetFullPath(Paths.ProgramState), ProjectSandboxGuard.EnsureAllowed(Paths, Paths.ProgramState));
    [Fact] public void AllowGithubExport() => Assert.Equal(Path.GetFullPath(Paths.GitHubExport), ProjectSandboxGuard.EnsureAllowed(Paths, Paths.GitHubExport));
    [Fact] public void RefuseAllowlistRootDeletion() => Assert.Throws<InvalidOperationException>(() => ProjectSandboxGuard.DeleteTree(Paths, Paths.TestSandboxRoot));
    [Fact] public void UniqueRunIds() => Assert.NotEqual(ProjectSandboxPaths.Create(Root).RunId, ProjectSandboxPaths.Create(Root).RunId);
    [Fact] public void InvalidRunIdRejected() => Assert.Throws<ArgumentException>(() => ProjectSandboxPaths.Create(Root, "../bad"));
    [Fact] public void ChildEnvironmentTelemetryOptOut() => Assert.Equal("1", Paths.ChildEnvironment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
    [Fact] public void ChildEnvironmentNoLogo() => Assert.Equal("1", Paths.ChildEnvironment["DOTNET_NOLOGO"]);
    [Fact] public void ChildEnvironmentXmlDocsSkipped() => Assert.Equal("skip", Paths.ChildEnvironment["NUGET_XMLDOC_MODE"]);
    [Fact] public void ChildEnvironmentNodeReuseDisabled() => Assert.Equal("1", Paths.ChildEnvironment["MSBUILDDISABLENODEREUSE"]);
    [Fact] public void NoDesktopSpecialFolderInSandboxPaths() => Assert.All(Paths.AllowedWriteRoots, path => Assert.True(ProjectSandboxGuard.IsWithin(Root, path)));
    [Fact] public void NoSystemTempInSandboxPaths() => Assert.DoesNotContain(Paths.AllowedWriteRoots, path => path.Contains(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase));
    [Fact]
    public void TestSandboxFactoryInsideProject()
    {
        string caseRoot = ProjectLocalTestSandbox.Create();
        try
        {
            Assert.True(ProjectSandboxGuard.IsWithin(Paths.TestSandboxRoot, caseRoot));
            Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, caseRoot));
        }
        finally
        {
            ProjectSandboxGuard.DeleteTree(TestProjectEnvironment.SandboxPaths, caseRoot);
        }
    }
    [Fact]
    public void RuntimeServicesUseOnlyIsolatedPathsAndPreserveProductionState()
    {
        Dictionary<string, string> before = CaptureProtectedProductionState();
        TestProjectEnvironment.ResetPlayerRuntimePaths();

        Assert.False(string.Equals(
            Path.GetFullPath(Root),
            Path.GetFullPath(TestProjectEnvironment.RuntimeRoot),
            StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), AppPaths.Current.ProjectRoot, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(TestProjectEnvironment.RuntimeRoot), PlayerRuntimePaths.ProjectRoot, ignoreCase: true);
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, RuntimeFolders.Logs));
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, RuntimeFolders.Settings));
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, RuntimeFolders.Recordings));
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, RuntimeFolders.StateRoot));

        string settingsPath = PlayerSettingsStore.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(
            settingsPath,
            "{\"SettingsVersion\":3,\"CountdownMode\":0}",
            new UTF8Encoding(false));
        PlayerSettings loaded = PlayerSettingsStore.Load();
        Assert.Equal(PlayerSettings.CurrentVersion, loaded.SettingsVersion);
        Assert.True(ProjectSandboxGuard.IsWithin(TestProjectEnvironment.RuntimeRoot, settingsPath));
        Assert.True(File.Exists(Path.Combine(PlayerRuntimePaths.Logs, "player_settings.log")));

        using (RecorderService recorder = new())
        {
            InvokeRecorder(recorder, "StartRecording");
            InvokeRecorder(recorder, "StopRecording");
        }
        Assert.True(File.Exists(Path.Combine(RuntimeFolders.Logs, "game_input_compatibility.log")));
        Assert.True(File.Exists(Path.Combine(RuntimeFolders.Logs, "MacroRecorder_hook_health.log")));

        AssertProtectedSnapshotsEqual(before, CaptureProtectedProductionState());

        string[] protectedTestEntries = TestProjectEnvironment.IsSourceOnly
            ? [Script("Test.ps1")]
            :
            [
                File.ReadAllText(Path.Combine(Dev, "scripts", "Publish-Release.ps1")),
                Script("Test.ps1")
            ];
        foreach (string script in protectedTestEntries)
        {
            Assert.Contains("Get-ProtectedRuntimeSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("Assert-ProtectedRuntimeSnapshot", script, StringComparison.Ordinal);
            Assert.Contains("PRODUCTION_STATE_PRE_POST_PARITY=PASS", script, StringComparison.Ordinal);
        }
    }
    [Fact] public void TestsDoNotCallPathGetTempPath()
    {
        string forbidden = "Path.Get" + "TempPath";
        string[] files = Directory.GetFiles(Path.Combine(Dev, "tests", "MacroCore.Tests"), "*.cs");
        Assert.DoesNotContain(files, file => File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal));
    }
    [Fact] public void LauncherGateUsesSimulatedDesktop() => Assert.Contains("TestSandbox", TestProjectEnvironment.IsSourceOnly ? Script("Clean.ps1") : Script("Invoke-LauncherGate.ps1"));
    [Fact] public void LauncherGateNoDesktopSpecialFolder() => Assert.DoesNotContain("SpecialFolder", TestProjectEnvironment.IsSourceOnly ? PublicPipeline() : Script("Invoke-LauncherGate.ps1"), StringComparison.Ordinal);
    [Fact] public void LauncherGateSetsSafeValidationEnvironment() => Assert.Contains("MKM_SAFE_VALIDATION_MODE", TestProjectEnvironment.IsSourceOnly ? PublicPipeline() : Script("Invoke-LauncherGate.ps1"));
    [Fact] public void LauncherSafeValidationArgumentParses()
    {
        Assert.True(LauncherArgumentParser.TryParse(["--tool","recorder","--mode","medium","--project-root",Root,"--safe-validation"], out LauncherRequest? request, out _));
        Assert.True(request!.SafeValidation);
    }
    [Fact] public void LauncherSafeEnvironmentRecognized() => Assert.True(LauncherPolicy.IsSafeValidation(Request(), "1"));
    [Fact] public void SafeLauncherDoesNotRegisterInstallRoot() => Assert.False(LauncherPolicy.ShouldRegisterInstallRoot(Request() with { SafeValidation = true }, true));
    [Fact] public void SafeLauncherDoesNotRequireElevation()
    {
        string source = Source("src","MacroLauncher","Program.cs");
        Assert.True(source.IndexOf("if (safeValidation)", StringComparison.Ordinal) < source.IndexOf("LauncherPolicy.RequiresElevation", StringComparison.Ordinal));
    }
    [Fact] public void RecorderSupportsSafeSmoke() => Assert.Contains("--safe-smoke", Source("src","MacroRecorder","Program.cs"));
    [Fact] public void PlayerSupportsSafeSmoke() => Assert.Contains("--safe-smoke", Source("src","MacroPlayer","Program.cs"));
    [Fact] public void WatchdogSupportsValidateOnly() => Assert.Contains("--validate-only", Source("src","MacroSafetyWatchdog","Program.cs"));
    [Fact] public void SafeValidationAvoidsReadyHandshake() => Assert.Contains("RunSafeChild", Source("src","MacroLauncher","Program.cs"));
    [Fact] public void SafeValidationAvoidsRegistryWrite()
    {
        string source = Source("src","MacroLauncher","Program.cs");
        Assert.True(source.IndexOf("if (safeValidation)", StringComparison.Ordinal) < source.IndexOf("InstallRootLocator.TryRegister", StringComparison.Ordinal));
    }
    [Fact] public void SafeValidationAvoidsUac()
    {
        string source = Source("src","MacroLauncher","Program.cs");
        Assert.True(source.IndexOf("return RunSafeValidation", StringComparison.Ordinal) < source.IndexOf("LauncherPolicy.RequiresElevation", StringComparison.Ordinal));
    }
    [Fact] public void SolutionExcludesIntegrationDriver() => Assert.DoesNotContain("MacroIntegrationDriver", Solution(), StringComparison.Ordinal);
    [Fact] public void SolutionExcludesMouseProbe() => Assert.DoesNotContain("MacroMouseIntegrationProbe", Solution(), StringComparison.Ordinal);
    [Fact] public void SolutionExcludesIntegrationTarget() => Assert.DoesNotContain("MacroIntegrationTarget", Solution(), StringComparison.Ordinal);
    [Fact] public void SolutionIncludesPerformanceProbe() => Assert.Contains("MacroPlaybackPerformanceProbe", Solution(), StringComparison.Ordinal);
    [Fact] public void PublishExcludesLiveGates()
    {
        string script = Script("Publish-Release.ps1");
        Assert.DoesNotContain("Invoke-FinalUiGate", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-MouseReplayIntegrationGate", script, StringComparison.Ordinal);
    }
    [Fact] public void PublishUsesSafeLauncherGate() => Assert.Contains("MKM_SAFE_VALIDATION_MODE", TestProjectEnvironment.IsSourceOnly ? PublicPipeline() : Script("Publish-Release.ps1"));
    [Fact] public void ProductionRecorderProbeRemoved() => Assert.False(File.Exists(Path.Combine(Dev,"src","MacroRecorder","RecorderIntegrationCaptureProbe.cs")));
    [Fact] public void ProductionRecorderArgRemoved() => Assert.DoesNotContain("integration-capture-probe", Source("src","MacroRecorder","Program.cs"), StringComparison.Ordinal);
    [Fact] public void ManualOnlyContainsDriver() => Assert.Equal(!TestProjectEnvironment.IsSourceOnly, Directory.Exists(Path.Combine(Dev,"ManualOnly","MacroIntegrationDriver")));
    [Fact] public void ManualOnlyContainsMouseProbe() => Assert.Equal(!TestProjectEnvironment.IsSourceOnly, Directory.Exists(Path.Combine(Dev,"ManualOnly","MacroMouseIntegrationProbe")));
    [Fact] public void ManualOnlyContainsTarget() => Assert.Equal(!TestProjectEnvironment.IsSourceOnly, Directory.Exists(Path.Combine(Dev,"ManualOnly","MacroIntegrationTarget")));
    [Fact] public void ManualOnlyRequiresAllowFlag() => AssertManualConsent("--allow-live-input");
    [Fact] public void ManualOnlyRequiresConsentFile() => AssertManualConsent("LIVE_INPUT_OWNER_CONSENT.txt");
    [Fact] public void ManualOnlyRequiresInteractiveConfirmation() => AssertManualConsent("Console.ReadLine");
    [Fact] public void ConsentFileIsAbsentByDefault() => Assert.False(File.Exists(Path.Combine(Dev,"ManualOnly","LIVE_INPUT_OWNER_CONSENT.txt")));
    [Fact] public void PublicContractForbidsManualOnly() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("ManualOnly/tool.cs"));
    [Fact] public void PublicContractForbidsIntegrationDriver() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("ManualOnly/MacroIntegrationDriver/Program.cs"));
    [Fact] public void ReleaseContractForbidsMouseProbe() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("ManualOnly/MacroMouseIntegrationProbe.exe"));
    [Fact] public void ExportExcludesManualOnly() => Assert.DoesNotContain("ManualOnly", Solution(), StringComparison.OrdinalIgnoreCase);
    [Fact] public void ExportExcludesLiveScripts()
    {
        Assert.False(File.Exists(Path.Combine(Dev,"scripts","Invoke-FinalUiGate.ps1")));
        Assert.False(File.Exists(Path.Combine(Dev,"scripts","Invoke-MouseReplayIntegrationGate.ps1")));
    }
    [Fact] public void CiSetsSafeValidation() => Assert.Contains("MKM_SAFE_VALIDATION_MODE", PublicPipeline());
    [Fact] public void CiUsesDisableParallel() => Assert.Contains("--disable-parallel", PublicPipeline());
    [Fact] public void CiUsesSingleCpu() => Assert.Contains("-m:1", PublicPipeline());
    [Fact] public void RestoreUsesDisableParallel() => Assert.Contains("--disable-parallel", Script("Build.ps1"));
    [Fact] public void BuildUsesSingleCpu() => Assert.Contains("-m:1", Script("Build.ps1"));
    [Fact] public void TestUsesSingleCpu()
    {
        string script = Script("Test.ps1");
        Assert.Contains("-m:1", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--filter", script, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void PublishUsesSingleCpu()
    {
        string script = Script("Publish.ps1");
        Assert.Contains("-m:1", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("publish", script, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void XUnitAssemblyParallelDisabled() => Assert.Contains("\"parallelizeAssembly\": false", XunitConfig());
    [Fact] public void XUnitCollectionsParallelDisabled() => Assert.Contains("\"parallelizeTestCollections\": false", XunitConfig());
    [Fact] public void XUnitMaxThreadsOne() => Assert.Contains("\"maxParallelThreads\": 1", XunitConfig());
    [Fact] public void WrapperUsesBelowNormal() => Assert.Contains("-m:1", PublicPipeline());
    [Fact] public void WrapperHasTimeout() => Assert.Contains("timeout-minutes", PublicPipeline());
    [Fact] public void WrapperUsesExactPidTreeTermination()
    {
        string script = PublicPipeline();
        Assert.DoesNotContain("/IM", script, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void WrapperUsesProjectEnvironment() => Assert.Contains("NUGET_HTTP_CACHE_PATH", PublicPipeline());
    [Fact] public void DirectoryBuildDeterministic() => Assert.Contains("<Deterministic>true</Deterministic>", Props());
    [Fact] public void ReleaseDebugTypeNone() => Assert.Contains("<DebugType>None</DebugType>", Props());
    [Fact] public void ReleaseDebugSymbolsFalse() => Assert.Contains("<DebugSymbols>false</DebugSymbols>", Props());
    [Fact] public void UnsafeBinaryFormatterDisabled() => Assert.Contains("<EnableUnsafeBinaryFormatterSerialization>false</EnableUnsafeBinaryFormatterSerialization>", Props());
    [Fact] public void RecorderUnsafeBinaryFormatterDisabled() => Assert.Contains("EnableUnsafeBinaryFormatterSerialization>false", SourceProject("MacroRecorder"));
    [Fact] public void PlayerUnsafeBinaryFormatterDisabled() => Assert.Contains("EnableUnsafeBinaryFormatterSerialization>false", SourceProject("MacroPlayer"));
    [Fact] public void PublishSingleFileDisabled() => Assert.Contains("<PublishSingleFile>false</PublishSingleFile>", SourceProject("MacroPlayer"));
    [Fact] public void PublishTrimmedDisabled() => Assert.Contains("<PublishTrimmed>false</PublishTrimmed>", SourceProject("MacroRecorder"));
    [Fact] public void PublishAotDisabled() => Assert.Contains("<PublishAot>false</PublishAot>", SourceProject("MacroPlayer"));
    [Fact] public void ReleaseContractForbidsPdb() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("Program/App/Player/app.pdb"));
    [Fact] public void RepositoryContractForbidsPdb() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("symbols/app.pdb"));
    [Fact] public void RepositoryContractForbidsMacro() => Assert.True(PublicationPackageContract.IsForbiddenRepositoryPath("Recordings/private.macro"));
    [Fact] public void ReleaseContractForbidsLogs() => Assert.True(PublicationPackageContract.IsForbiddenReleasePath("Program/State/Logs/private.log"));
    [Fact] public void ExistingMacroHashUnchanged()
    {
        string text = File.ReadAllText(TestProjectEnvironment.SyntheticRawFixture);
        Assert.Contains("synthetic", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2000-01-01", text, StringComparison.Ordinal);
    }
    [Fact] public void ExistingSafetyMechanismsRemain()
    {
        string source = Source("src","MacroCore","Input","CaptureSafety.cs") + Source("src","MacroPlayer","PlaybackService.cs") + Source("src","MacroRecorder","Services","RecorderSafetyStateMachine.cs");
        Assert.Contains("BoundedCapturePipeline", source);
        Assert.Contains("CircuitBreaker", source);
        Assert.Contains("F11", source);
        Assert.Contains("F12", source);
    }

    private static LauncherRequest Request() => new(LauncherTool.Recorder, LauncherMode.Medium, Root, null, false, false);
    private static string Solution() => File.ReadAllText(Path.Combine(Dev,"MouseKeyboardMacro.sln"));
    private static string Script(string name)
    {
        string privatePath = Path.Combine(Dev, "scripts", name);
        string publicPath = Path.Combine(Dev, "GitHubDocs", "Repository", "scripts", name);
        return File.ReadAllText(File.Exists(publicPath) ? publicPath : privatePath);
    }
    private static string PublicPipeline()
    {
        string publicRoot = TestProjectEnvironment.IsSourceOnly
            ? Root
            : Path.Combine(Dev, "GitHubDocs", "Repository");
        return File.ReadAllText(Path.Combine(publicRoot, "scripts", "Test.ps1"))
            + File.ReadAllText(Path.Combine(publicRoot, "scripts", "Publish.ps1"))
            + File.ReadAllText(Path.Combine(publicRoot, ".github", "workflows", "windows-ci.yml"));
    }
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { Dev }.Concat(parts).ToArray()));
    private static string Props() => File.ReadAllText(Path.Combine(Dev,"Directory.Build.props"));
    private static string XunitConfig() => File.ReadAllText(Path.Combine(Dev,"tests","MacroCore.Tests","xunit.runner.json"));
    private static string SourceProject(string name) => File.ReadAllText(Path.Combine(Dev,"src",name,name+".csproj"));
    private static void InvokeRecorder(RecorderService recorder, string method) =>
        typeof(RecorderService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(recorder, null);

    private static Dictionary<string, string> CaptureProtectedProductionState()
    {
        Dictionary<string, string> snapshot = new(StringComparer.OrdinalIgnoreCase);
        foreach (string relativeRoot in new[] { "Program/State", "Recordings" })
        {
            string absoluteRoot = Path.Combine(Root, relativeRoot.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absoluteRoot))
            {
                snapshot["M|" + relativeRoot] = string.Empty;
                continue;
            }

            snapshot["D|" + relativeRoot] = string.Empty;
            foreach (string directory in Directory.EnumerateDirectories(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                snapshot["D|" + Path.GetRelativePath(Root, directory).Replace('\\', '/')] = string.Empty;
            }
            foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*", SearchOption.AllDirectories))
            {
                FileInfo info = new(file);
                string relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                snapshot["F|" + relative] = $"{info.Length}|{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}";
            }
        }
        return snapshot;
    }

    private static void AssertProtectedSnapshotsEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        Assert.Equal(expected.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase),
            actual.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        foreach ((string path, string state) in expected)
        {
            Assert.True(actual.TryGetValue(path, out string? actualState), path);
            Assert.Equal(state, actualState);
        }
    }

    private static void AssertManualConsent(string value)
    {
        if (TestProjectEnvironment.IsSourceOnly)
        {
            Assert.False(Directory.Exists(Path.Combine(Dev,"ManualOnly")));
            return;
        }
        Assert.Contains(value, File.ReadAllText(Path.Combine(Dev,"ManualOnly","ManualLiveInputConsent.cs")));
    }
}
