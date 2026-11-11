using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Jingg.Zapi;

/// <summary>
/// Ponto de entrada. Thread-safe; crie um e reutilize (ou registre com <c>services.AddJinggZapi(...)</c>).
/// <code>
/// var zapi = new ZapiClient("http://localhost:3000", adminKey);
/// var created = await zapi.Devices.CreateAsync(new CreateDeviceRequest { Name = "atendimento" });
/// await zapi.Device(created.Id).Messages.SendTextAsync("5511999990000", "Olá!");
/// </code>
/// </summary>
public sealed class ZapiClient
{
    private readonly ZapiHttp _http;

    public ZapiClient(string baseUrl, string apiKey, HttpClient? httpClient = null)
        : this(new ZapiClientOptions { BaseUrl = baseUrl, ApiKey = apiKey }, httpClient) { }

    /// <param name="options">Endereço, chave, timeout e retentativas.</param>
    /// <param name="httpClient">Opcional: um HttpClient seu (testes, proxy). O timeout dele é ignorado em favor de <see cref="ZapiClientOptions.Timeout"/>.</param>
    public ZapiClient(ZapiClientOptions options, HttpClient? httpClient = null)
    {
        _http = new ZapiHttp(options, httpClient);
        Devices = new DevicesApi(_http);
    }

    public DevicesApi Devices { get; }

    /// <summary>Tudo o que é de um dispositivo. Nenhuma requisição é feita até você chamar um método.</summary>
    public DeviceClient Device(string deviceId) => new(_http, deviceId);

    /// <summary>true quando a API e suas dependências (Postgres, Redis, storage) estão no ar.</summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _http.GetAsync<JsonElement>("/ready", cancellationToken).ConfigureAwait(false);
            return res.TryGetProperty("status", out var status) && status.GetString() == "ok";
        }
        catch (ZapiException)
        {
            return false;
        }
    }
}

public sealed class DevicesApi
{
    private readonly ZapiHttp _http;
    internal DevicesApi(ZapiHttp http) => _http = http;

    /// <summary>Requer a chave de administrador. Guarde a <see cref="CreatedDevice.ApiKey"/>: ela só aparece aqui.</summary>
    public Task<CreatedDevice> CreateAsync(CreateDeviceRequest request, CancellationToken cancellationToken = default) =>
        _http.PostAsync<CreatedDevice>("/v1/devices", request, cancellationToken);

    /// <summary>Com chave de dispositivo, lista apenas ele.</summary>
    public Task<List<Device>> ListAsync(CancellationToken cancellationToken = default) =>
        _http.GetAsync<List<Device>>("/v1/devices", cancellationToken);

    public Task<DeviceDetails> GetAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _http.GetAsync<DeviceDetails>($"/v1/devices/{Uri.EscapeDataString(deviceId)}", cancellationToken);

    /// <summary>Procura um dispositivo pelo nome ou cria. Prático na inicialização da aplicação. A ApiKey só vem quando acabou de ser criado.</summary>
    public async Task<DeviceLookup> GetOrCreateAsync(string name, WebhookInput? webhook = null, bool? connect = null, CancellationToken cancellationToken = default)
    {
        var existing = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Name == name);
        if (existing is not null) return new DeviceLookup(existing, false, null);
        var created = await CreateAsync(new CreateDeviceRequest { Name = name, Webhook = webhook, Connect = connect }, cancellationToken).ConfigureAwait(false);
        return new DeviceLookup(created, true, created.ApiKey);
    }

    /// <summary>Requer a chave de administrador. Desloga do WhatsApp (se conectado) e apaga os dados do dispositivo.</summary>
    public Task DeleteAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _http.DeleteAsync($"/v1/devices/{Uri.EscapeDataString(deviceId)}", cancellationToken);
}

public sealed class DeviceClient
{
    private readonly ZapiHttp _http;
    private readonly string _base;

    internal DeviceClient(ZapiHttp http, string deviceId)
    {
        _http = http;
        Id = deviceId;
        _base = $"/v1/devices/{Uri.EscapeDataString(deviceId)}";
        Messages = new MessagesApi(http, _base);
        Webhooks = new WebhooksApi(http, _base);
        Events = new EventsApi(http, _base);
        Contacts = new ContactsApi(http, _base);
        Groups = new GroupsApi(http, _base);
    }

