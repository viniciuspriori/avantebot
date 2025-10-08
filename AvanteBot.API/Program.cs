using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

// --- 1. Configuração e Validação ---
// Valida se as variáveis de ambiente essenciais foram configuradas.
var botToken = Environment.GetEnvironmentVariable("BotToken") ?? throw new ArgumentNullException("BotToken not set");
var webhookUrl = Environment.GetEnvironmentVariable("BotWebhookUrl") ?? throw new ArgumentNullException("BotWebhookUrl not set");

services.AddHttpClient("tgwebhook")
        .RemoveAllLoggers()
        .AddTypedClient(httpClient => new TelegramBotClient(botToken, httpClient));

// Usar um HttpClientFactory para o bot é uma boa prática.
services.AddHttpClient();

// --- 2. Injeção de Dependência e Estado ---
// Usando um Singleton para o cache de imagens para que ele seja compartilhado por toda a aplicação.
// ConcurrentDictionary é thread-safe, evitando problemas de concorrência.
services.AddSingleton<ConcurrentDictionary<string, List<string>>>(new ConcurrentDictionary<string, List<string>>());

var app = builder.Build();

app.MapGet("/bot/setWebhook", async (TelegramBotClient bot, ILogger<Program> logger) =>
{
    try
    {
        await bot.SetWebhook(webhookUrl);
        logger.LogInformation("Webhook set successfully to {Url}", webhookUrl);
        return Results.Ok($"Webhook set to {webhookUrl}");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to set webhook.");
        return Results.Problem("Failed to set webhook.");
    }
});

app.MapGet("/", () => "AvanteBot is online!");

// O endpoint principal agora usa injeção de dependência para obter os serviços necessários.
// Retorna Results.Ok() para que o Telegram saiba que o update foi recebido.
app.MapPost("/bot", async (
    [FromBody] Update update,
    [FromServices] TelegramBotClient bot,
    [FromServices] IHttpClientFactory clientFactory,
    [FromServices] ConcurrentDictionary<string, List<string>> imageCache,
    [FromServices] ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    // O manipulador principal agora é `async Task` e tem um try-catch global.
    await HandleUpdateAsync(bot, update, clientFactory, imageCache, logger, cancellationToken);
    return Results.Ok();
});

app.Run();

// --- 3. Lógica do Bot Refatorada ---

// O manipulador de updates principal, que delega para métodos mais específicos.
async Task HandleUpdateAsync(
    TelegramBotClient bot,
    Update update,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, List<string>> imageCache,
    ILogger logger,
    CancellationToken cancellationToken)
{
    try
    {
        var handler = update.Type switch
        {
            UpdateType.Message when update.Message?.Text is not null => HandleMessageAsync(bot, update.Message, clientFactory, imageCache, logger, cancellationToken),
            UpdateType.CallbackQuery when update.CallbackQuery is not null => HandleCallbackQueryAsync(bot, update.CallbackQuery, clientFactory, imageCache, logger, cancellationToken),
            _ => HandleUnknownUpdate(logger, update)
        };
        await handler;
    }
    catch (Exception ex)
    {
        // Pega qualquer exceção não tratada nos handlers específicos.
        logger.LogError(ex, "An unhandled error occurred while processing update {UpdateId}", update.Id);
    }
}

// Manipulador para mensagens de texto
async Task HandleMessageAsync(
    TelegramBotClient bot,
    Message message,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, List<string>> imageCache,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var text = message.Text!;

    if (text.StartsWith("/image"))
    {
        var query = text.Replace("/image", "").Trim();
        await SendNextImageAsync(bot, message.Chat.Id, query, message.From?.Username, clientFactory, imageCache, logger, cancellationToken);
    }
    else if (text.StartsWith("/teste"))
    {
        var query = text.Replace("/teste", "").Trim();
        await bot.SendMessage(message.Chat.Id, $"{message.From?.FirstName} said: {query}\nTry /image <term> to search for an image!", cancellationToken: cancellationToken);
    }
}

// Manipulador para cliques em botões
async Task HandleCallbackQueryAsync(
    TelegramBotClient bot,
    CallbackQuery callbackQuery,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, List<string>> imageCache,
    ILogger logger,
    CancellationToken cancellationToken)
{
    const string ImageCallbackPrefix = "NEXT_IMAGE:";

    // Sempre responda ao callback para remover o "loading" no cliente do usuário.
    await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

    if (callbackQuery.Data is { } data && data.StartsWith(ImageCallbackPrefix))
    {
        var query = data.Replace(ImageCallbackPrefix, "");
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        await SendNextImageAsync(bot, chatId, query, callbackQuery.From.Username, clientFactory, imageCache, logger, cancellationToken);
    }
}

Task HandleUnknownUpdate(ILogger logger, Update update)
{
    logger.LogWarning("Received an unhandled update type: {UpdateType}", update.Type);
    return Task.CompletedTask;
}


