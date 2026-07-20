namespace SamsungSwitchWatch.Core.Profiles;

public sealed class DeviceProfileRegistry
{
    private readonly IReadOnlyDictionary<string, DeviceCommandProfile> _profiles;

    public DeviceProfileRegistry(IEnumerable<DeviceCommandProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var materialized = profiles.ToArray();
        if (materialized.Length == 0 ||
            materialized.Select(profile => profile.Model)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            throw new ArgumentException("At least one uniquely named device profile is required.", nameof(profiles));
        }

        _profiles = materialized.ToDictionary(profile => profile.Model, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Models => _profiles.Keys.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGet(string model, out DeviceCommandProfile profile) =>
        _profiles.TryGetValue(model, out profile!);

    public DeviceCommandProfile GetRequired(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return _profiles.TryGetValue(model, out var profile)
            ? profile
            : throw new ArgumentOutOfRangeException(nameof(model), model, "The switch model has no registered profile.");
    }
}