    public string Id { get; }
    public MessagesApi Messages { get; }
    public WebhooksApi Webhooks { get; }
    public EventsApi Events { get; }
    public ContactsApi Contacts { get; }
    public GroupsApi Groups { get; }

    public Task<DeviceDetails> GetAsync(CancellationToken cancellationToken = default) => _http.GetAsync<DeviceDetails>(_base, cancellationToken);
    public Task<Device> RenameAsync(string name, CancellationToken cancellationToken = default) => _http.PatchAsync<Device>(_base, new { name }, cancellationToken);

    /// <summary>Inicia a conexão. Sem sessão salva, o dispositivo passa a gerar QR codes.</summary>
    public Task<Device> ConnectAsync(CancellationToken cancellationToken = default) => _http.PostAsync<Device>($"{_base}/connect", null, cancellationToken);
    /// <summary>Fecha a conexão mantendo a sessão: <see cref="ConnectAsync"/> retoma sem novo QR.</summary>
    public Task<Device> DisconnectAsync(CancellationToken cancellationToken = default) => _http.PostAsync<Device>($"{_base}/disconnect", null, cancellationToken);
    public Task<Device> RestartAsync(CancellationToken cancellationToken = default) => _http.PostAsync<Device>($"{_base}/restart", null, cancellationToken);
    /// <summary>Desvincula do WhatsApp e apaga a sessão; será preciso parear de novo.</summary>
    public Task LogoutAsync(CancellationToken cancellationToken = default) => _http.PostAsync<JsonElement>($"{_base}/logout", null, cancellationToken);

    /// <summary>Gera uma nova chave para o dispositivo; a anterior deixa de valer.</summary>
    public async Task<string> RotateKeyAsync(CancellationToken cancellationToken = default) =>
        (await _http.PostAsync<JsonElement>($"{_base}/rotate-key", null, cancellationToken).ConfigureAwait(false)).GetProperty("apiKey").GetString()!;

    /// <summary>QR atual. Lança <see cref="ZapiException"/> 404 enquanto ainda não há um disponível.</summary>
    public Task<QrCode> GetQrAsync(CancellationToken cancellationToken = default) => _http.GetAsync<QrCode>($"{_base}/qr", cancellationToken);
    /// <summary>QR atual como bytes PNG.</summary>
    public Task<byte[]> GetQrPngAsync(CancellationToken cancellationToken = default) => _http.GetBytesAsync($"{_base}/qr?format=png", cancellationToken);

    /// <summary>Pareamento por código em vez de QR. No celular: Dispositivos conectados → Conectar com número de telefone.</summary>
    public async Task<string> GetPairingCodeAsync(string phone, CancellationToken cancellationToken = default) =>
        (await _http.PostAsync<JsonElement>($"{_base}/pairing-code", new { phone }, cancellationToken).ConfigureAwait(false)).GetProperty("code").GetString()!;

