using System.Net;
using System.Text;
using System.Text.Json;

namespace Jingg.Zapi.Client.Tests;

/// <summary>HttpMessageHandler falso: registra as chamadas e responde com o que o teste definir.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, int, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, JsonElement? Body, string? ApiKey)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((
            request.Method,
            request.RequestUri!.PathAndQuery,
            text is null ? null : JsonDocument.Parse(text).RootElement.Clone(),
            request.Headers.TryGetValues("x-api-key", out var key) ? key.First() : null));
        var (status, body) = respond(request, Calls.Count);
        return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    public ZapiClient Client(int maxRetries = 3) =>
        new(new ZapiClientOptions { BaseUrl = "http://zapi.test/", ApiKey = "k", MaxRetries = maxRetries }, new HttpClient(this));
}

public class ClientTests
{
    private const string Queued = """{"id":"o1","to":"1@s.whatsapp.net","type":"text","status":"queued","attempts":0,"createdAt":"2026-11-11T12:00:00.000Z"}""";

    [Fact]
    public async Task Envia_a_chave_e_monta_o_caminho_do_dispositivo()
    {
        var fake = new FakeHandler((_, _) => (HttpStatusCode.Accepted, Queued));
        var sent = await fake.Client().Device("dev 1").Messages.SendTextAsync("5511999990000", "oi", new SendOptions { QuotedMessageId = "Q" });

        Assert.Equal("o1", sent.Id);
        Assert.Equal(OutboundStatus.Queued, sent.Status);
        var call = Assert.Single(fake.Calls);
        Assert.Equal("/v1/devices/dev%201/messages", call.Path);
        Assert.Equal("k", call.ApiKey);
        var body = call.Body!.Value;
        Assert.Equal("text", body.GetProperty("type").GetString());
        Assert.Equal("oi", body.GetProperty("text").GetString());
        Assert.Equal("Q", body.GetProperty("quotedMessageId").GetString());
        Assert.True(Guid.TryParse(body.GetProperty("idempotencyKey").GetString(), out _));
        // campos não informados não podem ir como null
        Assert.False(body.TryGetProperty("mentions", out _));
        Assert.False(body.TryGetProperty("mentionAll", out _));
    }

    [Fact]
    public async Task Envio_e_repetido_em_503_com_a_MESMA_chave_de_idempotencia()
    {
        var fake = new FakeHandler((_, n) => n < 3 ? (HttpStatusCode.ServiceUnavailable, """{"message":"down"}""") : (HttpStatusCode.Accepted, Queued));
        await fake.Client().Device("d").Messages.SendTextAsync("1", "x");

        Assert.Equal(3, fake.Calls.Count);
        Assert.Single(fake.Calls.Select(c => c.Body!.Value.GetProperty("idempotencyKey").GetString()).Distinct());
    }

    [Fact]
    public async Task Post_nao_idempotente_nao_e_repetido_e_o_erro_traz_status_e_mensagem()
    {
        var fake = new FakeHandler((_, _) => (HttpStatusCode.ServiceUnavailable, """{"statusCode":503,"message":"nope"}"""));
        var ex = await Assert.ThrowsAsync<ZapiException>(() => fake.Client().Device("d").Groups.CreateAsync("g", ["1"]));
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal("nope", ex.Message);
        Assert.Single(fake.Calls);

        var conflict = new FakeHandler((_, _) => (HttpStatusCode.Conflict, """{"message":"device is not connected to WhatsApp"}"""));
        var notConnected = await Assert.ThrowsAsync<ZapiException>(() => conflict.Client().Device("d").Groups.ListAsync());
        Assert.True(notConnected.IsNotConnected);
        Assert.Single(conflict.Calls); // 4xx nunca é repetido
    }

    [Fact]
    public async Task Midia_em_bytes_vai_como_base64_e_opcoes_de_midia_entram_no_corpo()
    {
        var fake = new FakeHandler((_, _) => (HttpStatusCode.Accepted, Queued));
        await fake.Client().Device("d").Messages.SendDocumentAsync("1", MediaSource.FromBytes("ola mundo"u8), new MediaOptions { FileName = "a.txt", Caption = "veja" });

        var body = fake.Calls[0].Body!.Value;
        Assert.Equal("b2xhIG11bmRv", body.GetProperty("media").GetProperty("base64").GetString());
        Assert.Equal("a.txt", body.GetProperty("fileName").GetString());
        Assert.Equal("veja", body.GetProperty("caption").GetString());
        Assert.False(body.TryGetProperty("ptt", out _));
    }

