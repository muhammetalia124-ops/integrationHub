namespace IntegrationHub.Domain;

public enum AdminOperationStatus
{
    Success,
    NotFound,
    Conflict
}

public sealed record AdminOperationResult<T>(
    AdminOperationStatus Status,
    T? Value = default,
    string? Error = null);

public sealed class AdminService
{
    private const string UserNotFound = "Kullanıcı bulunamadı.";
    private const string EmailAlreadyRegistered = "Bu e-posta zaten kayıtlı.";
    private const string LastAdminProtected = "Sistemde en az bir etkin yönetici bulunmalıdır.";

    private readonly IUserRepository _users;
    private readonly UserActivityService _activities;
    private readonly IPasswordHasher _passwordHasher;

    public AdminService(IUserRepository users, UserActivityService activities, IPasswordHasher passwordHasher)
    {
        _users = users;
        _activities = activities;
        _passwordHasher = passwordHasher;
    }

    public async Task<List<AdminUserResponse>> GetUsersAsync(CancellationToken ct) =>
        (await _users.GetAllOrderedAsync(ct)).Select(ToResponse).ToList();

    public async Task<AdminOperationResult<AdminUserResponse>> GetUserByIdAsync(int id, CancellationToken ct)
    {
        var user = await FindUserAsync(id, ct);
        return user is null ? NotFound<AdminUserResponse>(UserNotFound) : Success(ToResponse(user));
    }

    public async Task<AdminOperationResult<AdminUserResponse>> CreateUserAsync(
        CreateAdminUserRequest request,
        CancellationToken ct)
    {
        var email = request.Email.Trim();
        if (await _users.AnyByEmailAsync(email, ct))
        {
            return Conflict<AdminUserResponse>(EmailAlreadyRegistered);
        }

        var user = new User
        {
            Email = email,
            Username = request.Username.Trim(),
            PasswordHash = _passwordHasher.Hash(request.Password),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await _users.AddAsync(user, ct);
        return Success(ToResponse(user));
    }

    public async Task<AdminOperationResult<AdminUserResponse>> UpdateUserAsync(
        int id,
        UpdateAdminUserRequest request,
        bool isCurrentUser,
        CancellationToken ct)
    {
        var user = await FindUserAsync(id, ct);
        if (user is null)
        {
            return NotFound<AdminUserResponse>(UserNotFound);
        }

        var email = request.Email.Trim();
        if (email != user.Email && await _users.AnyByEmailExceptIdAsync(email, id, ct))
        {
            return Conflict<AdminUserResponse>(EmailAlreadyRegistered);
        }

        if (isCurrentUser && (!request.IsAdmin || !request.IsActive))
        {
            return Conflict<AdminUserResponse>(
                "Kendi yönetici hesabınızı devre dışı bırakamaz veya yetkisini kaldıramazsınız.");
        }

        if (user.IsAdmin && user.IsActive && (!request.IsAdmin || !request.IsActive) && await CountActiveAdminsAsync(ct) <= 1)
        {
            return Conflict<AdminUserResponse>(LastAdminProtected);
        }

        user.Email = email;
        user.Username = request.Username.Trim();
        user.IsActive = request.IsActive;
        user.IsAdmin = request.IsAdmin;
        await _users.UpdateAsync(user, ct);
        return Success(ToResponse(user));
    }

    public async Task<AdminOperationResult<bool>> SetPasswordAsync(
        int id,
        SetAdminUserPasswordRequest request,
        CancellationToken ct)
    {
        var user = await FindUserAsync(id, ct);
        if (user is null)
        {
            return NotFound<bool>(UserNotFound);
        }

        user.PasswordHash = _passwordHasher.Hash(request.Password);
        await _users.UpdateAsync(user, ct);
        return Success(true);
    }

    public async Task<AdminOperationResult<List<UserActivityDto>>> GetHistoryAsync(int id, CancellationToken ct)
    {
        if (await FindUserAsync(id, ct) is null)
        {
            return NotFound<List<UserActivityDto>>(UserNotFound);
        }

        return Success(await _activities.GetHistoryAsync(id, ct));
    }

    public async Task<AdminOperationResult<bool>> ClearHistoryAsync(int id, CancellationToken ct)
    {
        if (await FindUserAsync(id, ct) is null)
        {
            return NotFound<bool>(UserNotFound);
        }

        await _activities.ClearHistoryAsync(id, ct);
        return Success(true);
    }

    public async Task<AdminOperationResult<bool>> DeleteUserAsync(int id, bool isCurrentUser, CancellationToken ct)
    {
        var user = await FindUserAsync(id, ct);
        if (user is null)
        {
            return NotFound<bool>(UserNotFound);
        }

        if (isCurrentUser)
        {
            return Conflict<bool>("Kendi hesabınızı silemezsiniz.");
        }

        if (user.IsAdmin && user.IsActive && await CountActiveAdminsAsync(ct) <= 1)
        {
            return Conflict<bool>(LastAdminProtected);
        }

        await _activities.ClearHistoryAsync(id, ct);
        await _users.DeleteAsync(user, ct);
        return Success(true);
    }

    private Task<User?> FindUserAsync(int id, CancellationToken ct) =>
        _users.GetByIdAsync(id, ct);

    private Task<int> CountActiveAdminsAsync(CancellationToken ct) =>
        _users.CountActiveAdminsAsync(ct);

    private static AdminOperationResult<T> Success<T>(T value) => new(AdminOperationStatus.Success, value);

    private static AdminOperationResult<T> NotFound<T>(string error) =>
        new(AdminOperationStatus.NotFound, Error: error);

    private static AdminOperationResult<T> Conflict<T>(string error) =>
        new(AdminOperationStatus.Conflict, Error: error);

    private static AdminUserResponse ToResponse(User user) => new()
    {
        Id = user.Id,
        Username = user.Username,
        Email = user.Email,
        IsActive = user.IsActive,
        IsAdmin = user.IsAdmin,
        CreatedAt = user.CreatedAt
    };
}
