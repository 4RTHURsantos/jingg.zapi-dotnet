using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Jingg.Zapi;

/// <summary>Lançada por <see cref="ZapiWebhook.Parse(string, string?, string?)"/> quando a assinatura não confere.</summary>
public sealed class ZapiWebhookSignatureException() : Exception("assinatura de webhook inválida");

/// <summary>Helpers para o lado que RECEBE os webhooks.</summary>
public static class ZapiWebhook
{
    /// <summary>Header com a assinatura.</summary>
    public const string SignatureHeader = "X-Zapi-Signature";
    /// <summary>Header com o id do evento (para deduplicar sem ler o corpo).</summary>
    public const string EventIdHeader = "X-Zapi-Event-Id";

    /// <summary>Valor esperado do header de assinatura para este corpo.</summary>
    public static string Sign(string secret, ReadOnlySpan<byte> rawBody) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody)).ToLowerInvariant();

    /// <summary>
    /// Confere o header <c>X-Zapi-Signature</c> contra o corpo BRUTO da requisição (os bytes como chegaram —
    /// não um objeto re-serializado). Comparação em tempo constante.
    /// </summary>
    public static bool VerifySignature(string secret, ReadOnlySpan<byte> rawBody, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(secret, rawBody)), Encoding.UTF8.GetBytes(signatureHeader));
    }

    public static bool VerifySignature(string secret, string rawBody, string? signatureHeader) =>
        VerifySignature(secret, Encoding.UTF8.GetBytes(rawBody), signatureHeader);

    /// <summary>
    /// Valida (quando há <paramref name="secret"/>) e converte o corpo de um webhook em evento.
    /// <code>
    /// app.MapPost("/webhooks/whatsapp", async (HttpRequest req) =>
    /// {
    ///     using var reader = new StreamReader(req.Body);
    ///     var ev = ZapiWebhook.Parse(await reader.ReadToEndAsync(), secret, req.Headers[ZapiWebhook.SignatureHeader]);
    ///     if (ev.Message is { FromMe: false } msg) { /* ... */ }
    ///     return Results.Ok();
    /// });
    /// </code>
    /// </summary>
    /// <exception cref="ZapiWebhookSignatureException">A assinatura não confere.</exception>
    public static ZapiEvent Parse(string rawBody, string? secret = null, string? signatureHeader = null)
    {
        if (!string.IsNullOrEmpty(secret) && !VerifySignature(secret, rawBody, signatureHeader)) throw new ZapiWebhookSignatureException();
        return JsonSerializer.Deserialize<ZapiEvent>(rawBody, ZapiHttp.Json)!;
    }
}

public static class ZapiServiceCollectionExtensions
{
    /// <summary>
    /// Registra um <see cref="ZapiClient"/> singleton.
    /// <code>services.AddJinggZapi(o => { o.BaseUrl = cfg["Zapi:Url"]!; o.ApiKey = cfg["Zapi:AdminKey"]!; });</code>
    /// </summary>
    public static IServiceCollection AddJinggZapi(this IServiceCollection services, Action<ZapiClientOptions> configure)
    {
        var options = new ZapiClientOptions();
        configure(options);
        return services.AddSingleton(new ZapiClient(options));
    }

    public static IServiceCollection AddJinggZapi(this IServiceCollection services, string baseUrl, string apiKey) =>
        services.AddJinggZapi(o => { o.BaseUrl = baseUrl; o.ApiKey = apiKey; });
}
