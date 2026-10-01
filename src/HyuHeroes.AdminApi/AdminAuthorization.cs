/**
 * ADMIN_AUTHORIZATION
 * Purpose: Configures JWT authentication and explicit role policies for the content-management API.
 * Connections: Program registers this module; AdminEndpoints binds authoring, review, and publishing operations to named policies.
 * Risk: High because policy mistakes could permit unreviewed or unauthorized content publication.
 */
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace HyuHeroes.AdminApi;

public static class AdminAuthorization
{
    public const string ReaderPolicy = "content-reader";
    public const string AuthorPolicy = "content-author";
    public const string ReviewerPolicy = "content-reviewer";
    public const string PublisherPolicy = "content-publisher";

    public static IServiceCollection AddAdminAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var issuer = Require(configuration, "HYU_ADMIN_JWT_ISSUER");
        var audience = Require(configuration, "HYU_ADMIN_JWT_AUDIENCE");
        var signingKey = Require(configuration, "HYU_ADMIN_JWT_SIGNING_KEY");
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException("HYU_ADMIN_JWT_SIGNING_KEY must be at least 32 UTF-8 bytes.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(1),
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role"
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ReaderPolicy, policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AuthorPolicy, policy => policy.RequireRole("Author"));
            options.AddPolicy(ReviewerPolicy, policy => policy.RequireRole("Reviewer"));
            options.AddPolicy(PublisherPolicy, policy => policy.RequireRole("Publisher"));
        });
        return services;
    }

    public static string RequireActor(ClaimsPrincipal principal)
    {
        var actor = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return string.IsNullOrWhiteSpace(actor)
            ? throw new InvalidOperationException("Authenticated token is missing the subject claim.")
            : actor;
    }

    private static string Require(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Required configuration '{key}' is missing.")
            : value;
    }
}
