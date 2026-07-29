using System.Text.Json.Nodes;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

internal sealed record ExistingTargetNetworksLoadResult(
    IReadOnlyList<string> TargetCidrs,
    SetupStepResult? Warning)
{
    public bool Succeeded => Warning is null;
}

internal sealed class ExistingTargetNetworkLoader(
    ISetupFileSystem fileSystem,
    DeploymentPaths paths)
{
    public ExistingTargetNetworksLoadResult Load()
    {
        try
        {
            if (!fileSystem.FileExists(paths.ProductionConfigurationPath))
            {
                return Success([]);
            }

            if (JsonNode.Parse(
                    fileSystem.ReadAllText(paths.ProductionConfigurationPath)) is not
                    JsonObject root ||
                root["Agent"] is not JsonObject agent ||
                agent["AllowedTargetCidrs"] is not JsonArray values ||
                values.Count is < 1 or > 2)
            {
                return Warning();
            }

            var cidrs = new List<string>(values.Count);
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (value is not JsonValue jsonValue ||
                    !jsonValue.TryGetValue<string>(out var cidr) ||
                    !Ipv4Input.IsCanonicalPrivateCidr(cidr) ||
                    !unique.Add(cidr))
                {
                    return Warning();
                }

                cidrs.Add(cidr);
            }

            return Success(cidrs.ToArray());
        }
        catch
        {
            return Warning();
        }
    }

    private static ExistingTargetNetworksLoadResult Success(
        IReadOnlyList<string> targetCidrs) =>
        new(targetCidrs, null);

    private static ExistingTargetNetworksLoadResult Warning() =>
        new(
            [],
            new SetupStepResult(
                SetupErrorCodes.ExistingNetworksNotLoaded,
                "기존 관리망 설정",
                SetupStepState.Warning,
                "기존 Agent 관리망을 불러오지 못했습니다. 승인된 관리망을 다시 선택하거나 추가하세요."));
}
