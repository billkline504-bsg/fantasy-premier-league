using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EplFantasy.Api.Contracts;
using EplFantasy.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EplFantasy.ApiTests;

/// <summary>
/// IT-13 (F-001.2): proves requestPasswordReset (always 202, BR-284's no-account-existence-leak)
/// and confirmPasswordReset (changes the password for real, rejects an unknown token or a weak
/// new password) end-to-end against the real host. The raw reset token is never exposed via HTTP
/// (BR-284) — no email-delivery mechanism exists yet (IPasswordResetService's own remarks) — so
/// confirmPasswordReset tests fetch it by calling that service directly, in-process, the same way
/// a future email sender eventually would.
/// </summary>
[Collection(nameof(ApiTestCollection))]
public class PasswordResetControllerTests(EplFantasyApiFactory factory) : IClassFixture<EplFantasyApiFactory>
{
    private const string StrongPassword = "correct-horse-battery-staple-97!";

    private async Task<(string Username, string Email)> RegisterAsync(HttpClient client)
    {
        var username = $"u{Guid.NewGuid():N}"[..16];
        var email = $"{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new { username, email, password = StrongPassword });
        return (username, email);
    }

    private async Task<string> IssueResetTokenAsync(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        return (await service.RequestPasswordResetAsync(email))!;
    }

    [Fact]
    public async Task RequestPasswordReset_returns_202_for_a_real_registered_email()
    {
        var client = factory.CreateClient();
        var (_, email) = await RegisterAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task RequestPasswordReset_also_returns_202_for_an_email_that_matches_no_account()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new { email = $"nobody-{Guid.NewGuid():N}@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ConfirmPasswordReset_changes_the_password_so_the_user_can_log_in_with_the_new_one_but_not_the_old()
    {
        var client = factory.CreateClient();
        var (username, email) = await RegisterAsync(client);
        var rawToken = await IssueResetTokenAsync(email);
        const string newPassword = "a-brand-new-strong-password-42!";

        var confirmResponse = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { resetToken = rawToken, newPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirmResponse.StatusCode);

        var newLoginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = newPassword });
        Assert.Equal(HttpStatusCode.OK, newLoginResponse.StatusCode);

        var oldLoginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = StrongPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, oldLoginResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmPasswordReset_with_an_unknown_token_returns_400()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { resetToken = "this-token-was-never-issued", newPassword = "a-brand-new-strong-password-42!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_reset_token", body.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ConfirmPasswordReset_with_a_weak_new_password_returns_400_and_the_token_still_works_afterward()
    {
        var client = factory.CreateClient();
        var (username, email) = await RegisterAsync(client);
        var rawToken = await IssueResetTokenAsync(email);

        var weakResponse = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { resetToken = rawToken, newPassword = "aaaaaaaaaaaa" });

        Assert.Equal(HttpStatusCode.BadRequest, weakResponse.StatusCode);
        var body = await weakResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("password_too_weak", body.GetProperty("errorCode").GetString());

        const string strongPassword = "a-brand-new-strong-password-42!";
        var retryResponse = await client.PostAsJsonAsync("/api/v1/auth/password-reset/confirm", new { resetToken = rawToken, newPassword = strongPassword });
        Assert.Equal(HttpStatusCode.NoContent, retryResponse.StatusCode);

        var loginResponse = await client.PostAsJsonAsync("/api/v1/auth/login", new { usernameOrEmail = username, password = strongPassword });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
    }
}
