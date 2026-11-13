using Lynxmote.Core;

namespace Lynxmote.Tests;

public class TerminalTests
{
    [Fact]
    public void Quote_survives_single_quotes() =>
        Assert.Equal(@"'echo '\''hi'\'''", Shell.Quote("echo 'hi'"));

    [Fact]
    public void Node_command_keeps_folder_merges_stderr_and_closes_stdin() =>
        Assert.Equal("{ cd '/var/log' && ls\n} </dev/null 2>&1", Shell.Compose("ls", "/var/log", TerminalTarget.Node, 0));

    [Fact]
    public void Container_command_goes_through_pct_exec() =>
        Assert.Equal(@"pct exec 101 -- bash -lc 'df -h 2>&1' </dev/null", Shell.Compose("df -h", "~", TerminalTarget.Container, 101));

    [Fact]
    public void Root_gets_a_stand_in_for_a_missing_sudo() =>
        Assert.Equal("{ command -v sudo >/dev/null || sudo() { \"$@\"; }; sudo apt update\n} </dev/null 2>&1",
            Shell.Compose("sudo apt update", "~", TerminalTarget.Node, 0));

    [Fact]
    public void Plain_user_gets_sbin_and_sudo_reads_the_password_once() =>
        Assert.Equal("{ sudo -S -p '' -v && { PATH=\"$PATH:/usr/local/sbin:/usr/sbin:/sbin\"; sudo fdisk -l\n} </dev/null; } 2>&1",
            Shell.Compose("sudo fdisk -l", "~", TerminalTarget.Node, 0, root: false, sudoPassword: true));

    [Theory]
    [InlineData("nano /etc/hosts", "nano")]
    [InlineData("sudo vim x", "vim")]
    [InlineData("top -bn1", null)]
    [InlineData("vigr", null)]
    [InlineData("cat nano", null)]
    public void Full_screen_programs_are_recognized(string command, string? program) =>
        Assert.Equal(program, Shell.FullScreen(command));

    [Fact]
    public void Folder_listing_puts_folders_first()
    {
        Assert.Equal("ls -1ApL -- '/var/my dir' 2>/dev/null", Shell.ListCommand("/var/my dir"));
        Assert.Equal([("etc", true), ("Var", true), ("a.txt", false), ("b", false)], Shell.ParseList("b\nVar/\r\na.txt\netc/\n\n"));
    }

    [Fact]
    public async Task Demo_terminal_lists_folders_for_the_tree()
    {
        using var t = new DemoTerminal("pve-01");
        var list = Shell.ParseList((await t.RunAsync(Shell.ListCommand("/root"), default)).Output);
        Assert.Equal([("backup", true), ("scripts", true), ("notes.txt", false)], list);
        Assert.Empty(Shell.ParseList((await t.RunAsync(Shell.ListCommand("/nowhere"), default)).Output));
    }

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
    public void Qm_own_error_from_stderr_is_shown_and_recognized()
    {
        // No agent: qm prints nothing to stdout, dies with 255 and the reason on stderr.
        var (output, code) = Shell.ParseGuestExec("", 255);
        output = Shell.Merge(output, "QEMU guest agent is not running\n");
        Assert.Equal(("QEMU guest agent is not running\n", 255), (output, code));
        Assert.True(Shell.NoAgent(output));
        Assert.True(Shell.NoAgent("No QEMU guest agent configured"));
        Assert.False(Shell.NoAgent("VM 100 is not running"));
    }

    [Fact]
    public void Stderr_goes_after_the_output() =>
        Assert.Equal("a\nwarning", Shell.Merge("a\n", "warning"));

    [Fact]
    public async Task Demo_terminal_tracks_folder()
    {
        using var t = new DemoTerminal("pve-01");
        await t.RunAsync("cd /var/log", default);
        Assert.Equal("root@pve-01:/var/log#", t.Prompt);
        Assert.Equal("/var/log", (await t.RunAsync("pwd", default)).Output);
        Assert.Equal(127, (await t.RunAsync("nano", default)).ExitCode);
        Assert.StartsWith("Linux pve-01 ", (await t.RunAsync(Shell.Greeting, default)).Output);
    }
}
