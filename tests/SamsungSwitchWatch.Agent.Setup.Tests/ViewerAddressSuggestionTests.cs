using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class ViewerAddressSuggestionTests
{
    [Fact]
    public void Create_WithOnePrivateAddress_ReturnsSingleChoice()
    {
        var suggestion = ViewerAddressSuggestion.Create(
        [
            Candidate(
                "ethernet",
                "Ethernet",
                "192.168.20.15",
                "192.168.20.0/24",
                "사내 유선")
        ]);

        Assert.Equal(ViewerAddressSuggestionKind.Single, suggestion.Kind);
        var choice = Assert.Single(suggestion.Choices);
        Assert.Equal("192.168.20.15", choice.Address);
        Assert.Contains("Ethernet", choice.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_WithMultiplePrivateAddresses_RequiresAChoiceAndDeduplicatesAddresses()
    {
        var suggestion = ViewerAddressSuggestion.Create(
        [
            Candidate(
                "ethernet",
                "Ethernet",
                "10.20.30.40",
                "10.20.30.0/24",
                "사내 유선"),
            Candidate(
                "duplicate",
                "Ethernet 2",
                "10.20.30.40",
                "10.20.30.0/24",
                "중복 주소"),
            Candidate(
                "wifi",
                "Wi-Fi",
                "192.168.50.20",
                "192.168.50.0/24",
                "무선")
        ]);

        Assert.Equal(ViewerAddressSuggestionKind.Multiple, suggestion.Kind);
        Assert.Collection(
            suggestion.Choices,
            choice => Assert.Equal("10.20.30.40", choice.Address),
            choice => Assert.Equal("192.168.50.20", choice.Address));
    }

    [Fact]
    public void Create_WithoutPrivateIpv4_ReturnsNoChoice()
    {
        var suggestion = ViewerAddressSuggestion.Create(
        [
            Candidate(
                "public",
                "Ethernet",
                "203.0.113.10",
                "203.0.113.0/24",
                "공인 주소"),
            Candidate(
                "invalid",
                "Broken",
                "not-an-ip",
                "invalid",
                "잘못된 주소")
        ]);

        Assert.Equal(ViewerAddressSuggestionKind.None, suggestion.Kind);
        Assert.Empty(suggestion.Choices);
    }

    private static NetworkCandidate Candidate(
        string id,
        string interfaceName,
        string address,
        string cidr,
        string description) =>
        new(id, interfaceName, address, cidr, description);
}
