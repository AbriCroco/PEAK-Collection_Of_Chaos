namespace Chaos.Effects
{
    public interface IEffect
    {
        byte Id { get; }
        string IntroMessage { get; }
        int CountdownSeconds { get; }

        void Apply(string? triggererName = null);
    }
}
