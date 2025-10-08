using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Core Telegram.Bot functionality and extension methods
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

// --- Funções Auxiliares ---
public static class ImageUrlHelper
{
    public static readonly HashSet<string> ValidImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"
    };

    public static bool IsValidImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var extension = Path.GetExtension(uri.AbsolutePath);
        return !string.IsNullOrEmpty(extension) && ValidImageExtensions.Contains(extension);
    }

    public static string NormalizeQuery(string query)
    {
        // Normaliza espaços e caixa
        var trimmed = (query ?? string.Empty).Trim();
        var parts = trimmed.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    public static string ShortTokenFor(string canonicalQuery)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalQuery));
        // 16 hex chars = 8 bytes de entropia -> suficiente e < 64 bytes para callback_data
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 16);
    }
}

public record QueryTokenInfo(string CanonicalQuery, string OriginalQuery);

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var services = builder.Services;

        // --- 1. Configuração e Validação ---
        var botToken = Environment.GetEnvironmentVariable("BotToken")
            ?? throw new InvalidOperationException("A variável de ambiente BotToken não está definida");
        var webhookUrl = Environment.GetEnvironmentVariable("BotWebhookUrl")
            ?? throw new InvalidOperationException("A variável de ambiente BotWebhookUrl não está definida");
        var webhookSecret = Environment.GetEnvironmentVariable("TelegramSecretToken")
            ?? throw new InvalidOperationException("A variável de ambiente TelegramSecretToken não está definida");

        services.AddHttpClient();

        // Telegram bot client via DI
        services.AddHttpClient<ITelegramBotClient>(httpClient => new TelegramBotClient(botToken, httpClient));

        // --- 2. Estado thread-safe ---
        services.AddSingleton(new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.OrdinalIgnoreCase));
        services.AddSingleton(new ConcurrentDictionary<string, QueryTokenInfo>());

        var app = builder.Build();

        // --- 3. Endpoints de utilidade ---
        app.MapGet("/bot/setWebhook", async ([FromServices] ITelegramBotClient bot, [FromServices] ILogger<Program> logger, CancellationToken ct) =>
        {
            try
            {
                // v22.x: Renomeado para SetWebhook (sem Async)
                await bot.SetWebhook(
                    url: webhookUrl,
                    allowedUpdates: new[] { UpdateType.Message, UpdateType.CallbackQuery },
                    secretToken: webhookSecret,
                    dropPendingUpdates: true,
                    cancellationToken: ct
                );
                logger.LogInformation("Webhook definido com sucesso para {Url}", webhookUrl);
                return Results.Ok($"Webhook definido para {webhookUrl}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao definir o webhook.");
                return Results.Problem("Falha ao definir o webhook.");
            }
        });

        app.MapGet("/bot/webhookInfo", async ([FromServices] ITelegramBotClient bot, CancellationToken ct) =>
        {
            // v22.x: Renomeado para GetWebhookInfo (sem Async)
            var info = await bot.GetWebhookInfo(ct);
            return Results.Json(info);
        });

        app.MapGet("/", () => "AvanteBot está online!");

        // --- 4. Endpoint Webhook (validação de secret) ---
        app.MapPost("/bot", async (
            HttpRequest request,
            [FromBody] Update update,
            [FromServices] ITelegramBotClient bot,
            [FromServices] IHttpClientFactory clientFactory,
            [FromServices] ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
            [FromServices] ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
            [FromServices] ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            if (!request.Headers.TryGetValue("X-Telegram-Bot-Api-Secret-Token", out var header) ||
                !string.Equals(header.ToString(), webhookSecret, StringComparison.Ordinal))
            {
                logger.LogWarning("Requisição ao webhook com secret inválido ou ausente.");
                return Results.Unauthorized();
            }

            await HandleUpdateAsync(bot, update, clientFactory, imageCache, tokenMap, logger, cancellationToken);
            return Results.Ok();
        });

        app.Run();
    }

    // --- 5. Lógica do Bot ---
    private static async Task HandleUpdateAsync(
        ITelegramBotClient bot,
        Update update,
        IHttpClientFactory clientFactory,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
        ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var handler = update.Type switch
            {
                UpdateType.Message when update.Message?.Text is not null =>
                    HandleMessageAsync(bot, update.Message, clientFactory, imageCache, tokenMap, logger, cancellationToken),

                UpdateType.CallbackQuery when update.CallbackQuery is not null =>
                    HandleCallbackQueryAsync(bot, update.CallbackQuery, clientFactory, imageCache, tokenMap, logger, cancellationToken),

                _ => HandleUnknownUpdate(logger, update)
            };

            await handler;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Erro não tratado ao processar a atualização {UpdateId}", update.Id);
        }
    }

    private static async Task HandleMessageAsync(
        ITelegramBotClient bot,
        Message message,
        IHttpClientFactory clientFactory,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
        ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var text = message.Text ?? string.Empty;

        var cmdEnt = message.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.BotCommand && e.Offset == 0);
        if (cmdEnt is null)
            return;

        var rawCmd = text.Substring(cmdEnt.Offset, cmdEnt.Length);
        var baseCmd = rawCmd.Split('@')[0];
        var args = text[(cmdEnt.Offset + cmdEnt.Length)..].Trim();

        switch (baseCmd)
        {
            case "/image":
                await SendNextImageToChatAsync(
                    bot, message.Chat.Id, args, message.From?.Username,
                    clientFactory, imageCache, tokenMap, logger, cancellationToken);
                break;

            case "/teste":
                // v22.x: Renomeado para SendMessage (sem Async)
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: $"{message.From?.FirstName} disse: {args}\nExperimente /image <termo> para procurar uma imagem!",
                    cancellationToken: cancellationToken);
                break;

            case "/start":
            case "/help":
                // v22.x: Renomeado para SendMessage (sem Async)
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: "Olá! Use:\n- /image <termo> para procurar imagens\n- /teste <texto> para um eco de teste",
                    cancellationToken: cancellationToken);
                break;
        }
    }

    private static async Task HandleCallbackQueryAsync(
        ITelegramBotClient bot,
        CallbackQuery callbackQuery,
        IHttpClientFactory clientFactory,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
        ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        // v22.x: Renomeado para AnswerCallbackQuery (sem Async)
        await bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);

        if (callbackQuery.Data is not string data)
            return;

        const string Prefix = "NI:";
        if (!data.StartsWith(Prefix, StringComparison.Ordinal))
            return;

        var token = data.Substring(Prefix.Length);
        if (!tokenMap.TryGetValue(token, out var info))
        {
            // v22.x: Renomeado para AnswerCallbackQuery (sem Async)
            await bot.AnswerCallbackQuery(
                callbackQuery.Id,
                text: "Sessão expirada. Envie o comando /image novamente.",
                showAlert: false,
                cancellationToken: cancellationToken);
            return;
        }

        if (callbackQuery.Message is not null)
        {
            var chatId = callbackQuery.Message.Chat.Id;
            await SendNextImageToChatAsync(
                bot, chatId, info.OriginalQuery, callbackQuery.From?.Username,
                clientFactory, imageCache, tokenMap, logger, cancellationToken);
        }
        else if (!string.IsNullOrEmpty(callbackQuery.InlineMessageId))
        {
            await EditInlineMessageToNextImageAsync(
                bot, callbackQuery.InlineMessageId!, info.OriginalQuery, callbackQuery.From?.Username,
                clientFactory, imageCache, tokenMap, logger, cancellationToken);
        }
    }

    private static Task HandleUnknownUpdate(ILogger<Program> logger, Update update)
    {
        logger.LogDebug("Atualização ignorada: {UpdateType}", update.Type);
        return Task.CompletedTask;
    }

    // --- 6. Envio/Edição de imagens ---
    private static async Task SendNextImageToChatAsync(
        ITelegramBotClient bot,
        long chatId,
        string originalQuery,
        string? username,
        IHttpClientFactory clientFactory,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
        ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(originalQuery))
        {
            // v22.x: Renomeado para SendMessage (sem Async)
            await bot.SendMessage(
                chatId,
                "Por favor, especifique o que você quer procurar. Exemplo: /image gatos",
                cancellationToken: cancellationToken);
            return;
        }

        var canonicalQuery = ImageUrlHelper.NormalizeQuery(originalQuery);

        var items = await FetchImageLinksAsync(originalQuery, clientFactory, logger, cancellationToken);
        if (items.Count == 0)
        {
            // v22.x: Renomeado para SendMessage (sem Async)
            await bot.SendMessage(
                chatId,
                $"Nenhuma imagem encontrada para '{originalQuery}'.",
                cancellationToken: cancellationToken);
            return;
        }

        var usedSet = imageCache.GetOrAdd(canonicalQuery, _ => new ConcurrentDictionary<string, byte>());
        var remaining = items.Where(u => !usedSet.ContainsKey(u)).ToList();

        if (remaining.Count == 0)
        {
            usedSet.Clear();
            remaining = items;
            // v22.x: Renomeado para SendMessage (sem Async)
            await bot.SendMessage(
                chatId,
                $"Você já viu todas as imagens! Reiniciando a lista para '{originalQuery}'.",
                cancellationToken: cancellationToken);
        }

        var token = ImageUrlHelper.ShortTokenFor(canonicalQuery);
        tokenMap[token] = new QueryTokenInfo(canonicalQuery, originalQuery);

        var shuffled = remaining.OrderBy(_ => Random.Shared.Next()).ToList();

        var caption = $"Resultado para: \"{originalQuery}\"";
        if (!string.IsNullOrEmpty(username)) caption += $" — pedido por @{username}";

        var inlineKeyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("🖼️ Mais Imagens", $"NI:{token}"));

        bool success = false;
        foreach (var urlToTry in shuffled)
        {
            try
            {
                // v22.x: Renomeado para SendPhoto (sem Async)
                await bot.SendPhoto(
                    chatId: chatId,
                    photo: new InputFileUrl(urlToTry),
                    caption: caption,
                    replyMarkup: inlineKeyboard,
                    cancellationToken: cancellationToken);

                usedSet.TryAdd(urlToTry, 0);
                success = true;
                break;
            }
            catch (ApiRequestException apiEx)
            {
                logger.LogWarning("Falha ao enviar imagem {Url}. Telegram {Code}: {Message}", urlToTry, apiEx.ErrorCode, apiEx.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado ao enviar a URL {Url}.", urlToTry);
            }
        }

        if (!success)
        {
            // v22.x: Renomeado para SendMessage (sem Async)
            await bot.SendMessage(
                chatId,
                $"Tentei encontrar imagens para '{originalQuery}', mas os links retornados falharam ao enviar. Tente novamente.",
                cancellationToken: cancellationToken);
        }
    }

    private static async Task EditInlineMessageToNextImageAsync(
        ITelegramBotClient bot,
        string inlineMessageId,
        string originalQuery,
        string? username,
        IHttpClientFactory clientFactory,
        ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> imageCache,
        ConcurrentDictionary<string, QueryTokenInfo> tokenMap,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var canonicalQuery = ImageUrlHelper.NormalizeQuery(originalQuery);

        var items = await FetchImageLinksAsync(originalQuery, clientFactory, logger, cancellationToken);
        if (items.Count == 0)
        {
            return;
        }

        var usedSet = imageCache.GetOrAdd(canonicalQuery, _ => new ConcurrentDictionary<string, byte>());
        var remaining = items.Where(u => !usedSet.ContainsKey(u)).ToList();
        if (remaining.Count == 0)
        {
            usedSet.Clear();
            remaining = items;
        }

        var token = ImageUrlHelper.ShortTokenFor(canonicalQuery);
        tokenMap[token] = new QueryTokenInfo(canonicalQuery, originalQuery);

        var shuffled = remaining.OrderBy(_ => Random.Shared.Next()).ToList();

        var caption = $"Resultado para: \"{originalQuery}\"";
        if (!string.IsNullOrEmpty(username)) caption += $" — pedido por @{username}";

        var inlineKeyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("🖼️ Mais Imagens", $"NI:{token}"));

        foreach (var urlToTry in shuffled)
        {
            try
            {
                var media = new InputMediaPhoto(new InputFileUrl(urlToTry))
                {
                    Caption = caption
                };

                // v22.x: Renomeado para EditMessageMedia (sem Async)
                await bot.EditMessageMedia(
                    inlineMessageId: inlineMessageId,
                    media: media,
                    replyMarkup: inlineKeyboard,
                    cancellationToken: cancellationToken);

                usedSet.TryAdd(urlToTry, 0);
                break;
            }
            catch (ApiRequestException apiEx)
            {
                logger.LogWarning("Falha ao editar imagem {Url}. Telegram {Code}: {Message}", urlToTry, apiEx.ErrorCode, apiEx.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro inesperado ao editar a URL {Url}.", urlToTry);
            }
        }
    }

    // --- 7. Busca de links de imagem (Google -> SerpAPI) ---
    private static async Task<List<string>> FetchImageLinksAsync(
        string originalQuery,
        IHttpClientFactory clientFactory,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var http = clientFactory.CreateClient();
        var apiKey = Environment.GetEnvironmentVariable("GoogleApiKey");
        var cx = Environment.GetEnvironmentVariable("GoogleCx");
        var serpApiKey = Environment.GetEnvironmentVariable("SerpApiKey");

        // 1) Google Custom Search
        if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(cx))
        {
            try
            {
                var googleUrl =
                    $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(originalQuery)}&searchType=image&key={apiKey}&cx={cx}";
                var result = await http.GetFromJsonAsync<JsonElement>(googleUrl, cancellationToken);

                var links = new List<string>();
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var itemsElement))
                {
                    foreach (var item in itemsElement.EnumerateArray())
                    {
                        string? link = item.TryGetProperty("link", out var linkProp) ? linkProp.GetString() : null;
                        if (ImageUrlHelper.IsValidImageUrl(link))
                        {
                            links.Add(link!);
                        }
                    }
                }

                links = links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (links.Any()) return links;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "A requisição à API Google Custom Search falhou.");
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Falha ao analisar o JSON da API Google Custom Search.");
            }
        }

        // 2) SerpAPI (fallback)
        if (!string.IsNullOrEmpty(serpApiKey))
        {
            try
            {
                var serpUrl =
                    $"https://serpapi.com/search.json?engine=google_images&q={Uri.EscapeDataString(originalQuery)}&api_key={serpApiKey}";
                var serpResult = await http.GetFromJsonAsync<JsonElement>(serpUrl, cancellationToken);

                var links = new List<string>();
                if (serpResult.ValueKind == JsonValueKind.Object &&
                    serpResult.TryGetProperty("images_results", out var serpItems))
                {
                    foreach (var item in serpItems.EnumerateArray())
                    {
                        string? link = item.TryGetProperty("original", out var linkProp) ? linkProp.GetString() : null;
                        if (ImageUrlHelper.IsValidImageUrl(link))
                        {
                            links.Add(link!);
                        }
                    }
                }

                links = links.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (links.Any()) return links;
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(ex, "A requisição à SerpAPI falhou.");
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Falha ao analisar o JSON da SerpAPI.");
            }
        }

        return new List<string>();
    }
}