using System.Text.Json;

namespace Jingg.Zapi;

/// <summary>Valores de <see cref="Device.Status"/>.</summary>
public static class DeviceStatus
{
    public const string Disconnected = "disconnected";
    public const string Connecting = "connecting";
    public const string Qr = "qr";
    public const string Connected = "connected";
    public const string Reconnecting = "reconnecting";
    public const string LoggedOut = "logged_out";
}

/// <summary>Tipos de evento (webhooks e log de eventos).</summary>
public static class ZapiEventTypes
{
    public const string ConnectionUpdate = "connection.update";
    public const string QrUpdated = "qr.updated";
    public const string MessageReceived = "message.received";
    public const string MessageSent = "message.sent";
    public const string MessageStatus = "message.status";
    public const string MessageReceipt = "message.receipt";
    public const string MessageEdited = "message.edited";
    public const string MessageDeleted = "message.deleted";
    public const string MessageReaction = "message.reaction";
    public const string MessagePollVote = "message.poll_vote";
    public const string MessageFailed = "message.failed";
    public const string GroupUpdated = "group.updated";
    public const string GroupParticipants = "group.participants";
    public const string GroupJoinRequest = "group.join_request";
    public const string CallUpdate = "call.update";
    public const string PresenceUpdate = "presence.update";
}

/// <summary>Configurações de comportamento de um dispositivo.</summary>
public record DeviceSettings
{
    /// <summary>
    /// Emite <c>presence.update</c> (digitando, gravando, online) de toda conversa que escrever para o dispositivo.
    /// A conta passa a aparecer online, e o celular deixa de notificar mensagens novas.
    /// </summary>
    public bool? PresenceEvents { get; init; }
}

