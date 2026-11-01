namespace TrayX.Domain.Entities;

public sealed record Profile
{
    public const int NameMaxLength = 48;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int VolumePercent { get; init; }

    public required bool IsMuted { get; init; }

    public required string WindowsTheme { get; init; }

    public required string Accent { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public static Profile Create(
        string name,
        int volumePercent,
        bool isMuted,
        string windowsTheme,
        string accent)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ArgumentException(
                $"Имя профиля должно содержать от 1 до {NameMaxLength} символов.",
                nameof(name));
        }

        var theme = windowsTheme is "light" or "dark" ? windowsTheme : "dark";

        return new Profile
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            VolumePercent = Math.Clamp(volumePercent, 0, 100),
            IsMuted = isMuted,
            WindowsTheme = theme,
            Accent = accent,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
