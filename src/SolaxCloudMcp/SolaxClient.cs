using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SolaxCloudMcp;

/// <summary>Raised when the SolaX API returns an error.</summary>
public class SolaxApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Self Use Mode settings for <see cref="SolaxClient.SetSelfUseModeAsync"/>. Times are HH:MM.</summary>
public record SelfUseMode(
    int MinSoc,
    int ChargeUpperSoc,
    int ChargeFromGridEnable = 1,
    string? ChargeStartTimePeriod1 = null,
    string? ChargeEndTimePeriod1 = null,
    string? DischargeStartTimePeriod1 = null,
    string? DischargeEndTimePeriod1 = null,
    int EnableTimePeriod2 = 0,
    string? ChargeStartTimePeriod2 = null,
    string? ChargeEndTimePeriod2 = null,
    string? DischargeStartTimePeriod2 = null,
    string? DischargeEndTimePeriod2 = null);

/// <summary>HTTP client for the SolaX Developer Platform API.</summary>
public class SolaxClient
{
    public const string BaseUrl = "https://openapi-eu.solaxcloud.com";
    public const string RealtimeDataUrl = BaseUrl + "/openapi/v2/device/realtime_data";
    public const string HistoryDataUrl = BaseUrl + "/openapi/v2/device/history_data";
    public const string SetSelfUseModeUrl = BaseUrl + "/openapi/v2/device/inverter_work_mode/batch_set_spontaneity_self_use";
    // push_power/positive_or_negative_mode is accepted but ignored by the X3-NEO-LV (tested 2026-10-05); this mode works
    public const string SocTargetUrl = BaseUrl + "/openapi/v2/device/inverter_vpp_mode/soc_target_control_mode";
    public const string ExitVppUrl = BaseUrl + "/openapi/v2/device/inverter_vpp_mode/exit_vpp_mode";
    public const string RequestResultUrl = BaseUrl + "/openapi/apiRequestLog/listByCondition";

    const int DeviceTypeInverter = 1;
    const int DeviceTypeBattery = 2;
    const int BusinessTypeResidential = 1;
    const int RequestSnTypeInverter = 1;

    static ILogger Log => AppLog.For<SolaxClient>();

    readonly SolaxAuth _auth;
    readonly HttpClient _http;
    readonly SemaphoreSlim _rateLimitLock = new(1, 1);
    long? _lastCallAtMs;

    /// <summary>Minimum spacing between API calls: 60 s / 100 calls per minute, with a buffer.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(0.7);

    public SolaxClient(SolaxAuth auth, HttpMessageHandler? handler = null)
    {
        _auth = auth;
        _http = Http.Create(handler, 10);
    }

    /// <summary>Send one API call: rate limit, auth (one retry on 10402), check code 10000. Returns the response body.</summary>
    async Task<JsonObject> SendAsync(HttpMethod method, string url, JsonObject? body = null)
    {
        await _rateLimitLock.WaitAsync();
        try
        {
            if (_lastCallAtMs is { } last && MinInterval.TotalMilliseconds - (Environment.TickCount64 - last) is > 0 and var wait)
                await Task.Delay(TimeSpan.FromMilliseconds(wait));
            _lastCallAtMs = Environment.TickCount64;
        }
        finally
        {
            _rateLimitLock.Release();
        }

        async Task<JsonObject> Once(string prefix)
        {
            string token;
            try
            {
                token = await _auth.GetAccessTokenAsync();
            }
            catch (SolaxAuthException e)
            {
                throw new SolaxApiException(prefix.Length == 0 ? $"Authentication failed: {e.Message}" : $"Re-authentication failed after 10402: {e.Message}", e);
            }

            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", $"bearer {token}");
            // StringContent sends Content-Length (no chunked encoding), like the old client
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            string text;
            try
            {
                response = await _http.SendAsync(request);
                text = await response.Content.ReadAsStringAsync();
            }
            catch (OperationCanceledException e)
            {
                // No cancellation token is passed in, so a cancelled request is always the HttpClient timeout
                throw new SolaxApiException($"{prefix}Network timeout: {e.Message}", e);
            }
            catch (HttpRequestException e)
            {
                throw new SolaxApiException($"{prefix}Network error: {e.Message}", e);
            }

            if ((int)response.StatusCode != 200) throw new SolaxApiException($"{prefix}HTTP {(int)response.StatusCode}: {text}");
            try
            {
                return JsonNode.Parse(text) as JsonObject ?? throw new JsonException("not a JSON object");
            }
            catch (JsonException e)
            {
                throw new SolaxApiException($"{prefix}Invalid JSON response: {e.Message}", e);
            }
        }

        var responseBody = await Once("");
        if (J.Num(responseBody["code"]) == 10402)
        {
            // Access token rejected: drop it and retry once with a fresh one
            _auth.InvalidateToken();
            responseBody = await Once("Retry: ");
        }

        var code = J.Num(responseBody["code"]);
        if (code != 10000)
        {
            var codeText = responseBody["code"]?.ToJsonString() ?? "None";
            var description = code is { } c && c == Math.Floor(c) && Models.ErrorCodeDescriptions.TryGetValue((int)c, out var known) ? known : $"Unknown code {codeText}";
            throw new SolaxApiException($"API error {codeText}: {description}");
        }
        return responseBody;
    }