public record Device
{
    public DeviceSettings Settings { get; init; } = new();
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary><c>started</c> ou <c>stopped</c>.</summary>
    public string DesiredState { get; init; } = "";
    /// <summary>Ver <see cref="DeviceStatus"/>.</summary>
    public string Status { get; init; } = "";
    public string? StatusReason { get; init; }
    public string? Jid { get; init; }
    public string? Lid { get; init; }
    public string? Phone { get; init; }
    public string? ProfileName { get; init; }
    public string? WorkerId { get; init; }
    public long LastEventSeq { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? ConnectedAt { get; init; }

    public bool IsConnected => Status == DeviceStatus.Connected;
}

public record QueueCounts(int Queued, int Failed);
public record DeliveryCounts(int Pending, int Failed);

public record DeviceDetails : Device
{
    /// <summary>true quando algum worker está com o dispositivo.</summary>
    public bool Running { get; init; }
    public QueueCounts? Outbound { get; init; }
    public DeliveryCounts? WebhookDeliveries { get; init; }
}

public record CreatedDevice : Device
{
    /// <summary>Chave própria do dispositivo — só é devolvida na criação.</summary>
    public string ApiKey { get; init; } = "";
}

/// <summary>Resultado de <see cref="DevicesApi.GetOrCreateAsync"/>.</summary>
public record DeviceLookup(Device Device, bool Created, string? ApiKey);

public record CreateDeviceRequest
{
    public required string Name { get; init; }
    /// <summary>Padrão true: o dispositivo já começa a gerar QR codes.</summary>
    public bool? Connect { get; init; }
    public WebhookInput? Webhook { get; init; }
    public DeviceSettings? Settings { get; init; }
}

public record QrCode
{
    /// <summary>Conteúdo do QR.</summary>
    public string Qr { get; init; } = "";
    /// <summary>PNG em data URL, pronto para um <c>&lt;img src&gt;</c>.</summary>
    public string Image { get; init; } = "";
    public DateTimeOffset GeneratedAt { get; init; }
}

public record WebhookInput
{
    public required string Url { get; init; }
    public string? Secret { get; init; }
    /// <summary>Vazio/nulo = todos os eventos. Ver <see cref="ZapiEventTypes"/>.</summary>
    public IReadOnlyList<string>? Events { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public bool? Enabled { get; init; }
}

public record WebhookUpdate
{
    public string? Url { get; init; }
    public string? Secret { get; init; }
    public IReadOnlyList<string>? Events { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public bool? Enabled { get; init; }
}

public record Webhook
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public IReadOnlyList<string> Events { get; init; } = [];
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public bool Enabled { get; init; }
    public bool HasSecret { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public record WebhookTestResult
{
    public bool Ok { get; init; }
    public int? StatusCode { get; init; }
    public int DurationMs { get; init; }
    public string? ResponseBody { get; init; }
    public string? Error { get; init; }
}

public record WebhookDelivery
{
    public string Id { get; init; } = "";
    public string WebhookId { get; init; } = "";
    public string EventId { get; init; } = "";
    public string EventType { get; init; } = "";
    public long EventSeq { get; init; }
    /// <summary><c>pending</c>, <c>delivered</c> ou <c>failed</c>.</summary>
    public string Status { get; init; } = "";
    public int Attempts { get; init; }
    public int? LastStatusCode { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; init; }
}

/// <summary>Valores de <see cref="OutboundMessage.Status"/>.</summary>
public static class OutboundStatus
{
    public const string Queued = "queued";
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

public record OutboundMessage
{
    /// <summary>Id da mensagem na fila da API (use para acompanhar o envio).</summary>
    public string Id { get; init; } = "";
    public string To { get; init; } = "";
    public string Type { get; init; } = "";
    /// <summary>Ver <see cref="OutboundStatus"/>.</summary>
    public string Status { get; init; } = "";
    /// <summary>Progresso informado pelo WhatsApp: sent → delivered → read → played.</summary>
    public string? Ack { get; init; }
    /// <summary>Id da mensagem no WhatsApp, quando atribuído.</summary>
    public string? MessageId { get; init; }
    public int Attempts { get; init; }
    public string? LastError { get; init; }
    public string? IdempotencyKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SentAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public string? DeviceStatus { get; init; }
    /// <summary>true quando a chave de idempotência já existia e a mensagem original foi devolvida.</summary>
    public bool Duplicate { get; init; }
}

public record MediaInfo
{
    /// <summary>stored, failed, too_large, not_downloaded ou pending.</summary>
    public string Status { get; init; } = "";
    public string? Mimetype { get; init; }
    public long? Size { get; init; }
    public string? FileName { get; init; }
    public int? Seconds { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool? Ptt { get; init; }
    /// <summary>sticker: é animada.</summary>
    public bool? Animated { get; init; }
    /// <summary>sticker: vetorial (Lottie) — o arquivo guardado é um .was, não um WebP.</summary>
    public bool? Lottie { get; init; }
    public string? Key { get; init; }
    /// <summary>URL temporária; expira — use <c>Messages.GetMediaUrlAsync</c> para uma nova.</summary>
    public string? Url { get; init; }
    public string? Error { get; init; }
}

public record MessageLocation(double? Latitude, double? Longitude, string? Name, string? Address, bool? Live);
public record MessageContact(string? Name, string? Vcard);
public record MessagePoll(string? Name, IReadOnlyList<string>? Options, int? SelectableCount);
public record MessageSelection(string? Id, string? Text);
public record StickerPackInfo(string? Id, string? Name, string? Publisher, int? Count);
/// <summary>A mensagem citada por uma resposta; <c>Type</c> e <c>Text</c> descrevem a mensagem citada.</summary>
public record QuotedMessage(string Id, string? Sender, bool? FromMe = null, string? Type = null, string? Text = null);
/// <summary>Reação atual de uma pessoa a uma mensagem.</summary>
public record MessageReactionInfo(string Emoji, string Sender, string? SenderAlt, bool FromMe, DateTimeOffset Timestamp);

/// <summary>Mensagem recebida ou enviada, no formato normalizado da API.</summary>
public record Message
{
    public string Id { get; init; } = "";
    public string Chat { get; init; } = "";
    /// <summary>A outra forma de endereço do chat (número quando <see cref="Chat"/> é LID e vice-versa), quando conhecida.</summary>
    public string? ChatAlt { get; init; }
    public string Sender { get; init; } = "";
    public string? SenderAlt { get; init; }
    public bool FromMe { get; init; }
    public bool IsGroup { get; init; }
    public string? PushName { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>text, image, video, audio, document, sticker, sticker_pack, location, contact, poll, button_reply, list_reply, group_invite, event, unknown.</summary>
    public string Type { get; init; } = "";
    public string? ContentType { get; init; }
    public string? Text { get; init; }
    public MediaInfo? Media { get; init; }
    public MessageLocation? Location { get; init; }
    public IReadOnlyList<MessageContact>? Contacts { get; init; }
    public MessagePoll? Poll { get; init; }
    public MessageSelection? Selection { get; init; }
    /// <summary>Preenchido quando <c>Type</c> é <c>sticker_pack</c>.</summary>
    public StickerPackInfo? StickerPack { get; init; }
    public QuotedMessage? Quoted { get; init; }
    /// <summary>Reações atuais; vem nas mensagens consultadas na API, não nos eventos.</summary>
    public IReadOnlyList<MessageReactionInfo>? Reactions { get; init; }
    public IReadOnlyList<string>? Mentions { get; init; }
    public bool? ViewOnce { get; init; }
    public bool? Forwarded { get; init; }
    /// <summary>Preenchido quando a mensagem saiu por esta API.</summary>
    public string? OutboundId { get; init; }
    public string? Status { get; init; }
    /// <summary>Objeto bruto do Baileys, quando pedido.</summary>
    public JsonElement? Raw { get; init; }
}

public record MediaUrl(string Url, string? Mimetype, long? Size, string? FileName);

// ───────────── dados dos eventos ─────────────

public record ConnectionUpdate(string Status, string? Reason, string? Jid, string? Name);
public record QrUpdated(string Qr);
public record MessageStatusUpdate(string Id, string Chat, bool FromMe, string Status, string? OutboundId);
public record MessageFailed(string OutboundId, string To, string? Type, string Error, int? Attempts);
/// <summary><c>Emoji</c> nulo = reação removida. <c>FromMe</c>: foi o próprio dispositivo que reagiu.</summary>
public record MessageReaction(string MessageId, string Chat, string? Emoji, bool FromMe, string? Sender)
{
    public bool MessageFromMe { get; init; }
    public string? PreviousEmoji { get; init; }
    public bool Removed { get; init; }
    public string? SenderAlt { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>Presença de um contato. Em grupos, <c>Participant</c> é quem está digitando.</summary>
public record PresenceUpdate
{
    public string Chat { get; init; } = "";
    public string? ChatAlt { get; init; }
    public string Participant { get; init; } = "";
    public string? ParticipantAlt { get; init; }
    public bool IsGroup { get; init; }
    /// <summary>composing, recording, paused, available ou unavailable.</summary>
    public string State { get; init; } = "";
    public bool Typing { get; init; }
    public bool Recording { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
public record MessageEdited(string Id, string Chat, bool FromMe, string? Text);
public record MessageDeleted(string Id, string Chat, bool FromMe);

/// <summary>Um evento, como chega no webhook ou no log de eventos.</summary>
public record ZapiEvent
{
    /// <summary>Único por evento — use para deduplicar.</summary>
    public string Id { get; init; } = "";
    /// <summary>Sequencial e sem buracos por dispositivo — use para ordenar e retomar.</summary>
    public long Seq { get; init; }
    public string DeviceId { get; init; } = "";
    /// <summary>Ver <see cref="ZapiEventTypes"/>.</summary>
    public string Type { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public JsonElement Data { get; init; }

    /// <summary>Converte <see cref="Data"/> para o tipo desejado.</summary>
    public T DataAs<T>() => Data.Deserialize<T>(ZapiHttp.Json)!;

    public bool IsMessage => Type is ZapiEventTypes.MessageReceived or ZapiEventTypes.MessageSent;

    /// <summary>A mensagem de um <c>message.received</c>/<c>message.sent</c>; nulo para outros tipos.</summary>
    public Message? Message => IsMessage ? DataAs<Message>() : null;
    public ConnectionUpdate? Connection => Type == ZapiEventTypes.ConnectionUpdate ? DataAs<ConnectionUpdate>() : null;
    public MessageStatusUpdate? MessageStatus => Type == ZapiEventTypes.MessageStatus ? DataAs<MessageStatusUpdate>() : null;
    public MessageFailed? MessageFailed => Type == ZapiEventTypes.MessageFailed ? DataAs<MessageFailed>() : null;
    public MessageReaction? Reaction => Type == ZapiEventTypes.MessageReaction ? DataAs<MessageReaction>() : null;
    public PresenceUpdate? Presence => Type == ZapiEventTypes.PresenceUpdate ? DataAs<PresenceUpdate>() : null;
}

public record EventPage
{
    public IReadOnlyList<ZapiEvent> Events { get; init; } = [];
    public long LastSeq { get; init; }
    public bool HasMore { get; init; }
}

public record NumberCheck(string Input, bool Exists, string? Jid, string? Lid);

public record GroupSummary
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public string? Owner { get; init; }
    public int? Size { get; init; }
    public long? Creation { get; init; }
    public bool? Announce { get; init; }
    public bool? Restrict { get; init; }
    public bool? IsCommunity { get; init; }
}

public record GroupParticipant
{
    public string Id { get; init; } = "";
    /// <summary><c>admin</c>, <c>superadmin</c> ou nulo.</summary>
    public string? Admin { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Lid { get; init; }
}

public record GroupMetadata
{
    public string Id { get; init; } = "";
    public string Subject { get; init; } = "";
    public string? Owner { get; init; }
    public string? Desc { get; init; }
    public bool? Announce { get; init; }
    public bool? Restrict { get; init; }
    public int? Size { get; init; }
    public IReadOnlyList<GroupParticipant> Participants { get; init; } = [];
}

public record GroupSettings
{
    public string? Subject { get; init; }
    /// <summary>String vazia remove a descrição.</summary>
    public string? Description { get; init; }
    /// <summary>true = só admins enviam mensagens.</summary>
    public bool? Announce { get; init; }
    /// <summary>true = só admins editam os dados do grupo.</summary>
    public bool? Locked { get; init; }
    /// <summary>Mensagens temporárias, em segundos (0 desliga).</summary>
    public int? Ephemeral { get; init; }
    /// <summary><c>admin_add</c> ou <c>all_member_add</c>.</summary>
    public string? MemberAddMode { get; init; }
    public bool? JoinApprovalMode { get; init; }
}

public record InviteLink(string? Code, string? Url);

public record ContactCard(string Name, string Phone, string? Organization = null);

/// <summary>Origem de uma mídia a enviar.</summary>
public sealed class MediaSource
{
    private MediaSource() { }
    internal string? Url { get; private init; }
    internal string? Base64 { get; private init; }

    /// <summary>URL pública de onde o worker da API baixa o arquivo.</summary>
    public static MediaSource FromUrl(string url) => new() { Url = url };
    public static MediaSource FromBytes(ReadOnlySpan<byte> bytes) => new() { Base64 = Convert.ToBase64String(bytes) };
    public static MediaSource FromBase64(string base64) => new() { Base64 = base64 };
    public static MediaSource FromFile(string path) => FromBytes(File.ReadAllBytes(path));
    public static async Task<MediaSource> FromStreamAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return FromBytes(buffer.ToArray());
    }

    internal object ToPayload() => Url is not null ? new { url = Url } : new { base64 = Base64 };
}

/// <summary>Opções comuns a qualquer envio.</summary>
public record SendOptions
{
    /// <summary>Responde citando esta mensagem.</summary>
    public string? QuotedMessageId { get; init; }
    /// <summary>Números/JIDs mencionados (inclua também @numero no texto).</summary>
    public IReadOnlyList<string>? Mentions { get; init; }
    public bool? MentionAll { get; init; }
    /// <summary>
    /// Enviar duas vezes com a mesma chave entrega uma vez só. Quando omitida, o cliente gera uma por chamada —
    /// é isso que torna seguras as retentativas automáticas.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>Dados exibidos quando o destinatário toca na figurinha.</summary>
public record StickerOptions : SendOptions
{
    /// <summary>Nome do pacote.</summary>
    public string? Pack { get; init; }
    public string? Author { get; init; }
    public IReadOnlyList<string>? Emojis { get; init; }
}

/// <summary>Opções de envio de mídia; cada campo vale para os tipos indicados.</summary>
public record MediaOptions : SendOptions
{
    /// <summary>image, video, document.</summary>
    public string? Caption { get; init; }
    public string? Mimetype { get; init; }
    /// <summary>document.</summary>
    public string? FileName { get; init; }
    /// <summary>audio: enviar como mensagem de voz (use ogg/opus).</summary>
    public bool? Ptt { get; init; }
    /// <summary>video: reproduzir como GIF.</summary>
    public bool? Gif { get; init; }
    /// <summary>image, video, audio.</summary>
    public bool? ViewOnce { get; init; }
}
