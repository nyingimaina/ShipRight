using System.Security.Cryptography;
using System.Text;
using Jattac.Libraries.QBuilder;
using Rocket.Libraries.DatabaseIntegrator;
using ShipRight.Database;
using ShipRight.Modules.Auth.Models;

namespace ShipRight.Modules.Auth.Services;

public interface ITokenService
{
    Task<(string accessToken, string refreshToken)> CreateSessionAsync(User user);
    Task<(string accessToken, string refreshToken)?> RefreshSessionAsync(string refreshToken);
    Task RevokeRefreshTokenAsync(string refreshToken);
    Task RevokeAllUserSessionsAsync(Guid userId);
}

public class TokenService : ITokenService
{
    private readonly IDatabaseHelper<Guid> _db;
    private readonly IJwtService _jwt;
    private readonly TimeSpan _refreshTokenLifetime = TimeSpan.FromDays(30);

    public TokenService(IDatabaseHelper<Guid> db, IJwtService jwt)
    {
        _db = db;
        _jwt = jwt;
    }

    public async Task<(string accessToken, string refreshToken)> CreateSessionAsync(User user)
    {
        var accessToken = _jwt.GenerateAccessToken(
            user.Id, user.Email, user.CompanyId, user.IsAdmin, user.TokenVersion);

        var (refreshToken, tokenHash) = GenerateRefreshToken();
        var expiry = DateTime.UtcNow.Add(_refreshTokenLifetime);

        var rt = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = tokenHash,
            ExpiresAt = expiry,
            Created = DateTime.UtcNow,
            Modified = DateTime.UtcNow,
        };

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseTableBoundInsert<RefreshToken>()
            .FromObject(rt)
            .BuildWithParameters();

        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);

        return (accessToken, refreshToken);
    }

    public async Task<(string accessToken, string refreshToken)?> RefreshSessionAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseSelector()
            .Select<RefreshToken>("*")
            .Then()
            .UseTableBoundFilter<RefreshToken>()
            .WhereEqualTo(r => r.TokenHash, tokenHash)
            .Then()
            .BuildWithParameters();

        var tokens = await _db.GetManyAsync<RefreshToken>(built.ParameterizedSql, built.Parameters);
        var stored = tokens.FirstOrDefault();

        if (stored == null || stored.RevokedAt != null || stored.ExpiresAt < DateTime.UtcNow)
            return null;

        var userQb = new QBuilder(parameterize: true);
        var userBuilt = userQb
            .UseSelector()
            .Select<User>("*")
            .Then()
            .UseTableBoundFilter<User>()
            .WhereEqualTo(u => u.Id, stored.UserId)
            .Then()
            .BuildWithParameters();

        var users = await _db.GetManyAsync<User>(userBuilt.ParameterizedSql, userBuilt.Parameters);
        var user = users.FirstOrDefault();

        if (user == null || user.Deleted)
            return null;

        stored.RevokedAt = DateTime.UtcNow;
        using var updateQb = new QBuilder(parameterize: true);
        var updateBuilt = updateQb
            .UseTableBoundUpdate<RefreshToken>()
            .Set(r => r.RevokedAt, stored.RevokedAt.Value)
            .WhereEqualTo(r => r.Id, stored.Id)
            .BuildWithParameters();
        await _db.ExecuteAsync(updateBuilt.ParameterizedSql, updateBuilt.Parameters);

        return await CreateSessionAsync(user);
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken)
    {
        var tokenHash = HashToken(refreshToken);

        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseTableBoundUpdate<RefreshToken>()
            .Set(r => r.RevokedAt, DateTime.UtcNow)
            .WhereEqualTo(r => r.TokenHash, tokenHash)
            .BuildWithParameters();

        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    public async Task RevokeAllUserSessionsAsync(Guid userId)
    {
        using var qBuilder = new QBuilder(parameterize: true);
        var built = qBuilder
            .UseTableBoundUpdate<RefreshToken>()
            .Set(r => r.RevokedAt, DateTime.UtcNow)
            .WhereEqualTo(r => r.UserId, userId)
            .BuildWithParameters();

        await _db.ExecuteAsync(built.ParameterizedSql, built.Parameters);
    }

    private static (string token, string hash) GenerateRefreshToken()
    {
        var tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes);
        var hash = HashToken(token);
        return (token, hash);
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
