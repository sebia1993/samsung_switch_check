using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup;

internal enum ViewerAddressSuggestionKind
{
    None,
    Single,
    Multiple
}

internal sealed record ViewerAddressChoice(
    string Address,
    string DisplayText);

internal sealed record ViewerAddressSuggestion(
    ViewerAddressSuggestionKind Kind,
    IReadOnlyList<ViewerAddressChoice> Choices)
{
    public static ViewerAddressSuggestion Create(
        IEnumerable<NetworkCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var choices = candidates
            .Where(candidate =>
                Ipv4Input.TryParseStrict(candidate.Address, out var address) &&
                Ipv4Input.IsPrivate(address))
            .GroupBy(candidate => candidate.Address, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(candidate => new ViewerAddressChoice(
                candidate.Address,
                $"{candidate.Address} · {candidate.InterfaceName} · {candidate.Description}"))
            .ToArray();

        return new ViewerAddressSuggestion(
            choices.Length switch
            {
                0 => ViewerAddressSuggestionKind.None,
                1 => ViewerAddressSuggestionKind.Single,
                _ => ViewerAddressSuggestionKind.Multiple
            },
            choices);
    }
}
