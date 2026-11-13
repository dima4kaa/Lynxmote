using Lynxmote.Core;

namespace Lynxmote.Tests;

public class NodeSensorsTests
{
    [Fact]
    public void Intel_node_takes_the_cpu_sensor_and_sums_physical_nics()
    {
        var r = NodeSensors.Parse("""
            T|acpitz|27800
            T|nvme|38850
            T|coretemp|52000
            T|coretemp|49000
            T|x86_pkg_temp|51000
            T|iwlwifi_1|
            N|1000|2000
            N|500|100
            C|17
            """);
        Assert.Equal(52, r.TempC);
        Assert.Equal(17, r.Connections);
        Assert.Equal(1500, r.RxBytes);
        Assert.Equal(2100, r.TxBytes);
    }

    [Fact]
    public void Without_a_cpu_sensor_the_board_one_is_used_and_junk_is_ignored()
    {
        var r = NodeSensors.Parse("T|nvme|61000\r\nT|acpitz|40000\r\nT|acpitz|-273000\r\nT|pch|255000\r\nC|0\r\n");
        Assert.Equal(40, r.TempC);
        Assert.Equal(0, r.Connections);
        Assert.Null(r.RxBytes);
    }

    [Fact]
    public void A_node_without_sensors_has_no_temperature()
    {
        // What the command prints in a VM: the globs stay unexpanded and cat fails.
        var r = NodeSensors.Parse("T||\nT||\nN|10|20\nC|3\n");
        Assert.Null(r.TempC);
        Assert.Equal(3, r.Connections);
    }

    [Fact]
    public void Commands_have_no_carriage_returns()
    {
        Assert.DoesNotContain('\r', NodeSensors.Command);
        Assert.DoesNotContain('\r', GuestProbe.Command);
    }

    [Fact]
    public void Neighbours_map_a_network_card_to_its_address()
    {
        var r = NodeSensors.Parse("C|1\nA|10.0.0.31|BC:24:11:aa:bb:cc\nA|10.0.0.1|00:11:22:33:44:55\n");
        Assert.Equal("10.0.0.31", r.Neighbors["bc:24:11:aa:bb:cc"]);
        Assert.Equal(2, r.Neighbors.Count);
    }

    [Fact]
    public void Guest_disk_counts_each_device_once_and_lists_addresses()
    {
        // What the guest command prints: sda1 is mounted twice (a bind mount), sizes are in KiB.
        var r = GuestProbe.Parse("D|/dev/sda1|20000000|5000000\r\nD|/dev/sda1|20000000|5000000\nD|/dev/sda15|100000|10000\nI|10.0.0.31/24\nI|10.0.0.31/24\nI|192.168.5.2/24\n");
        Assert.Equal(20_100_000L * 1024, r.DiskTotal);
        Assert.Equal(5_010_000L * 1024, r.DiskUsed);
        Assert.Equal("10.0.0.31, 192.168.5.2", r.Ips);
    }

    [Fact]
    public void Guest_without_df_output_has_no_disk_and_no_address()
    {
        var r = GuestProbe.Parse("");
        Assert.Equal(0, r.DiskTotal);
        Assert.Null(r.Ips);
    }
}