    [Fact]
    public async Task Stream_segue_o_cursor_filtra_tipos_e_para_no_cancelamento()
    {
        var all = Enumerable.Range(1, 5).Select(i => new { id = $"e{i}", seq = i, deviceId = "d", type = i % 2 == 0 ? "message.status" : "message.received", timestamp = "2026-11-11T12:00:00Z", data = new { text = $"m{i}" } }).ToArray();
        var fake = new FakeHandler((req, _) =>
        {
            var after = long.Parse(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["after"]!);
            var events = all.Where(e => e.seq > after).Take(2).ToArray();
            return (HttpStatusCode.OK, JsonSerializer.Serialize(new { events, lastSeq = events.LastOrDefault()?.seq ?? after, hasMore = events.Length == 2 }));
        });

        using var cts = new CancellationTokenSource();
        var seen = new List<(long Seq, string? Text)>();
        await foreach (var ev in fake.Client().Device("d").Events.StreamAsync(after: 1, types: [ZapiEventTypes.MessageReceived], pollInterval: TimeSpan.FromMilliseconds(20), pageSize: 2, cancellationToken: cts.Token))
        {
            seen.Add((ev.Seq, ev.Message?.Text));
            if (ev.Seq == 5) cts.Cancel();
        }

        Assert.Equal([(3L, "m3"), (5L, "m5")], seen);
        Assert.Equal(["after=1", "after=3"], fake.Calls.Select(c => c.Path.Split('?')[1].Split('&')[0]));
    }

    [Fact]
    public async Task Api_inalcancavel_vira_ZapiException_com_status_0()
    {
        var zapi = new ZapiClient(new ZapiClientOptions { BaseUrl = "http://127.0.0.1:1", ApiKey = "k", MaxRetries = 1, Timeout = TimeSpan.FromSeconds(5) });
        var ex = await Assert.ThrowsAsync<ZapiException>(() => zapi.Devices.ListAsync());
        Assert.Equal(0, ex.StatusCode);
        Assert.False(await zapi.IsReadyAsync());
    }

    [Fact]
    public async Task Cancelamento_de_quem_chama_nao_vira_ZapiException()
    {
        var fake = new FakeHandler((_, _) => (HttpStatusCode.OK, "[]"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fake.Client().Devices.ListAsync(cts.Token));
    }

    [Fact]
    public void Webhook_assinatura_e_parse()
    {
        const string body = """{"id":"e1","seq":7,"deviceId":"d","type":"message.received","timestamp":"2026-11-11T12:00:00.000Z","data":{"id":"M1","chat":"5511@s.whatsapp.net","sender":"5511@s.whatsapp.net","fromMe":false,"isGroup":false,"timestamp":"2026-11-11T11:59:59.000Z","type":"image","text":"olá","media":{"status":"stored","size":10,"url":"http://x"}}}""";
        var signature = ZapiWebhook.Sign("s3gr3do", Encoding.UTF8.GetBytes(body));
        Assert.Matches("^sha256=[0-9a-f]{64}$", signature);

        Assert.True(ZapiWebhook.VerifySignature("s3gr3do", body, signature));
        Assert.False(ZapiWebhook.VerifySignature("outro", body, signature));
        Assert.False(ZapiWebhook.VerifySignature("s3gr3do", body + " ", signature));
        Assert.False(ZapiWebhook.VerifySignature("s3gr3do", body, null));

        var ev = ZapiWebhook.Parse(body, "s3gr3do", signature);
        Assert.Equal(7, ev.Seq);
        Assert.True(ev.IsMessage);
        Assert.Equal("olá", ev.Message!.Text);
        Assert.Equal("stored", ev.Message.Media!.Status);
        Assert.Null(ev.Connection);
        Assert.Throws<ZapiWebhookSignatureException>(() => ZapiWebhook.Parse(body, "s3gr3do", "sha256=00"));
    }

    [Fact]
    public void Assinatura_bate_com_valor_conhecido()
    {
        // HMAC-SHA256(key="key", msg="The quick brown fox jumps over the lazy dog") — vetor de teste público
        Assert.Equal(
            "sha256=f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8",
            ZapiWebhook.Sign("key", "The quick brown fox jumps over the lazy dog"u8));
    }
}

/// <summary>Roda contra uma API de verdade. Ignorado sem ZAPI_URL e ZAPI_ADMIN_KEY.</summary>
public class IntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("ZAPI_URL");
    private static readonly string? AdminKey = Environment.GetEnvironmentVariable("ZAPI_ADMIN_KEY");

