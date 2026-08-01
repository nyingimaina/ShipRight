using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace ShipRight.Modules.Auth.Services;

public interface IJwtService
{
    string GenerateAccessToken(Guid userId, string email, Guid companyId, bool isAdmin, int tokenVersion);
    ClaimsPrincipal? ValidateToken(string token);
}

public class JwtService : IJwtService
{
    private readonly string _signingKey;
    private readonly string _issuer;
    private readonly string _audience;

    public JwtService(string signingKey, string? issuer = null, string? audience = null)
    {
        _signingKey = signingKey;
        _issuer = issuer ?? "shipright";
        _audience = audience ?? "shipright-api";
    }

    public string GenerateAccessToken(Guid userId, string email, Guid companyId, bool isAdmin, int tokenVersion)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, email),
            new Claim("company_id", companyId.ToString()),
            new Claim("is_admin", isAdmin ? "true" : "false"),
            new Claim("token_version", tokenVersion.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public ClaimsPrincipal? ValidateToken(string token)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_signingKey));

        try
        {
            var handler = new JwtSecurityTokenHandler();
            return handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = _audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            }, out _);
        }
        catch
        {
            return null;
        }
    }
}