    async Task<JsonNode?> GetResultAsync(string url, params (string Name, object Value)[] query)
    {
        var queryString = string.Join("&", query.Select(q => $"{q.Name}={Uri.EscapeDataString(Convert.ToString(q.Value, CultureInfo.InvariantCulture)!)}"));
        return (await SendAsync(HttpMethod.Get, $"{url}?{queryString}"))["result"];
    }

    /// <summary>One realtime request. Returns the first result, or null for an empty list (device offline / no data yet).</summary>
    async Task<JsonObject?> RequestRealtimeAsync(string deviceSn, int deviceType, int? requestSnType)
    {
        var query = new List<(string, object)> { ("snList", deviceSn), ("deviceType", deviceType), ("businessType", BusinessTypeResidential) };
        if (requestSnType is { } snType) query.Add(("requestSnType", snType));
        return await GetResultAsync(RealtimeDataUrl, [.. query]) is JsonArray { Count: > 0 } list ? list[0] as JsonObject : null;
    }

    /// <summary>Inverter history samples (W for residential) between two UTC ms timestamps, at most 12 h apart.</summary>
    public virtual async Task<JsonArray> FetchHistoryAsync(string deviceSn, long startMs, long endMs, int intervalMin = 5) =>
        await GetResultAsync(HistoryDataUrl, ("snList", deviceSn), ("deviceType", DeviceTypeInverter), ("businessType", BusinessTypeResidential),
            ("startTime", startMs), ("endTime", endMs), ("timeInterval", intervalMin)) as JsonArray ?? [];

    public async Task<JsonObject> FetchInverterDataAsync(string deviceSn) =>
        await RequestRealtimeAsync(deviceSn, DeviceTypeInverter, null)
        ?? throw new SolaxApiException($"No inverter data returned for device_sn={deviceSn}");

    /// <summary>Battery data for an inverter, or null. Errors are swallowed: they most likely mean a system with no battery.</summary>
    public async Task<JsonObject?> FetchBatteryDataAsync(string deviceSn)
    {
        try
        {
            return await RequestRealtimeAsync(deviceSn, DeviceTypeBattery, RequestSnTypeInverter);
        }
        catch (SolaxApiException)
        {
            return null;
        }
    }

    /// <summary>Inverter and battery realtime data. Throws if the inverter call fails; battery errors give null.</summary>
    public virtual async Task<(JsonObject Inverter, JsonObject? Battery)> FetchRealtimeDataAsync(string deviceSn) =>
        (await FetchInverterDataAsync(deviceSn), await FetchBatteryDataAsync(deviceSn));

