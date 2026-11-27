using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure.Persistence.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly UserDbContext _db;

    public UserRepository(UserDbContext db)
    {
        _db = db;
    }

    public Task<User?> GetByIdAsync(int id, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(user => user.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        _db.Users.FirstOrDefaultAsync(user => user.Email == email, ct);

    public Task<bool> AnyByEmailAsync(string email, CancellationToken ct) =>
        _db.Users.AnyAsync(user => user.Email == email, ct);

    public Task<bool> AnyByEmailExceptIdAsync(string email, int exceptId, CancellationToken ct) =>
        _db.Users.AnyAsync(user => user.Email == email && user.Id != exceptId, ct);

    public async Task<List<User>> GetAllOrderedAsync(CancellationToken ct) =>
        await _db.Users.AsNoTracking().OrderBy(user => user.Id).ToListAsync(ct);

    public Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        _db.Users.CountAsync(user => user.IsAdmin && user.IsActive, ct);

    public Task<bool> AnyAdminAsync(CancellationToken ct) =>
        _db.Users.AnyAsync(user => user.IsAdmin, ct);

    public Task<bool> IsActiveAdminAsync(int id, CancellationToken ct) =>
        _db.Users.AnyAsync(user => user.Id == id && user.IsActive && user.IsAdmin, ct);

    public async Task AddAsync(User user, CancellationToken ct)
    {
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(User user, CancellationToken ct)
    {
        if (_db.Entry(user).State == EntityState.Detached)
        {
            _db.Users.Update(user);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(User user, CancellationToken ct)
    {
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