    /// <summary>Inicia o dispositivo se preciso e devolve o QR assim que houver um (ou <c>null</c> se já estiver conectado).</summary>
    public async Task<QrCode?> ConnectAndGetQrAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            try
            {
                return await GetQrAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ZapiException ex) when (ex.StatusCode is 404 or 409)
            {
                if ((await GetAsync(cancellationToken).ConfigureAwait(false)).IsConnected) return null;
                if (DateTimeOffset.UtcNow > deadline) throw new ZapiException("tempo esgotado aguardando o QR code", 0);
            }
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Conclui quando o dispositivo está conectado ao WhatsApp. Lança em timeout, ou se ele acabar deslogado/parado.</summary>
    public async Task<DeviceDetails> WaitUntilConnectedAsync(TimeSpan? timeout = null, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(2));
        while (true)
        {
            var device = await GetAsync(cancellationToken).ConfigureAwait(false);
            if (device.IsConnected) return device;
            if (device.Status == DeviceStatus.LoggedOut) throw new ZapiException("o dispositivo foi deslogado", 0);
            if (device.DesiredState == "stopped") throw new ZapiException($"o dispositivo está parado ({device.StatusReason ?? "chame ConnectAsync"})", 0);
            if (DateTimeOffset.UtcNow > deadline) throw new ZapiException($"tempo esgotado aguardando a conexão (status: {device.Status})", 0);
            await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Nome e/ou recado do perfil do WhatsApp.</summary>
    public Task UpdateProfileAsync(string? name = null, string? status = null, CancellationToken cancellationToken = default) =>
        _http.PatchAsync<JsonElement>($"{_base}/profile", new { name, status }, cancellationToken);

    /// <summary><c>composing</c>, <c>recording</c> e <c>paused</c> exigem <paramref name="to"/>; <c>available</c>/<c>unavailable</c> valem para a conta.</summary>
    public Task SendPresenceAsync(string state, string? to = null, CancellationToken cancellationToken = default) =>
        _http.PostAsync<JsonElement>($"{_base}/presence", new { state, to }, cancellationToken);

    /// <summary>Use <c>id</c> e <c>from</c> de um evento <c>call.update</c>.</summary>
    public Task RejectCallAsync(string callId, string from, CancellationToken cancellationToken = default) =>
        _http.PostAsync<JsonElement>($"{_base}/calls/reject", new { callId, from }, cancellationToken);
}

public sealed class MessagesApi
{
    private readonly ZapiHttp _http;
    private readonly string _base;
    internal MessagesApi(ZapiHttp http, string @base) => (_http, _base) = (http, @base);

    private Task<OutboundMessage> SendAsync(string to, string type, Dictionary<string, object?> fields, SendOptions? options, CancellationToken ct)
    {
        fields["to"] = to;
        fields["type"] = type;
        fields["quotedMessageId"] = options?.QuotedMessageId;
        fields["mentions"] = options?.Mentions;
        fields["mentionAll"] = options?.MentionAll;
        // sempre há chave de idempotência: é o que permite repetir o POST com segurança após uma falha de rede
        fields["idempotencyKey"] = options?.IdempotencyKey ?? Guid.NewGuid().ToString();
        if (options is MediaOptions media)
        {
            fields["caption"] = media.Caption;
            fields["mimetype"] = media.Mimetype;
            fields["fileName"] = media.FileName;
            fields["ptt"] = media.Ptt;
            fields["gif"] = media.Gif;
            fields["viewOnce"] = media.ViewOnce;
        }
        // valores nulos de dicionário seriam enviados como null; a API espera o campo ausente
        var body = fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value);
        return _http.PostAsync<OutboundMessage>($"{_base}/messages", body, ct, idempotent: true);
    }

    private Task<OutboundMessage> SendMediaAsync(string to, string type, MediaSource media, SendOptions? options, CancellationToken ct) =>
        SendAsync(to, type, new() { ["media"] = media.ToPayload() }, options, ct);

