namespace ScheduleIArcade.Games;

internal sealed class ArcadeGameRegistry
{
    private readonly Dictionary<string, Func<IArcadeGame>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string id, Func<IArcadeGame> factory)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Game ID cannot be empty.", nameof(id));

        _factories[id] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public bool TryCreate(string id, out IArcadeGame? game)
    {
        if (_factories.TryGetValue(id, out var factory))
        {
            game = factory();
            return true;
        }

        game = null;
        return false;
    }

    public string[] RegisteredIds => _factories.Keys.ToArray();
}
