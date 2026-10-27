using System.Diagnostics;
using System.Runtime.CompilerServices;
using TailControl.Agent.Power;

namespace TailControl.Tests.Agent;

/// <summary>
/// Dovezi ca agentul nu poate executa text primit din afara: procesele pornite au cale absoluta
/// si argumente constante, iar codul sursa nu are alte cai de pornire a proceselor.
/// </summary>
public sealed class NoArbitraryExecutionTests
{
    [Fact]
    public void ScheduleShutdown_UsesAbsolutePath_AndConstantArguments()
    {
        var psi = SystemCommands.ScheduleShutdown(TimeSpan.FromSeconds(10));

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"), psi.FileName);
        Assert.True(Path.IsPathFullyQualified(psi.FileName));
        Assert.False(psi.UseShellExecute);
        Assert.Empty(psi.Arguments);
        Assert.Equal(["/s", "/t", "10", "/c", SystemCommands.ShutdownComment, "/d", "p:0:0"], psi.ArgumentList);
    }

    [Fact]
    public void AbortShutdown_UsesOnlySlashA()
    {
        var psi = SystemCommands.AbortShutdown();

        Assert.Equal(SystemCommands.ShutdownExePath, psi.FileName);
        Assert.Equal(["/a"], psi.ArgumentList);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(601)]
    [InlineData(-1)]
    public void ScheduleShutdown_DelayOutOfRange_Throws(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemCommands.ScheduleShutdown(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void ProcessRunner_AcceptsOnlyTheSafeShutdownCommands()
    {
        ProcessRunner.EnsureSafe(SystemCommands.ScheduleShutdown(TimeSpan.FromSeconds(10)));
        ProcessRunner.EnsureSafe(SystemCommands.AbortShutdown());
    }

    [Fact]
    public void ProcessRunner_RejectsShellExecute()
    {
        var psi = SystemCommands.AbortShutdown();
        psi.UseShellExecute = true;

        Assert.Throws<InvalidOperationException>(() => ProcessRunner.EnsureSafe(psi));
    }

    [Fact]
    public void ProcessRunner_RejectsArgumentString()
    {
        var psi = new ProcessStartInfo(SystemCommands.ShutdownExePath) { Arguments = "/s /t 0", UseShellExecute = false };

        Assert.Throws<InvalidOperationException>(() => ProcessRunner.EnsureSafe(psi));
    }

    [Theory]
    [InlineData("shutdown.exe")]
    [InlineData("cmd.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Users\Public\shutdown.exe")]
    public void ProcessRunner_RejectsOtherExecutables(string fileName)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };

        Assert.Throws<InvalidOperationException>(() => ProcessRunner.EnsureSafe(psi));
    }

    [Fact]
    public void SourceCode_StartsProcessesOnlyThroughProcessRunner()
    {
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("Process.Start(", StringComparison.Ordinal))
            .Select(RelativeToSrc)
            .ToList();

        Assert.Equal([Path.Combine("TailControl.Agent", "Power", "ProcessRunner.cs")], offenders);
    }

    [Fact]
    public void SourceCode_UsesProcessStartInfoOnlyInSystemCommandsAndRunner()
    {
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains("ProcessStartInfo", StringComparison.Ordinal))
            .Select(RelativeToSrc)
            .Order()
            .ToList();

        Assert.Equal(
            [Path.Combine("TailControl.Agent", "Power", "ProcessRunner.cs"), Path.Combine("TailControl.Agent", "Power", "SystemCommands.cs")],
            offenders);
    }

    [Theory]
    [InlineData("UseShellExecute = true")]
    [InlineData("\"cmd")]
    [InlineData("\"powershell")]
    [InlineData("\"pwsh")]
    [InlineData("\"rundll32")]
    [InlineData("\"wscript")]
    [InlineData(".Arguments =")]
    [InlineData("Arguments = \"")]
    public void SourceCode_ContainsNoShellOrStringArguments(string forbidden)
    {
        var offenders = SourceFiles()
            .Where(f => File.ReadAllText(f).Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            .Select(RelativeToSrc)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> SourceFiles([CallerFilePath] string thisFile = "")
    {
        var src = SrcDirectory(thisFile);
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal));
    }

    private static string RelativeToSrc(string path) => Path.GetRelativePath(SrcDirectory(), path);

    private static string SrcDirectory([CallerFilePath] string thisFile = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TailControl.slnx")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("Radacina repo-ului nu a fost gasita."), "src");
    }
}