    /// <summary>
    /// Enfileira um texto. Conclui quando a API gravou a mensagem — a entrega é assíncrona
    /// (acompanhe com <see cref="GetOutboundAsync"/>, <see cref="WaitUntilSentAsync"/> ou pelos eventos message.*).
    /// </summary>
    public Task<OutboundMessage> SendTextAsync(string to, string text, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync(to, "text", new() { ["text"] = text }, options, cancellationToken);

    public Task<OutboundMessage> SendImageAsync(string to, MediaSource media, MediaOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMediaAsync(to, "image", media, options, cancellationToken);
    public Task<OutboundMessage> SendVideoAsync(string to, MediaSource media, MediaOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMediaAsync(to, "video", media, options, cancellationToken);
    /// <summary>Com <see cref="MediaOptions.Ptt"/> = true vai como mensagem de voz.</summary>
    public Task<OutboundMessage> SendAudioAsync(string to, MediaSource media, MediaOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMediaAsync(to, "audio", media, options, cancellationToken);
    public Task<OutboundMessage> SendDocumentAsync(string to, MediaSource media, MediaOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMediaAsync(to, "document", media, options, cancellationToken);
    public Task<OutboundMessage> SendStickerAsync(string to, MediaSource media, MediaOptions? options = null, CancellationToken cancellationToken = default) =>
        SendMediaAsync(to, "sticker", media, options, cancellationToken);

    public Task<OutboundMessage> SendLocationAsync(string to, double latitude, double longitude, string? name = null, string? address = null, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync(to, "location", new() { ["latitude"] = latitude, ["longitude"] = longitude, ["name"] = name, ["address"] = address }, options, cancellationToken);

    public Task<OutboundMessage> SendContactsAsync(string to, IEnumerable<ContactCard> contacts, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync(to, "contact", new() { ["contacts"] = contacts.ToArray() }, options, cancellationToken);

    public Task<OutboundMessage> SendPollAsync(string to, string name, IEnumerable<string> pollOptions, int selectableCount = 1, SendOptions? options = null, CancellationToken cancellationToken = default) =>
        SendAsync(to, "poll", new() { ["poll"] = new { name, options = pollOptions.ToArray(), selectableCount } }, options, cancellationToken);

    /// <summary>Emoji vazio remove a reação.</summary>
    public Task<OutboundMessage> ReactAsync(string chat, string messageId, string emoji, CancellationToken cancellationToken = default) =>
        SendAsync(chat, "reaction", new() { ["messageId"] = messageId, ["emoji"] = emoji }, null, cancellationToken);

    public Task<OutboundMessage> EditAsync(string chat, string messageId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(chat, "edit", new() { ["messageId"] = messageId, ["text"] = text }, null, cancellationToken);

    /// <summary>Apaga para todos.</summary>
    public Task<OutboundMessage> DeleteForEveryoneAsync(string chat, string messageId, CancellationToken cancellationToken = default) =>
        SendAsync(chat, "delete", new() { ["messageId"] = messageId }, null, cancellationToken);

    /// <summary>Situação de uma mensagem enfileirada pela API.</summary>
    public Task<OutboundMessage> GetOutboundAsync(string outboundId, CancellationToken cancellationToken = default) =>
        _http.GetAsync<OutboundMessage>($"{_base}/messages/outbound/{Uri.EscapeDataString(outboundId)}", cancellationToken);

    public Task<List<OutboundMessage>> ListOutboundAsync(string? status = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _http.GetAsync<List<OutboundMessage>>($"{_base}/messages/outbound{ZapiHttp.Query(("status", status), ("limit", limit))}", cancellationToken);

    /// <summary>Só funciona enquanto a mensagem ainda está na fila.</summary>
    public Task<OutboundMessage> CancelAsync(string outboundId, CancellationToken cancellationToken = default) =>
        _http.DeleteAsync<OutboundMessage>($"{_base}/messages/outbound/{Uri.EscapeDataString(outboundId)}", cancellationToken);

    /// <summary>Recoloca na fila uma mensagem que falhou ou foi cancelada.</summary>
    public Task<OutboundMessage> RetryAsync(string outboundId, CancellationToken cancellationToken = default) =>
        _http.PostAsync<OutboundMessage>($"{_base}/messages/outbound/{Uri.EscapeDataString(outboundId)}/retry", null, cancellationToken);

    /// <summary>Aguarda a mensagem sair da fila. Conclui quando enviada; lança se falhou, foi cancelada ou no timeout.</summary>
    public async Task<OutboundMessage> WaitUntilSentAsync(string outboundId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(1));
        while (true)
        {
            var message = await GetOutboundAsync(outboundId, cancellationToken).ConfigureAwait(false);
            if (message.Status == OutboundStatus.Sent) return message;
            if (message.Status is OutboundStatus.Failed or OutboundStatus.Cancelled)
                throw new ZapiException($"mensagem {message.Status}: {message.LastError ?? "sem detalhes"}", 0);
            if (DateTimeOffset.UtcNow > deadline) throw new ZapiException($"tempo esgotado aguardando o envio (status: {message.Status})", 0);
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Histórico armazenado (recebidas e enviadas), da mais nova para a mais antiga.</summary>
    public Task<List<Message>> ListAsync(string? chat = null, DateTimeOffset? before = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _http.GetAsync<List<Message>>($"{_base}/messages{ZapiHttp.Query(("chat", chat), ("before", before), ("limit", limit))}", cancellationToken);

    public Task<Message> GetAsync(string messageId, bool raw = false, CancellationToken cancellationToken = default) =>
        _http.GetAsync<Message>($"{_base}/messages/{Uri.EscapeDataString(messageId)}{ZapiHttp.Query(("raw", raw ? true : null))}", cancellationToken);

    /// <summary>URL temporária nova para a mídia da mensagem (a que vem nos eventos expira).</summary>
    public Task<MediaUrl> GetMediaUrlAsync(string messageId, CancellationToken cancellationToken = default) =>
        _http.GetAsync<MediaUrl>($"{_base}/messages/{Uri.EscapeDataString(messageId)}/media?redirect=false", cancellationToken);

    /// <summary>Marca como lida. <paramref name="participant"/> (o autor) é obrigatório em grupos.</summary>
    public Task MarkReadAsync(string chat, string messageId, string? participant = null, CancellationToken cancellationToken = default) =>
        _http.PostAsync<JsonElement>($"{_base}/messages/read", new { messages = new[] { new { chat, id = messageId, participant } } }, cancellationToken);
}

public sealed class WebhooksApi
{
    private readonly ZapiHttp _http;
    private readonly string _base;
    internal WebhooksApi(ZapiHttp http, string @base) => (_http, _base) = (http, @base);

    public Task<List<Webhook>> ListAsync(CancellationToken cancellationToken = default) =>
        _http.GetAsync<List<Webhook>>($"{_base}/webhooks", cancellationToken);

    public Task<Webhook> CreateAsync(WebhookInput input, CancellationToken cancellationToken = default) =>
        _http.PostAsync<Webhook>($"{_base}/webhooks", input, cancellationToken);

    /// <summary>Só os campos preenchidos são alterados.</summary>
    public Task<Webhook> UpdateAsync(string webhookId, WebhookUpdate changes, CancellationToken cancellationToken = default) =>
        _http.PatchAsync<Webhook>($"{_base}/webhooks/{Uri.EscapeDataString(webhookId)}", changes, cancellationToken);

    public Task DeleteAsync(string webhookId, CancellationToken cancellationToken = default) =>
        _http.DeleteAsync($"{_base}/webhooks/{Uri.EscapeDataString(webhookId)}", cancellationToken);

    /// <summary>Cria o webhook desta URL ou atualiza o existente. Pode ser chamado a cada inicialização.</summary>
    public async Task<Webhook> EnsureAsync(WebhookInput input, CancellationToken cancellationToken = default)
    {
        var existing = (await ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(w => w.Url == input.Url);
        if (existing is null) return await CreateAsync(input, cancellationToken).ConfigureAwait(false);
        var changes = new WebhookUpdate { Url = input.Url, Secret = input.Secret, Events = input.Events, Headers = input.Headers, Enabled = input.Enabled };
        return await UpdateAsync(existing.Id, changes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Envia um evento <c>ping</c> agora e informa como o seu endpoint respondeu.</summary>
    public Task<WebhookTestResult> TestAsync(string webhookId, CancellationToken cancellationToken = default) =>
        _http.PostAsync<WebhookTestResult>($"{_base}/webhooks/{Uri.EscapeDataString(webhookId)}/test", null, cancellationToken);

    public Task<List<WebhookDelivery>> ListDeliveriesAsync(string? status = null, string? webhookId = null, int? limit = null, CancellationToken cancellationToken = default) =>
        _http.GetAsync<List<WebhookDelivery>>($"{_base}/webhook-deliveries{ZapiHttp.Query(("status", status), ("webhookId", webhookId), ("limit", limit))}", cancellationToken);

    public Task RetryDeliveryAsync(string deliveryId, CancellationToken cancellationToken = default) =>
        _http.PostAsync<JsonElement>($"{_base}/webhook-deliveries/{Uri.EscapeDataString(deliveryId)}/retry", null, cancellationToken);

    /// <summary>Reenfileira todas as entregas que esgotaram as tentativas. Devolve quantas.</summary>
    public async Task<int> RetryFailedDeliveriesAsync(CancellationToken cancellationToken = default) =>
        (await _http.PostAsync<JsonElement>($"{_base}/webhook-deliveries/retry-failed", null, cancellationToken).ConfigureAwait(false)).GetProperty("requeued").GetInt32();
}

public sealed class EventsApi
{
    private readonly ZapiHttp _http;
    private readonly string _base;
    internal EventsApi(ZapiHttp http, string @base) => (_http, _base) = (http, @base);

    /// <summary>Uma página do log de eventos do dispositivo, em ordem.</summary>
    public Task<EventPage> ListAsync(long after = 0, int? limit = null, IEnumerable<string>? types = null, CancellationToken cancellationToken = default) =>
        _http.GetAsync<EventPage>($"{_base}/events{ZapiHttp.Query(("after", after), ("limit", limit), ("types", types is null ? null : string.Join(",", types)))}", cancellationToken);

    /// <summary>seq do evento mais recente — passe como <c>after</c> para receber só o que acontecer daqui em diante.</summary>
    public async Task<long> GetLastSeqAsync(CancellationToken cancellationToken = default) =>
        (await _http.GetAsync<Device>(_base, cancellationToken).ConfigureAwait(false)).LastEventSeq;

    /// <summary>
    /// Fluxo contínuo e ordenado de eventos por polling — a forma de consumir o dispositivo sem webhook.
    /// Nada é pulado nem repetido desde que você retome do último <c>Seq</c> processado.
    /// Indisponibilidades da API são aguardadas; cancele o token para encerrar (o laço termina sem exceção).
    /// <code>
    /// await foreach (var ev in device.Events.StreamAsync(after: lastSeq, cancellationToken: ct))
    /// {
    ///     if (ev.Message is { FromMe: false } msg) Console.WriteLine(msg.Text);
    ///     lastSeq = ev.Seq;
    /// }
    /// </code>
    /// </summary>
    public async IAsyncEnumerable<ZapiEvent> StreamAsync(
        long after = 0,
        IEnumerable<string>? types = null,
        TimeSpan? pollInterval = null,
        int pageSize = 200,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var cursor = after;
        var wait = pollInterval ?? TimeSpan.FromSeconds(2);
        var filter = types?.ToHashSet();
        while (!cancellationToken.IsCancellationRequested)
        {
            EventPage? page = null;
            try
            {
                // os tipos são filtrados aqui, não no servidor, para o cursor sempre avançar
                page = await ListAsync(cursor, pageSize, null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (ZapiException ex) when (ex.StatusCode is 0 or 429 or >= 500)
            {
                // fora do ar: espera e tenta de novo. Erros 4xx (chave errada, dispositivo inexistente) sobem.
            }

            if (page is not null)
            {
                foreach (var ev in page.Events)
                {
                    cursor = ev.Seq;
                    if (filter is null || filter.Contains(ev.Type)) yield return ev;
                }
                if (page.HasMore) continue;
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}

public sealed class ContactsApi
{
    private readonly ZapiHttp _http;
    private readonly string _base;
    internal ContactsApi(ZapiHttp http, string @base) => (_http, _base) = (http, @base);

    private string Path(string jid, string suffix) => $"{_base}/contacts/{Uri.EscapeDataString(jid)}/{suffix}";

    /// <summary>Quais destes números têm WhatsApp, e sob qual JID.</summary>
    public Task<List<NumberCheck>> CheckAsync(IEnumerable<string> numbers, CancellationToken cancellationToken = default) =>
        _http.PostAsync<List<NumberCheck>>($"{_base}/contacts/check", new { numbers = numbers.ToArray() }, cancellationToken, idempotent: true);

    public async Task<NumberCheck> CheckAsync(string number, CancellationToken cancellationToken = default) =>
        (await CheckAsync([number], cancellationToken).ConfigureAwait(false))[0];

    public async Task<string?> GetPictureUrlAsync(string jid, CancellationToken cancellationToken = default) =>
        (await _http.GetAsync<JsonElement>(Path(jid, "picture"), cancellationToken).ConfigureAwait(false)).GetProperty("url").GetString();

    public Task<JsonElement> GetStatusAsync(string jid, CancellationToken cancellationToken = default) => _http.GetAsync<JsonElement>(Path(jid, "status"), cancellationToken);
    public Task<JsonElement> GetBusinessProfileAsync(string jid, CancellationToken cancellationToken = default) => _http.GetAsync<JsonElement>(Path(jid, "business"), cancellationToken);

    public Task BlockAsync(string jid, CancellationToken cancellationToken = default) => _http.PostAsync<JsonElement>(Path(jid, "block"), new { action = "block" }, cancellationToken);
    public Task UnblockAsync(string jid, CancellationToken cancellationToken = default) => _http.PostAsync<JsonElement>(Path(jid, "block"), new { action = "unblock" }, cancellationToken);

    /// <summary>Passa a receber eventos <c>presence.update</c> deste contato.</summary>
    public Task SubscribePresenceAsync(string jid, CancellationToken cancellationToken = default) => _http.PostAsync<JsonElement>(Path(jid, "presence/subscribe"), null, cancellationToken);
}

public sealed class GroupsApi
{
    private readonly ZapiHttp _http;
    private readonly string _base;
    internal GroupsApi(ZapiHttp http, string @base) => (_http, _base) = (http, @base);

    private string Path(string groupJid, string suffix = "") => $"{_base}/groups/{Uri.EscapeDataString(groupJid)}{suffix}";
    private static string Code(string codeOrLink) => codeOrLink.Split('/')[^1].Split('?')[0];

    public Task<List<GroupSummary>> ListAsync(CancellationToken cancellationToken = default) => _http.GetAsync<List<GroupSummary>>($"{_base}/groups", cancellationToken);
    public Task<GroupMetadata> GetAsync(string groupJid, CancellationToken cancellationToken = default) => _http.GetAsync<GroupMetadata>(Path(groupJid), cancellationToken);

    public Task<GroupMetadata> CreateAsync(string subject, IEnumerable<string> participants, CancellationToken cancellationToken = default) =>
        _http.PostAsync<GroupMetadata>($"{_base}/groups", new { subject, participants = participants.ToArray() }, cancellationToken);

    public Task<GroupMetadata> UpdateAsync(string groupJid, GroupSettings settings, CancellationToken cancellationToken = default) =>
        _http.PatchAsync<GroupMetadata>(Path(groupJid), settings, cancellationToken);

    private Task<JsonElement> ParticipantsAsync(string groupJid, string action, IEnumerable<string> participants, CancellationToken ct) =>
        _http.PostAsync<JsonElement>(Path(groupJid, "/participants"), new { action, participants = participants.ToArray() }, ct);

    public Task<JsonElement> AddParticipantsAsync(string groupJid, IEnumerable<string> participants, CancellationToken cancellationToken = default) => ParticipantsAsync(groupJid, "add", participants, cancellationToken);
    public Task<JsonElement> RemoveParticipantsAsync(string groupJid, IEnumerable<string> participants, CancellationToken cancellationToken = default) => ParticipantsAsync(groupJid, "remove", participants, cancellationToken);
    public Task<JsonElement> PromoteAsync(string groupJid, IEnumerable<string> participants, CancellationToken cancellationToken = default) => ParticipantsAsync(groupJid, "promote", participants, cancellationToken);
    public Task<JsonElement> DemoteAsync(string groupJid, IEnumerable<string> participants, CancellationToken cancellationToken = default) => ParticipantsAsync(groupJid, "demote", participants, cancellationToken);

    public Task<InviteLink> GetInviteLinkAsync(string groupJid, CancellationToken cancellationToken = default) => _http.GetAsync<InviteLink>(Path(groupJid, "/invite-code"), cancellationToken);
    /// <summary>Invalida o link atual e devolve o novo.</summary>
    public Task<InviteLink> RevokeInviteLinkAsync(string groupJid, CancellationToken cancellationToken = default) => _http.PostAsync<InviteLink>(Path(groupJid, "/invite-code/revoke"), null, cancellationToken);

    /// <summary>Dados do grupo a partir do código ou link de convite, sem entrar.</summary>
    public Task<GroupMetadata> GetInviteInfoAsync(string codeOrLink, CancellationToken cancellationToken = default) =>
        _http.GetAsync<GroupMetadata>($"{_base}/groups/invite/{Uri.EscapeDataString(Code(codeOrLink))}", cancellationToken);

    /// <summary>Entra no grupo e devolve o JID dele.</summary>
    public async Task<string?> JoinAsync(string codeOrLink, CancellationToken cancellationToken = default) =>
        (await _http.PostAsync<JsonElement>($"{_base}/groups/join", new { code = Code(codeOrLink) }, cancellationToken).ConfigureAwait(false)).GetProperty("jid").GetString();

    public Task LeaveAsync(string groupJid, CancellationToken cancellationToken = default) => _http.PostAsync<JsonElement>(Path(groupJid, "/leave"), null, cancellationToken);
}
