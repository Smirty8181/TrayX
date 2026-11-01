using TrayX.Domain.Entities;

namespace TrayX.Application.Profiles;

public interface IProfileStore
{
    Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(Profile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
