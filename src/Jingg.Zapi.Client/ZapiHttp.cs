using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jingg.Zapi;

/// <summary>Configuração do cliente.</summary>
public sealed class ZapiClientOptions
{
    /// <summary>Endereço da API, ex. <c>http://localhost:3000</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Chave de administrador (acesso total) ou chave de um dispositivo (só aquele dispositivo).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Tempo máximo de cada requisição. Padrão: 30s.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Tentativas extras após falha transitória (rede, 429, 502, 503, 504). Padrão: 3.</summary>
    public int MaxRetries { get; set; } = 3;
}

/// <summary>Qualquer resposta não-2xx da API, ou falha ao alcançá-la (<see cref="StatusCode"/> = 0).</summary>
public sealed class ZapiException : Exception
{
    public ZapiException(string message, int statusCode, string? responseBody = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Status HTTP; 0 quando a API não respondeu (rede, timeout) ou em falhas dos helpers de espera.</summary>
    public int StatusCode { get; }
    public string? ResponseBody { get; }

    /// <summary>O dispositivo não está rodando ou não está conectado ao WhatsApp.</summary>
    public bool IsNotConnected => StatusCode == 409;
    public bool IsNotFound => StatusCode == 404;
}

internal sealed class ZapiHttp
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ZapiClientOptions _options;
    private readonly string _baseUrl;

    public ZapiHttp(ZapiClientOptions options, HttpClient? httpClient)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("BaseUrl é obrigatório", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ApiKey)) throw new ArgumentException("ApiKey é obrigatório", nameof(options));
        _options = options;
        _baseUrl = options.BaseUrl.TrimEnd('/');
        // o timeout é controlado por requisição; conexões são recicladas para acompanhar mudanças de DNS
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AllowAutoRedirect = false,
        })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public static string Query(params (string Key, object? Value)[] pairs)
    {
        var parts = pairs
            .Where(p => p.Value is not null)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(Format(p.Value!))}")
            .ToArray();
        return parts.Length == 0 ? "" : "?" + string.Join("&", parts);

        static string Format(object value) => value switch
        {
            bool b => b ? "true" : "false",
            DateTimeOffset d => d.ToUniversalTime().ToString("o"),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "",
        };
    }

    public Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, true, ct);
    public Task<T> PostAsync<T>(string path, object? body, CancellationToken ct, bool idempotent = false) =>
        SendAsync<T>(HttpMethod.Post, path, body ?? new { }, idempotent, ct);
    public Task<T> PatchAsync<T>(string path, object body, CancellationToken ct) => SendAsync<T>(HttpMethod.Patch, path, body, false, ct);
    public Task DeleteAsync(string path, CancellationToken ct) => SendAsync<JsonElement?>(HttpMethod.Delete, path, null, true, ct);
    public Task<T> DeleteAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Delete, path, null, true, ct);

    public async Task<byte[]> GetBytesAsync(string path, CancellationToken ct)
    {
        using var response = await ExecuteAsync(HttpMethod.Get, path, null, true, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, bool canRetry, CancellationToken ct)
    {
        using var response = await ExecuteAsync(method, path, body, canRetry, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text)) return default!;
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    /// <summary>Devolve uma resposta de sucesso; lança <see cref="ZapiException"/> em qualquer outro caso.</summary>
    private async Task<HttpResponseMessage> ExecuteAsync(HttpMethod method, string path, object? body, bool canRetry, CancellationToken ct)
    {
        var payload = body is null ? null : JsonSerializer.Serialize(body, Json);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, _baseUrl + path);
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (payload is not null) request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                ct.ThrowIfCancellationRequested(); // cancelamento pedido por quem chamou não é erro da API
                if (canRetry && attempt < _options.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                var reason = ex is OperationCanceledException ? $"timeout após {_options.Timeout.TotalSeconds:0}s" : ex.Message;
                throw new ZapiException($"{method} {path} falhou: {reason}", 0, null, ex);
            }

            var status = (int)response.StatusCode;
            if (status is >= 200 and < 400) return response;

            if (canRetry && status is 429 or 502 or 503 or 504 && attempt < _options.MaxRetries)
            {
                response.Dispose();
                await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            string? message = null;
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
            }
            catch (JsonException) { }
            throw new ZapiException(message ?? (string.IsNullOrEmpty(text) ? $"HTTP {status}" : text), status, text);
        }
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt), 8000) + Random.Shared.Next(250));
}
