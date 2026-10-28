using ProxMonitor.Core;

namespace ProxMonitor.Tests;

public class TerminalTests
{
    [Fact]
    public void Quote_survives_single_quotes() =>
        Assert.Equal(@"'echo '\''hi'\'''", Shell.Quote("echo 'hi'"));

    [Fact]
    public void Node_command_keeps_folder_and_merges_stderr() =>
        Assert.Equal("{ cd '/var/log' && ls\n} 2>&1", Shell.Compose("ls", "/var/log", TerminalTarget.Node, 0));

    [Fact]
    public void Container_command_goes_through_pct_exec() =>
        Assert.Equal(@"pct exec 101 -- bash -lc 'df -h 2>&1'", Shell.Compose("df -h", "~", TerminalTarget.Container, 101));

    [Fact]
    public void Vm_command_goes_through_guest_agent() =>
        Assert.StartsWith("qm guest exec 100 -- bash -lc ", Shell.Compose("uptime", "~", TerminalTarget.Vm, 100));

    [Fact]
    public void Guest_exec_json_is_unwrapped()
    {
        var (output, code) = Shell.ParseGuestExec("""{"exitcode":3,"exited":1,"out-data":"a\n","err-data":"b"}""", 0);
        Assert.Equal("a\nb", output);
        Assert.Equal(3, code);
    }

    [Fact]
    public void Guest_exec_error_text_is_kept() =>
        Assert.Equal(("QEMU guest agent is not running", 255), Shell.ParseGuestExec("QEMU guest agent is not running", 255));

    [Fact]
    public async Task Demo_terminal_tracks_folder()
    {
        using var t = new DemoTerminal("pve-01");
        await t.RunAsync("cd /var/log", default);
        Assert.Equal("root@pve-01:/var/log#", t.Prompt);
        Assert.Equal("/var/log", (await t.RunAsync("pwd", default)).Output);
        Assert.Equal(127, (await t.RunAsync("nano", default)).ExitCode);
    }
}
