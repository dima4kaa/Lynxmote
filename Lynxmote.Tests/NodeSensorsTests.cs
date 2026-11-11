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
    public void Command_has_no_carriage_returns()
    {
        Assert.DoesNotContain('\r', NodeSensors.Command);
    }
}
