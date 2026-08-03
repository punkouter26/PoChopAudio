using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PoChopAudio.API.Features.Auth;

public sealed class FakeAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IHostEnvironment _environment;

    public FakeAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _environment = environment;

        if (_environment.IsProduction())
        {
            throw new InvalidOperationException("FakeAuthHandler is strictly forbidden in Production environment.");
        }
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userHeader = Request.Headers["X-Fake-User"].FirstOrDefault() ?? "dev-user";
        var rolesHeader = Request.Headers["X-Fake-Roles"].FirstOrDefault() ?? "User";

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userHeader),
            new(ClaimTypes.Name, userHeader),
        };

        foreach (var role in rolesHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "FakeAuth");
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, "FakeAuth");

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
