using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Convenciones.IntegrationTests;

/// <summary>
/// Mints tokens for tests.
/// </summary>
/// <remarks>
/// The key below is a test constant, not a secret: it exists only so the test host has a way to
/// validate a signature offline. Nothing in a deployment uses a symmetric key — real tokens are
/// validated against the identity provider's published keys.
/// </remarks>
internal static class TestAuth
{
    public const string Issuer = "https://test.convenciones.local";
    public const string Audience = "convenciones-tests";
    public const string SigningKey = "convenciones-integration-tests-signing-key-not-a-secret";

    public static string Token(
        Guid subject,
        string? issuer = null,
        string? audience = null,
        string? signingKey = null,
        TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey ?? SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer ?? Issuer,
            audience: audience ?? Audience,
            claims: new List<Claim> { new("sub", subject.ToString()) },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token that was valid in the past. Built explicitly, since a negative lifetime would
    /// produce Expires before NotBefore and fail as a malformed token rather than an expired one.</summary>
    public static string ExpiredToken(Guid subject)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: new List<Claim> { new("sub", subject.ToString()) },
            notBefore: DateTime.UtcNow.AddMinutes(-30),
            expires: DateTime.UtcNow.AddMinutes(-20),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