    /// <summary>Set the inverter to Self Use Mode with SOC limits and optional charge/discharge time periods.</summary>
    public virtual Task<JsonObject> SetSelfUseModeAsync(string deviceSn, SelfUseMode mode)
    {
        var body = new JsonObject
        {
            ["snList"] = new JsonArray(deviceSn),
            ["minSoc"] = mode.MinSoc,
            ["chargeFromGridEnable"] = mode.ChargeFromGridEnable,
            ["chargeUpperSoc"] = mode.ChargeUpperSoc,
            ["businessType"] = BusinessTypeResidential,
        };
        void Time(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) body[name] = value;
        }
        Time("chargeStartTimePeriod1", mode.ChargeStartTimePeriod1);
        Time("chargeEndTimePeriod1", mode.ChargeEndTimePeriod1);
        Time("dischargeStartTimePeriod1", mode.DischargeStartTimePeriod1);
        Time("dischargeEndTimePeriod1", mode.DischargeEndTimePeriod1);
        if (mode.EnableTimePeriod2 != 0) body["enableTimePeriod2"] = mode.EnableTimePeriod2;
        Time("chargeStartTimePeriod2", mode.ChargeStartTimePeriod2);
        Time("chargeEndTimePeriod2", mode.ChargeEndTimePeriod2);
        Time("dischargeStartTimePeriod2", mode.DischargeStartTimePeriod2);
        Time("dischargeEndTimePeriod2", mode.DischargeEndTimePeriod2);
        return SendAsync(HttpMethod.Post, SetSelfUseModeUrl, body);
    }

    /// <summary>Throw unless the device command was issued (code 10000 only means the platform accepted the request).</summary>
    static JsonObject CheckDelivered(JsonObject response, string deviceSn)
    {
        Log.LogWarning("SolaX command for {DeviceSn}: requestId={RequestId} result={Result}", deviceSn, response["requestId"]?.ToJsonString(), response["result"]?.ToJsonString());
        var status = J.Num(((response["result"] as JsonObject)?[deviceSn] as JsonObject)?["status"]);
        // result[SN].status: 3 = command issued, 4 = device received and started execution
        if (status is not (3 or 4))
            throw new SolaxApiException($"Command not delivered to {deviceSn}: status {status?.ToString(CultureInfo.InvariantCulture) ?? "None"}");
        return response;
    }

    Task<JsonObject> SocTargetAsync(string deviceSn, int power, int targetSoc) => SendAsync(HttpMethod.Post, SocTargetUrl, new JsonObject
    {
        ["snList"] = new JsonArray(deviceSn),
        ["targetSoc"] = targetSoc,
        ["chargeDischargPower"] = power, // sic, API field name: positive = charge, negative = discharge
        ["businessType"] = BusinessTypeResidential,
    });

    /// <summary>Remote control: discharge at dischargeW (W, inverter AC port) until the battery reaches stopSoc (%).
    /// The mode has no duration: it runs until stopSoc is reached or ExitVppModeAsync is called. The house
    /// load is served first and only the rest is exported. If stopSoc >= current SOC, the inverter exits the mode at once.</summary>
    public virtual async Task<JsonObject> DischargeToSocAsync(string deviceSn, int dischargeW, int stopSoc) =>
        CheckDelivered(await SocTargetAsync(deviceSn, -dischargeW, stopSoc), deviceSn);

    /// <summary>Remote control: charge at chargeW (W, inverter AC port, from grid and PV) until the battery reaches stopSoc (%).
    /// The X3-NEO-LV accepts but ignores the Self Use grid-charge settings; this mode is what charges from the grid
    /// (verified on hardware 2026-10-13).</summary>
    public virtual async Task<JsonObject> ChargeToSocAsync(string deviceSn, int chargeW, int stopSoc) =>
        CheckDelivered(await SocTargetAsync(deviceSn, chargeW, stopSoc), deviceSn);

    /// <summary>Exit remote control: the inverter returns to its normal work mode (e.g. Self Use).</summary>
    public virtual async Task<JsonObject> ExitVppModeAsync(string deviceSn) =>
        CheckDelivered(await SendAsync(HttpMethod.Post, ExitVppUrl, new JsonObject { ["snList"] = new JsonArray(deviceSn), ["businessType"] = BusinessTypeResidential }), deviceSn);

    /// <summary>Execution result of a control command, by the requestId it returned (status 4 = started, 5 = failed).</summary>
    public Task<JsonObject> GetRequestResultAsync(string requestId) =>
        SendAsync(HttpMethod.Post, RequestResultUrl, new JsonObject { ["requestId"] = requestId });
}
