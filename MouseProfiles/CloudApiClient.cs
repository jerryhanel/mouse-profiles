using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MouseProfiles;

// MOUSEPROFILES_CLOUD_URL overrides the base URL for a production deployment.
static class CloudApiClient
{
    static readonly string BaseUrl = Environment.GetEnvironmentVariable("MOUSEPROFILES_CLOUD_URL") is { Length: > 0 } u
        ? u : "http://localhost:5110";

    static readonly HttpClient Http = new();
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static async Task<string> LoginAsync(string email, string password)
    {
        var resp = await Http.PostAsJsonAsync($"{BaseUrl}/auth/login", new { email, password }, Web);
        await EnsureSuccessAsync(resp);
        var body = await resp.Content.ReadFromJsonAsync<LoginResponse>(Web);
        return body!.Token;
    }

    public static async Task RegisterAsync(string email, string password)
    {
        var resp = await Http.PostAsJsonAsync($"{BaseUrl}/auth/register", new { email, password }, Web);
        await EnsureSuccessAsync(resp);
    }

    public static async Task<List<CloudProfileMeta>> ListProfilesAsync(string token)
    {
        using var req = AuthRequest(HttpMethod.Get, "/profiles", token);
        var resp = await Http.SendAsync(req);
        await EnsureSuccessAsync(resp, isAuthenticated: true);
        return await resp.Content.ReadFromJsonAsync<List<CloudProfileMeta>>(Web) ?? [];
    }

    public static async Task<CloudProfileFull> DownloadProfileAsync(string token, Guid id)
    {
        using var req = AuthRequest(HttpMethod.Get, $"/profiles/{id}", token);
        var resp = await Http.SendAsync(req);
        await EnsureSuccessAsync(resp, isAuthenticated: true);
        return (await resp.Content.ReadFromJsonAsync<CloudProfileFull>(Web))!;
    }

    public static async Task<CloudSyncResult> SyncAsync(string token, IEnumerable<CloudSyncUpsert> upserts, IEnumerable<Guid> deletes)
    {
        using var req = AuthRequest(HttpMethod.Post, "/profiles/sync", token);
        req.Content = JsonContent.Create(new { upsert = upserts, delete = deletes }, options: Web);
        var resp = await Http.SendAsync(req);
        await EnsureSuccessAsync(resp, isAuthenticated: true);
        return (await resp.Content.ReadFromJsonAsync<CloudSyncResult>(Web))!;
    }

    static HttpRequestMessage AuthRequest(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    static async Task EnsureSuccessAsync(HttpResponseMessage resp, bool isAuthenticated = false)
    {
        if (resp.IsSuccessStatusCode) return;
        if (isAuthenticated && resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new SessionExpiredException();
        string? detail = null;
        try
        {
            var pd = await resp.Content.ReadFromJsonAsync<ProblemDetailsResponse>(Web);
            detail = pd?.Detail;
        }
        catch { }
        throw new ApiException(detail ?? resp.ReasonPhrase ?? "Request failed.", (int)resp.StatusCode);
    }

    record LoginResponse(string Token);
    record ProblemDetailsResponse(string? Detail);

    public record CloudProfileMeta(Guid Id, string Name);
    public record CloudProfileFull(Guid Id, string Name, string ConfigJson);
    public record CloudSyncUpsert(Guid Id, string Name, string ConfigJson);
    public record CloudSyncResult(int Created, int Updated, int Deleted);
}

sealed class ApiException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

sealed class SessionExpiredException() : Exception("Session expired. Please log in again.");
