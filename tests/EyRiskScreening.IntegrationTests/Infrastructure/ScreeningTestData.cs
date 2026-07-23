using System.Net.Http.Headers;
using System.Net.Http.Json;
using EyRiskScreening.Api.Contracts.Authentication;
using EyRiskScreening.Api.Contracts.Screening;
using Xunit;

namespace EyRiskScreening.IntegrationTests.Infrastructure;

internal static class ScreeningTestData
{
    public static ScreeningRequest ValidRequest(params ScreeningSourceContract[] sources) =>
        new()
        {
            EntityName = "Acme Corporation",
            Sources = sources.Length == 0 ? [ScreeningSourceContract.Ofac] : sources,
        };

    public static async Task<string> LoginAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { userName, password = IdentityTestData.ValidPassword },
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(
            TestContext.Current.CancellationToken);
        return login?.AccessToken
            ?? throw new InvalidOperationException("Login response did not contain an access token.");
    }

    public static HttpRequestMessage CreateAuthorizedScreeningRequest(
        string token,
        ScreeningRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/screenings")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    public static HttpRequestMessage CreateAuthorizedHistoryRequest(
        string token,
        Guid runId)
    {
        var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/screenings/{runId:D}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }
}