    [Fact]
    public async Task Ida_e_volta_na_api_real()
    {
        if (string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(AdminKey)) return; // sem ambiente: nada a verificar

        var zapi = new ZapiClient(Url, AdminKey);
        Assert.True(await zapi.IsReadyAsync());

        var created = await zapi.Devices.CreateAsync(new CreateDeviceRequest { Name = $"sdk-dotnet-{Guid.NewGuid():N}", Connect = false });
        Assert.StartsWith("zk_", created.ApiKey);
        var device = zapi.Device(created.Id);
        try
        {
            var again = await zapi.Devices.GetOrCreateAsync(created.Name);
            Assert.Equal(created.Id, again.Device.Id);
            Assert.False(again.Created);

            // a chave do próprio dispositivo serve para as rotas dele
            var scoped = new ZapiClient(Url, created.ApiKey).Device(created.Id);
            Assert.Equal(created.Name, (await scoped.GetAsync()).Name);

            var hook = await device.Webhooks.EnsureAsync(new WebhookInput { Url = "http://127.0.0.1:9/none", Secret = "x", Events = [ZapiEventTypes.MessageReceived] });
            Assert.Equal(hook.Id, (await device.Webhooks.EnsureAsync(new WebhookInput { Url = "http://127.0.0.1:9/none", Enabled = false })).Id);
            var hooks = await device.Webhooks.ListAsync();
            Assert.False(Assert.Single(hooks).Enabled);
            Assert.False((await device.Webhooks.TestAsync(hook.Id)).Ok);

            var qr = await device.ConnectAndGetQrAsync();
            Assert.StartsWith("data:image/png;base64,", qr!.Image);
            var png = await device.GetQrPngAsync();
            Assert.Equal("PNG"u8.ToArray(), png[1..4]);

            var sent = await device.Messages.SendTextAsync("5511999990000", "teste", new SendOptions { IdempotencyKey = "k-1" });
            Assert.Equal(OutboundStatus.Queued, sent.Status);
            var dup = await device.Messages.SendTextAsync("5511999990000", "teste", new SendOptions { IdempotencyKey = "k-1" });
            Assert.Equal(sent.Id, dup.Id);
            Assert.True(dup.Duplicate);
            Assert.Equal(OutboundStatus.Cancelled, (await device.Messages.CancelAsync(sent.Id)).Status);
            Assert.Single(await device.Messages.ListOutboundAsync(OutboundStatus.Cancelled));

            var page = await device.Events.ListAsync();
            Assert.Contains(page.Events, e => e.Type == ZapiEventTypes.QrUpdated);
            Assert.Contains(page.Events, e => e.Connection?.Status == DeviceStatus.Qr);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var seqs = new List<long>();
            await foreach (var ev in device.Events.StreamAsync(pollInterval: TimeSpan.FromMilliseconds(100), cancellationToken: cts.Token))
            {
                seqs.Add(ev.Seq);
                if (ev.Seq >= page.LastSeq) cts.Cancel();
            }
            Assert.Equal(page.Events.Select(e => e.Seq), seqs.Take(page.Events.Count));

            Assert.True((await Assert.ThrowsAsync<ZapiException>(() => device.Groups.ListAsync())).IsNotConnected);
            Assert.True((await Assert.ThrowsAsync<ZapiException>(() => zapi.Device("00000000-0000-4000-8000-000000000000").GetAsync())).IsNotFound);
        }
        finally
        {
            await zapi.Devices.DeleteAsync(created.Id);
        }
    }
}