// A função principal de busca e envio de imagens, agora mais robusta.
async Task SendNextImageAsync(
    TelegramBotClient bot,
    long chatId,
    string query,
    string? username,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, List<string>> imageCache,
    ILogger logger,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        await bot.SendMessage(chatId, "Por favor, especifique o que você quer buscar. Ex: `/image gatos`", cancellationToken: cancellationToken);
        return;
    }

    // --- 4. Busca de Imagens com Fallback ---
    List<string> items = await FetchImageLinksAsync(query, clientFactory, logger, cancellationToken);

    if (items.Count == 0)
    {
        await bot.SendMessage(chatId, $"Nenhuma imagem encontrada para '{query}'.", cancellationToken: cancellationToken);
        return;
    }

    // --- 5. Lógica de Cache Thread-Safe ---
    var usedImages = imageCache.GetOrAdd(query, _ => new List<string>());

    var remaining = items.Except(usedImages).ToList();
    if (remaining.Count == 0)
    {
        // Se todas as imagens já foram vistas, limpa o cache para essa busca e recomeça.
        imageCache.TryRemove(query, out _);
        usedImages = imageCache.GetOrAdd(query, _ => new List<string>());
        remaining = items;
        await bot.SendMessage(chatId, $"Você já viu todas as imagens! Resetando o ciclo para '{query}'.", cancellationToken: cancellationToken);
    }

    var random = new Random();
    var chosenUrl = remaining[random.Next(remaining.Count)];
    usedImages.Add(chosenUrl);

    // --- 6. Envio com Tratamento de Erro Específico ---
    var caption = $"Resultado para: \"{query}\"";
    if (username != null) caption += $" pedido por @{username}";

    var callbackData = $"NEXT_IMAGE:{query}";
    var inlineKeyboard = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("🖼️ Mais Imagens", callbackData));

    try
    {
        await bot.SendPhoto(
            chatId: chatId,
            photo: InputFile.FromUri(chosenUrl),
            caption: caption,
            replyMarkup: inlineKeyboard,
            cancellationToken: cancellationToken
        );
    }
    catch (ApiRequestException apiEx)
    {
        logger.LogError(apiEx, "Telegram API error while sending photo for query '{Query}'. URL: {Url}", query, chosenUrl);
        await bot.SendMessage(chatId, "Desculpe, não consegui enviar essa imagem. Pode ser um link inválido ou temporariamente indisponível.", cancellationToken: cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unexpected error while sending photo for query '{Query}'. URL: {Url}", query, chosenUrl);
        await bot.SendMessage(chatId, "Ocorreu um erro inesperado ao tentar enviar a imagem.", cancellationToken: cancellationToken);
    }
}

// Função auxiliar para buscar imagens, isolando a lógica de rede.
async Task<List<string>> FetchImageLinksAsync(
    string query,
    IHttpClientFactory clientFactory,
    ILogger logger,
    CancellationToken cancellationToken)
{
    var http = clientFactory.CreateClient();
    var apiKey = Environment.GetEnvironmentVariable("GoogleApiKey");
    var cx = Environment.GetEnvironmentVariable("GoogleCx");
    var serpApiKey = Environment.GetEnvironmentVariable("SerpApiKey");

    // Tenta a API do Google primeiro
    if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(cx))
    {
        try
        {
            var googleUrl = $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(query)}&searchType=image&key={apiKey}&cx={cx}";
            var result = await http.GetFromJsonAsync<JsonElement>(googleUrl, cancellationToken);
            if (result.TryGetProperty("items", out var itemsElement))
            {
                var links = itemsElement.EnumerateArray()
                    .Select(i => i.TryGetProperty("link", out var linkProp) ? linkProp.GetString() : null)
                    .Where(IsValidImageUrl)
                    .ToList();
                if (links.Any()) return links!;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Google Custom Search API request failed.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse JSON from Google Custom Search API.");
        }
    }

    // Fallback para a SerpAPI
    if (!string.IsNullOrEmpty(serpApiKey))
    {
        try
        {
            var serpUrl = $"https://serpapi.com/search.json?engine=google_images&q={Uri.EscapeDataString(query)}&api_key={serpApiKey}";
            var serpResult = await http.GetFromJsonAsync<JsonElement>(serpUrl, cancellationToken);
            if (serpResult.TryGetProperty("images_results", out var serpItems))
            {
                var links = serpItems.EnumerateArray()
                    .Select(i => i.TryGetProperty("original", out var linkProp) ? linkProp.GetString() : null)
                    .Where(IsValidImageUrl)
                    .ToList();
                if (links.Any()) return links!;
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "SerpAPI request failed.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse JSON from SerpAPI.");
        }
    }

    return new List<string>(); // Retorna lista vazia se tudo falhar
}


// --- Helper method ---
bool IsValidImageUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url))
        return false;

    url = url.ToLowerInvariant();

    // Filter only real image extensions
    string[] validExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];
    return url.StartsWith("http") && validExtensions.Any(url.EndsWith);
}
