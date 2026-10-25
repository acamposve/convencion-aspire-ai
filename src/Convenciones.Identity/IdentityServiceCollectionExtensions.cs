using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Convenciones.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers authentication, the fallback authorization policy and session resolution.
    /// </summary>
    /// <remarks>
    /// The fallback policy requires an authenticated user, so an endpoint becomes protected by
    /// default and has to opt out explicitly. Opting in by accident is impossible; opting out is
    /// visible in the diff.
    /// </remarks>
    public static IServiceCollection AddConvencionesIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

        ValidateJwtOptions(jwt);

        services.AddSingleton(jwt);
        services.AddSingleton<ISessionResolver>(_ => new PostgresSessionResolver(connectionString));
        services.AddScoped<ISessionAccessor, SessionAccessor>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Keep claim names as issued; mapping renames "sub" and breaks subject resolution.
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                if (jwt.HasSigningKey)
                {
                    var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey!));
                    options.TokenValidationParameters.IssuerSigningKey = key;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(jwt.MetadataAddress))
                    {
                        options.MetadataAddress = jwt.MetadataAddress;
                    }

                    if (!string.IsNullOrWhiteSpace(jwt.Authority))
                    {
                        options.Authority = jwt.Authority;
                    }
                }
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        return services;
    }

    private static void ValidateJwtOptions(JwtOptions jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt.Issuer) || string.IsNullOrWhiteSpace(jwt.Audience))
        {
            throw new InvalidOperationException(
                "Jwt:Issuer and Jwt:Audience must be configured. Without them a token from any "
                + "issuer for any audience would be accepted.");
        }

        if (!jwt.HasSigningKey && !jwt.HasMetadataEndpoint)
        {
            throw new InvalidOperationException(
                "Configure either Jwt:SigningKey (local development and tests) or "
                + "Jwt:MetadataAddress / Jwt:Authority (Supabase JWKS). Refusing to start without a "
                + "way to validate token signatures.");
        }

        if (jwt.HasSigningKey && jwt.SigningKey!.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 characters for HS256.");
        }
    }
}
