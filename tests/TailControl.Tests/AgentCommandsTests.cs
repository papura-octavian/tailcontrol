using TailControl.Shared.Commands;

namespace TailControl.Tests;

public sealed class AgentCommandsTests
{
    [Fact]
    public void All_CoversEveryEnumValue_AndRoundTrips()
    {
        Assert.Equal(Enum.GetValues<AgentCommand>().Order(), AgentCommands.All);

        foreach (var command in AgentCommands.All)
        {
            Assert.True(AgentCommands.TryParse(AgentCommands.ToRouteName(command), out var parsed));
            Assert.Equal(command, parsed);
        }
    }

    [Theory]
    [InlineData("shutdown", AgentCommand.Shutdown)]
    [InlineData("lock", AgentCommand.Lock)]
    [InlineData("sleep", AgentCommand.Sleep)]
    [InlineData("cancel-shutdown", AgentCommand.CancelShutdown)]
    public void TryParse_KnownName_ReturnsCommand(string name, AgentCommand expected)
    {
        Assert.True(AgentCommands.TryParse(name, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("Shutdown")]
    [InlineData("SHUTDOWN")]
    [InlineData(" shutdown")]
    [InlineData("shutdown\n")]
    [InlineData("shutdown;calc.exe")]
    [InlineData("shutdown /s /t 0")]
    [InlineData("shutdown && del C:\\*")]
    [InlineData("../shutdown")]
    [InlineData("restart")]
    [InlineData("hibernate")]
    [InlineData("cmd.exe /c whoami")]
    [InlineData("powershell -c Get-Process")]
    public void TryParse_AnythingOutsideFixedList_IsRejected(string? value)
    {
        Assert.False(AgentCommands.TryParse(value, out var command));
        Assert.Equal(default, command);
    }

    [Fact]
    public void ToRouteName_UndefinedValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentCommands.ToRouteName(default));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentCommands.ToRouteName((AgentCommand)99));
    }
}
