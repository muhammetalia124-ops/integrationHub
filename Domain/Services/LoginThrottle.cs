using Microsoft.Extensions.Caching.Memory;

namespace IntegrationHub.Domain;

public sealed class LoginThrottle
{
    public const int MaxAttempts = 3;

    private readonly IMemoryCache _cache;

    public LoginThrottle(IMemoryCache cache)
    {
        _cache = cache;
    }

    public LoginBlockState GetState(string email)
    {
        return _cache.TryGetValue<LoginBlockState>(Key(email), out var state) && state is not null
            ? state
            : new LoginBlockState(0, null);
    }

    public bool IsBlocked(string email)
    {
        var state = GetState(email);
        return state.BlockedUntilUtc.HasValue && state.BlockedUntilUtc.Value > DateTime.UtcNow;
    }

    public LoginBlockState RegisterFailure(string email)
    {
        var state = GetState(email);
        var attempts = state.FailedAttempts + 1;
        DateTime? blockedUntil = null;
        if (attempts >= MaxAttempts)
        {
            blockedUntil = DateTime.UtcNow.AddMinutes(BlockMinutes);
        }

        var next = new LoginBlockState(attempts >= MaxAttempts ? 0 : attempts, blockedUntil);
        _cache.Set(Key(email), next, TimeSpan.FromMinutes(BlockMinutes));
        return next;
    }

    public void Reset(string email) => _cache.Remove(Key(email));

    private static int BlockMinutes => 15;

    private static string Key(string email) => $"login:throttle:{email.Trim().ToLowerInvariant()}";
}

public sealed record LoginBlockState(int FailedAttempts, DateTime? BlockedUntilUtc);
