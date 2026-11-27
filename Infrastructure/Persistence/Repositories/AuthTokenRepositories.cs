using IntegrationHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace IntegrationHub.Infrastructure.Persistence.Repositories;

public sealed class AuthVerificationCodeRepository : IAuthVerificationCodeRepository
{
    private readonly UserDbContext _db;

    public AuthVerificationCodeRepository(UserDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(AuthVerificationCode code, CancellationToken ct)
    {
        _db.AuthVerificationCodes.Add(code);
        await _db.SaveChangesAsync(ct);
    }

    public Task<AuthVerificationCode?> GetLatestActiveAsync(string email, string purpose, CancellationToken ct) =>
        _db.AuthVerificationCodes
            .AsNoTracking()
            .Where(code => code.Email == email &&
                           code.Purpose == purpose &&
                           code.ConsumedAtUtc == null &&
                           code.ExpiresAtUtc > DateTime.UtcNow &&
                           code.Attempts < 5)
            .OrderByDescending(code => code.Id)
            .FirstOrDefaultAsync(ct);

    public async Task UpdateAsync(AuthVerificationCode code, CancellationToken ct)
    {
        if (_db.Entry(code).State == EntityState.Detached)
        {
            _db.AuthVerificationCodes.Update(code);
        }

        await _db.SaveChangesAsync(ct);
    }

    public Task DeleteAllForEmailAsync(string email, string purpose, CancellationToken ct) =>
        _db.AuthVerificationCodes
            .Where(code => code.Email == email && code.Purpose == purpose)
            .ExecuteDeleteAsync(ct);
}

public sealed class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly UserDbContext _db;

    public RefreshTokenRepository(UserDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(RefreshToken token, CancellationToken ct)
    {
        _db.RefreshTokens.Add(token);
        await _db.SaveChangesAsync(ct);
    }

    public Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct) =>
        _db.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash, ct);

    public async Task UpdateAsync(RefreshToken token, CancellationToken ct)
    {
        if (_db.Entry(token).State == EntityState.Detached)
        {
            _db.RefreshTokens.Update(token);
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(RefreshToken token, CancellationToken ct)
    {
        if (_db.Entry(token).State != EntityState.Deleted)
        {
            _db.RefreshTokens.Remove(token);
        }

        await _db.SaveChangesAsync(ct);
    }

    public Task DeleteAllForUserAsync(int userId, CancellationToken ct) =>
        _db.RefreshTokens
            .Where(token => token.UserId == userId)
            .ExecuteDeleteAsync(ct);

    public Task DeleteExpiredForUserAsync(int userId, CancellationToken ct) =>
        _db.RefreshTokens
            .Where(token => token.UserId == userId && token.ExpiresAtUtc <= DateTime.UtcNow)
            .ExecuteDeleteAsync(ct);
}
