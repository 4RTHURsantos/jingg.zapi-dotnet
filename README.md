# Jingg.Zapi.Client

Cliente .NET para a Jingg Zapi — API de WhatsApp multi-dispositivo. Você informa a URL e a chave;
a biblioteca cuida de autenticação, serialização, retentativas, idempotência dos envios e validação de webhooks.

Alvos: `net8.0` e `net10.0`. Única dependência: `Microsoft.Extensions.DependencyInjection.Abstractions`.

## Instalação

```bash
dotnet pack src/Jingg.Zapi.Client -c Release      # gera artifacts/nuget/*.nupkg
cp nuget.config.exemplo ../meu-projeto/nuget.config   # ajuste o caminho do feed local
dotnet add package Jingg.Zapi.Client
```

Ou por `ProjectReference` (submódulo), que compila do fonte e funciona dentro de container sem feed.

## Uso

```csharp
using Jingg.Zapi;

var zapi = new ZapiClient("http://localhost:3000", adminKey);

// acha o dispositivo pelo nome ou cria — pode rodar a cada inicialização
var found = await zapi.Devices.GetOrCreateAsync("atendimento");
var device = zapi.Device(found.Device.Id);

// pareamento: mostre o QR e aguarde
if (await device.ConnectAndGetQrAsync() is { } qr)
{
    await File.WriteAllBytesAsync("qr.png", await device.GetQrPngAsync());
    await device.WaitUntilConnectedAsync(TimeSpan.FromMinutes(2));
}

// envio
var sent = await device.Messages.SendTextAsync("5511999990000", "Olá!");
await device.Messages.SendImageAsync("5511999990000", MediaSource.FromFile("foto.jpg"), new MediaOptions { Caption = "Veja" });
await device.Messages.SendDocumentAsync("5511999990000", MediaSource.FromUrl("https://exemplo.com/boleto.pdf"),
    new MediaOptions { FileName = "boleto.pdf" });

await device.Messages.WaitUntilSentAsync(sent.Id);   // opcional: aguarda sair da fila
```

Com injeção de dependência:

```csharp
builder.Services.AddJinggZapi(o =>
{
    o.BaseUrl = builder.Configuration["Zapi:Url"]!;
    o.ApiKey = builder.Configuration["Zapi:AdminKey"]!;
});
// depois: injete ZapiClient (singleton, thread-safe)
```

A chave pode ser a de administrador (acesso a tudo) ou a de um dispositivo (só `zapi.Device(id)` daquele dispositivo).

## Recebendo eventos

**Por webhook** — registre uma vez e valide cada chamada:

```csharp
await device.Webhooks.EnsureAsync(new WebhookInput { Url = "https://meu-sistema/webhooks/whatsapp", Secret = secret });

app.MapPost("/webhooks/whatsapp", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var ev = ZapiWebhook.Parse(await reader.ReadToEndAsync(), secret, req.Headers[ZapiWebhook.SignatureHeader]);

    if (ev.Message is { FromMe: false } msg)
        Console.WriteLine($"{msg.PushName}: {msg.Text}");
    else if (ev.MessageFailed is { } failed)
        Console.WriteLine($"falhou: {failed.Error}");

    return Results.Ok();   // qualquer coisa fora de 2xx faz a API reenviar
});
```

A entrega é *ao menos uma vez*: deduplique por `ev.Id` (gravando-o no seu banco) e ordene por `ev.Seq`.

**Sem webhook** — um fluxo ordenado por polling, que retoma de onde parou:

```csharp
await foreach (var ev in device.Events.StreamAsync(after: ultimoSeq, cancellationToken: stoppingToken))
{
    await Processar(ev);
    ultimoSeq = ev.Seq;      // persista: é o seu cursor
}
```

`ev.DataAs<T>()` converte os dados de qualquer evento; os mais usados têm atalho: `ev.Message`, `ev.Connection`,
`ev.MessageStatus`, `ev.MessageFailed`, `ev.Reaction`. Os tipos estão em `ZapiEventTypes`.

## O que a biblioteca faz por você

- **Retentativas** com backoff em falha de rede, timeout e respostas 429/502/503/504 — em `GET`, `DELETE` e nos envios de mensagem.
- **Idempotência nos envios**: cada `Send…Async` leva uma chave única (ou a sua, em `SendOptions.IdempotencyKey`), então
  repetir o POST após uma queda de rede não duplica a mensagem. Demais `POST`s não são repetidos automaticamente.
- **Erros** viram `ZapiException` com `StatusCode` e a mensagem da API (`IsNotConnected`, `IsNotFound`); `StatusCode == 0`
  significa que a API não respondeu.
- **Cancelamento**: todo método aceita `CancellationToken`; cancelar lança `OperationCanceledException`, não `ZapiException`.

## Superfície

| | |
|---|---|
| `zapi.Devices` | `CreateAsync`, `ListAsync`, `GetAsync`, `GetOrCreateAsync`, `DeleteAsync` |
| `zapi.Device(id)` | `ConnectAsync`, `DisconnectAsync`, `RestartAsync`, `LogoutAsync`, `GetQrAsync`, `GetQrPngAsync`, `GetPairingCodeAsync`, `ConnectAndGetQrAsync`, `WaitUntilConnectedAsync`, `RotateKeyAsync`, `UpdateProfileAsync`, `SendPresenceAsync`, `RejectCallAsync` |
| `.Messages` | `SendText/Image/Video/Audio/Document/Sticker/Location/Contacts/PollAsync`, `ReactAsync`, `EditAsync`, `DeleteForEveryoneAsync`, `GetOutboundAsync`, `ListOutboundAsync`, `CancelAsync`, `RetryAsync`, `WaitUntilSentAsync`, `ListAsync`, `GetAsync`, `GetMediaUrlAsync`, `MarkReadAsync` |
| `.Webhooks` | `ListAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`, `EnsureAsync`, `TestAsync`, `ListDeliveriesAsync`, `RetryDeliveryAsync`, `RetryFailedDeliveriesAsync` |
| `.Events` | `ListAsync`, `GetLastSeqAsync`, `StreamAsync` |
| `.Contacts` | `CheckAsync`, `GetPictureUrlAsync`, `GetStatusAsync`, `GetBusinessProfileAsync`, `BlockAsync`, `UnblockAsync`, `SubscribePresenceAsync` |
| `.Groups` | `ListAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `Add/RemoveParticipantsAsync`, `PromoteAsync`, `DemoteAsync`, `GetInviteLinkAsync`, `RevokeInviteLinkAsync`, `GetInviteInfoAsync`, `JoinAsync`, `LeaveAsync` |
| `ZapiWebhook` | `Parse`, `VerifySignature`, `Sign` |

## Testes

```bash
dotnet test                                   # unitários (HttpMessageHandler falso)
ZAPI_URL=http://localhost:3000 ZAPI_ADMIN_KEY=... dotnet test   # inclui a ida-e-volta contra uma API real
```

Sem as duas variáveis, o teste de integração não faz nada (e aparece como aprovado).
