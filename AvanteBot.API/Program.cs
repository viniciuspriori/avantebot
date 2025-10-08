using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
var botToken = Environment.GetEnvironmentVariable("BotToken") 
    ?? throw new InvalidOperationException("BotToken environment variable not set");
var webhookUrl = Environment.GetEnvironmentVariable("BotWebhookUrl") 
    ?? throw new InvalidOperationException("BotWebhookUrl environment variable not set");

services.AddHttpClient("tgwebhook")
        .AddTypedClient(httpClient => new TelegramBotClient(botToken, httpClient));

services.AddHttpClient();

// --- 2. Injeção de Dependência e Estado (corrigido para thread-safety) ---
services.AddSingleton<ConcurrentDictionary<string, ConcurrentBag<string>>>(
    new ConcurrentDictionary<string, ConcurrentBag<string>>());

// --- 3. Lógica do Bot Refatorada ---
async Task HandleUpdateAsync(
    TelegramBotClient bot,
    Update update,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, ConcurrentBag<string>> imageCache,
    ILogger<Program> logger,
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
        logger.LogError(ex, "An unhandled error occurred while processing update {UpdateId}", update.Id);
    }
}

async Task HandleMessageAsync(
    TelegramBotClient bot,
    Message message,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, ConcurrentBag<string>> imageCache,
    ILogger<Program> logger,
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
        await bot.SendTextMessageAsync(
            message.Chat.Id, 
            $"{message.From?.FirstName} said: {query}\nTry /image <term> to search for an image!", 
            cancellationToken: cancellationToken);
    }
}

async Task HandleCallbackQueryAsync(
    TelegramBotClient bot,
    CallbackQuery callbackQuery,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, ConcurrentBag<string>> imageCache,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    const string ImageCallbackPrefix = "NEXT_IMAGE:";
    
    await bot.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: cancellationToken);
    
    if (callbackQuery.Data is { } data && data.StartsWith(ImageCallbackPrefix))
    {
        var query = data.Replace(ImageCallbackPrefix, "");
        var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;
        await SendNextImageAsync(bot, chatId, query, callbackQuery.From.Username, clientFactory, imageCache, logger, cancellationToken);
    }
}

Task HandleUnknownUpdate(ILogger<Program> logger, Update update)
{
    logger.LogWarning("Received an unhandled update type: {UpdateType}", update.Type);
    return Task.CompletedTask;
}

async Task SendNextImageAsync(
    TelegramBotClient bot,
    long chatId,
    string query,
    string? username,
    IHttpClientFactory clientFactory,
    ConcurrentDictionary<string, ConcurrentBag<string>> imageCache,
    ILogger<Program> logger,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        await bot.SendTextMessageAsync(
            chatId, 
            "Por favor, especifique o que você quer buscar. Ex: `/image gatos`",
            parseMode: ParseMode.Markdown,
            cancellationToken: cancellationToken);
        return;
    }

    List<string> items = await FetchImageLinksAsync(query, clientFactory, logger, cancellationToken);

    if (items.Count == 0)
    {
        await bot.SendTextMessageAsync(
            chatId, 
            $"Nenhuma imagem encontrada para '{query}'.", 
            cancellationToken: cancellationToken);
        return;
    }
    
    var usedImages = imageCache.GetOrAdd(query, _ => new ConcurrentBag<string>());
    
    var remaining = items.Except(usedImages).ToList();
    if (remaining.Count == 0)
    {
        imageCache.TryRemove(query, out _);
        usedImages = imageCache.GetOrAdd(query, _ => new ConcurrentBag<string>());
        remaining = items;
        await bot.SendTextMessageAsync(
            chatId, 
            $"Você já viu todas as imagens! Resetando o ciclo para '{query}'.", 
            cancellationToken: cancellationToken);
    }

    // --- LÓGICA DE TENTATIVAS ---
    var shuffledUrls = remaining.OrderBy(_ => Random.Shared.Next()).ToList();

    bool imageSentSuccessfully = false;
    foreach (var urlToTry in shuffledUrls)
    {
        var caption = $"Resultado para: \"{query}\"";
        if (username != null) caption += $" pedido por @{username}";
        var callbackData = $"NEXT_IMAGE:{query}";
        var inlineKeyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("🖼️ Mais Imagens", callbackData));

        try
        {
            await bot.SendPhotoAsync(
                chatId: chatId,
                photo: InputFile.FromUri(urlToTry),
                caption: caption,
                replyMarkup: inlineKeyboard,
                cancellationToken: cancellationToken
            );

            // Se chegou aqui, a imagem foi enviada com sucesso!
            usedImages.Add(urlToTry);
            imageSentSuccessfully = true;
            break; // Sai do loop
        }
        catch (ApiRequestException apiEx)
        {
            logger.LogWarning("Falha ao enviar imagem da URL {Url}. Motivo: {Error}. Tentando a próxima.", urlToTry, apiEx.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro inesperado ao enviar a URL {Url}.", urlToTry);
        }
    }

    // --- MENSAGEM DE FALHA FINAL ---
    if (!imageSentSuccessfully)
    {
        await bot.SendTextMessageAsync(
            chatId, 
            $"Sinto muito, tentei encontrar imagens para '{query}', mas os links encontrados estavam protegidos ou quebrados.", 
            cancellationToken: cancellationToken);
    }
}

async Task<List<string>> FetchImageLinksAsync(
    string query,
    IHttpClientFactory clientFactory,
    ILogger<Program> logger,
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
                    .Where(s => !string.IsNullOrEmpty(s) && ImageUrlHelper.IsValidImageUrl(s))
                    .Select(s => s!)
                    .ToList();
                if (links.Any()) return links;
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
                    .Where(s => !string.IsNullOrEmpty(s) && ImageUrlHelper.IsValidImageUrl(s))
                    .Select(s => s!)
                    .ToList();
                if (links.Any()) return links;
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

var app = builder.Build();

app.MapGet("/bot/setWebhook", async (TelegramBotClient bot, ILogger<Program> logger) =>
{
    try
    {
        await bot.SetWebhookAsync(webhookUrl);
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

app.MapPost("/bot", async (
    [FromBody] Update update,
    [FromServices] TelegramBotClient bot,
    [FromServices] IHttpClientFactory clientFactory,
    [FromServices] ConcurrentDictionary<string, ConcurrentBag<string>> imageCache,
    [FromServices] ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    await HandleUpdateAsync(bot, update, clientFactory, imageCache, logger, cancellationToken);
    return Results.Ok();
});

app.Run();

// --- Funções Auxiliares ---
static class ImageUrlHelper
{
    public static readonly HashSet<string> ValidImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    public static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Valida se é HTTP ou HTTPS
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath);

        return !string.IsNullOrEmpty(extension) && ValidImageExtensions.Contains(extension);
    }
}