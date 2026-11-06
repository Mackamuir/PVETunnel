using PveTunnel.Relay;
using Xunit;

namespace PveTunnel.Relay.Tests;

/// <summary>
/// The registry decides where a VM's traffic goes and which port belongs to whom, so every
/// rejection here is a routing mistake that never reaches production. The relay fails closed on
/// all of them rather than starting with a partly-trusted file.
/// </summary>
public class VmRegistryTests
{
    private const string Valid = """
    {
      "version": 1,
      "vms": [
        { "vmid": "101", "managementPort": 24101, "ingress": true, "egress": true,
          "egressTarget": "10.10.0.20:5000", "ingressTarget": "127.0.0.1:5986" },
        { "vmid": "102", "managementPort": 24102, "ingress": false, "egress": true,
          "egressTarget": "10.10.0.20:5000", "ingressTarget": "127.0.0.1:5986" }
      ]
    }
    """;

    [Fact]
    public void ParsesAValidRegistry()
    {
        var reg = VmRegistry.Parse(Valid);

        Assert.Equal(2, reg.Count);
        var vm101 = reg["101"];
        Assert.Equal(24101, vm101.ManagementPort);
        Assert.True(vm101.IngressEnabled);
        Assert.Equal("10.10.0.20:5000", vm101.EgressTarget.ToString());
        Assert.Equal("127.0.0.1:5986", vm101.IngressTarget.ToString());
        Assert.False(reg["102"].IngressEnabled);
    }

    /// <summary>
    /// The one that would actually hurt: two VMs claiming a port means one VM's management traffic
    /// lands in another VM's guest. There is no safe winner, so the whole file is refused.
    /// </summary>
    [Fact]
    public void RejectsDuplicateManagementPort()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" },
            { "vmid": "102", "managementPort": 24101, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        var ex = Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json));
        Assert.Contains("claimed by both", ex.Message);
        Assert.Contains("101", ex.Message);
        Assert.Contains("102", ex.Message);
    }

    [Fact]
    public void RejectsDuplicateVmid()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" },
            { "vmid": "101", "managementPort": 24102, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        Assert.Contains("more than once", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    /// <summary>A hostname would rebind the target at connect time, defeating the fixed-target rule.</summary>
    [Theory]
    [InlineData("ghosts.example.local:5000")]
    [InlineData("localhost:5000")]
    public void RejectsHostnameTargets(string target)
    {
        var json = $$"""
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "egressTarget": "{{target}}", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        Assert.Contains("not a literal IP address", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    /// <summary>
    /// The guest management target must be guest loopback. A routable address there would describe
    /// a bridge reaching across the exercise network, which is the thing this transport exists to
    /// avoid needing.
    /// </summary>
    [Fact]
    public void RejectsNonLoopbackIngressTarget()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "egressTarget": "10.0.0.1:5000", "ingressTarget": "10.2.10.20:5986" }
          ]
        }
        """;
        Assert.Contains("guest loopback", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(0)]
    public void RejectsManagementPortOutsideRange(int port)
    {
        var json = $$"""
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": {{port}}, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        Assert.Contains("outside", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"\"")]
    [InlineData("\"10 1\"")]
    [InlineData("\"12345678901\"")]
    public void RejectsNonNumericVmid(string vmidLiteral)
    {
        var json = $$"""
        {
          "version": 1,
          "vms": [
            { "vmid": {{vmidLiteral}}, "managementPort": 24101, "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        Assert.Contains("invalid vmid", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    [Fact]
    public void RejectsUnsupportedVersion()
        => Assert.Contains("not supported", Assert.Throws<VmRegistryException>(
            () => VmRegistry.Parse("""{ "version": 2, "vms": [] }""")).Message);

    [Fact]
    public void RejectsMalformedJson()
        => Assert.Contains("not valid JSON", Assert.Throws<VmRegistryException>(
            () => VmRegistry.Parse("{ not json")).Message);

    [Fact]
    public void RejectsEntryWithBothDirectionsDisabled()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "ingress": false, "egress": false,
              "egressTarget": "10.0.0.1:5000", "ingressTarget": "127.0.0.1:5986" }
          ]
        }
        """;
        Assert.Contains("does nothing", Assert.Throws<VmRegistryException>(() => VmRegistry.Parse(json)).Message);
    }

    /// <summary>
    /// The sample shipped in deploy/ is what an operator will copy, so it has to survive the real
    /// parser rather than merely being valid JSON. It also carries underscore-prefixed comment
    /// keys, which confirms unknown fields are ignored rather than rejected.
    /// </summary>
    [Fact]
    public void ShippedExampleRegistryParses()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vms.example.json");
        Assert.True(File.Exists(path), $"example registry not copied to output: {path}");

        var reg = VmRegistry.Load(path);

        Assert.Equal(2, reg.Count);
        Assert.True(reg["101"].IngressEnabled);
        Assert.False(reg["102"].IngressEnabled);
        Assert.NotEqual(reg["101"].ManagementPort, reg["102"].ManagementPort);
    }

    /// <summary>
    /// The exact shape deploy/pve-tunnel-vm.sh writes. The shell script allocates and the relay
    /// validates, so if those two drift the failure appears on a node at deploy time rather than
    /// here. Keep this in step with the allocator.
    /// </summary>
    [Fact]
    public void AcceptsTheShapeWrittenByTheAllocationScript()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            {
              "vmid": "101",
              "managementPort": 24000,
              "ingress": true,
              "egress": true,
              "egressTarget": "127.0.0.1:5000",
              "ingressTarget": "127.0.0.1:5986"
            }
          ]
        }
        """;
        var reg = VmRegistry.Parse(json);

        Assert.Single(reg);
        Assert.Equal(24000, reg["101"].ManagementPort);
        Assert.True(reg["101"].IngressEnabled);
        Assert.Equal("127.0.0.1:5986", reg["101"].IngressTarget.ToString());
    }

    /// <summary>
    /// install-relay-node.sh seeds an empty registry before any VM is attached, so the relay has
    /// to start on one. Authoritative-and-empty means every VM is refused, which is correct; what
    /// would not be correct is refusing to start at all.
    /// </summary>
    [Fact]
    public void AcceptsAnEmptyRegistry()
    {
        var reg = VmRegistry.Parse("""{ "version": 1, "vms": [] }""");
        Assert.Empty(reg);
    }

    [Fact]
    public void AcceptsBracketedIpv6Target()
    {
        var json = """
        {
          "version": 1,
          "vms": [
            { "vmid": "101", "managementPort": 24101, "egressTarget": "[fd00::20]:5000", "ingressTarget": "[::1]:5986" }
          ]
        }
        """;
        var reg = VmRegistry.Parse(json);
        Assert.Equal(5000, reg["101"].EgressTarget.Port);
        Assert.Equal(5986, reg["101"].IngressTarget.Port);
    }
}
