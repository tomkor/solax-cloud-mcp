using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Raised when OAuth2 authentication fails.</summary>
public class SolaxAuthException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>OAuth2 token lifecycle for the SolaX Developer Platform (fetch, cache, refresh, invalidate).</summary>
public class SolaxAuth
{
    public const string TokenUrl = "https://openapi-eu.solaxcloud.com/openapi/auth/oauth/token";
    const long ExpiryBufferMs = 300_000;

    readonly HttpClient _http;
    readonly SemaphoreSlim _lock = new(1, 1);
    string? _token;
    long _expiresAtMs;

    public SolaxAuth(HttpMessageHandler? handler = null)
    {
        _http = Http.Create(handler, 5);
    }

    /// <summary>A valid access token; fetches a new one when the cached token is within 5 minutes of expiry.</summary>
    public virtual async Task<string> GetAccessTokenAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_token is not null && Environment.TickCount64 < _expiresAtMs - ExpiryBufferMs) return _token;
            var (token, expiresIn) = await FetchNewTokenAsync();
            _token = token;
            _expiresAtMs = Environment.TickCount64 + (long)(expiresIn * 1000);
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Clear the cached token. The client calls this on error 10402 (access_token authentication failed).</summary>
    public virtual void InvalidateToken()
    {
        _token = null;
        _expiresAtMs = 0;
    }

    async Task<(string Token, double ExpiresIn)> FetchNewTokenAsync()
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = Config.ClientId(),
            ["client_secret"] = Config.ClientSecret(),
            ["grant_type"] = "client_credentials",
        });

        HttpResponseMessage response;
        string text;
        try
        {
            response = await _http.PostAsync(TokenUrl, form);
            text = await response.Content.ReadAsStringAsync();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new SolaxAuthException($"Network error during token fetch: {e.Message}", e);
        }

        if ((int)response.StatusCode != 200)
            throw new SolaxAuthException($"Token endpoint returned HTTP {(int)response.StatusCode}: {text}");

        JsonObject body;
        try
        {
            body = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("not a JSON object");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            throw new SolaxAuthException($"Failed to parse token response JSON: {e.Message}", e);
        }

        // The auth endpoint uses code 0 for success, NOT 10000 like the data endpoints
        if (J.Num(body["code"]) != 0)
            throw new SolaxAuthException($"Token fetch failed: code={body["code"]?.ToJsonString()}, message={J.Str(body["message"])}");

        if (body["result"] is not JsonObject result)
            throw new SolaxAuthException("Token response missing 'result' field");
        if (J.Str(result["access_token"]) is not { Length: > 0 } token)
            throw new SolaxAuthException("Token response missing 'access_token' in result");
        if (J.Num(result["expires_in"]) is not { } expiresIn)
            throw new SolaxAuthException("Token response missing 'expires_in' in result");
        return (token, expiresIn);
    }
}
