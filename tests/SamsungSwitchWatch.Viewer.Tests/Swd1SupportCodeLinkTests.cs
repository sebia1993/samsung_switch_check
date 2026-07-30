using SamsungSwitchWatch.Support;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class Swd1SupportCodeLinkTests
{
    [Fact]
    public void ViewerAssembly_CompilesAndRunsLinkedSwd1Codec()
    {
        var payload = Swd1ViewerPayloadBuilder.Build(
            "0.10.9-poc",
            "AGENT_CONNECTION_CHECK",
            "NONE",
            "NONE",
            "NORMAL",
            "NONE",
            "SUCCEEDED",
            "SUCCEEDED",
            "SUCCEEDED",
            "SUCCEEDED",
            "SUCCEEDED",
            1,
            "0.10.9-poc",
            "3");

        var code = Swd1SupportCode.Encode(payload);

        Assert.Matches(
            "^SWD1-[0-9A-HJKMNP-TV-Z]{4}(?:-[0-9A-HJKMNP-TV-Z]{4}){3}$",
            code);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(payload, decoded);
    }
}
