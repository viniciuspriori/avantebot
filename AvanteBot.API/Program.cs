using System.Net.Http.Json;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups; // <-- Add this for inline keyboards

var builder = WebApplication.CreateBuilder(args);

var token = Environment.GetEnvironmentVariable("BotToken")!;
var webhookUrl = Environment.GetEnvironmentVariable("BotWebhookUrl")!;

builder.Services.AddHttpClient("tgwebhook")
                .RemoveAllLoggers()
                .AddTypedClient(httpClient => new TelegramBotClient(token, httpClient));

var app = builder.Build();

// NOTE: imageCache now stores a list of image links for a given search query.
var imageCache = new Dictionary<string, List<string>>();
var http = new HttpClient();

// Constants for Callback Data
const string ImageCallbackPrefix = "NEXT_IMAGE:";

app.MapGet("/bot/setWebhook", async (TelegramBotClient bot) =>
{
    await bot.SetWebhook(webhookUrl);
    return $"Webhook set to {webhookUrl}";
});

app.MapGet("/", () => "AvanteBot is online!");
app.MapPost("/bot", OnUpdate); // Single entry point for all Telegram updates

app.Run();

// New helper method to handle the logic of fetching and sending an image.
// This is used by both the initial '/image' command and the inline button callback.
async Task SendNextImage(TelegramBotClient bot, long chatId, string query, string? username = null, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        await bot.SendMessage(chatId, "Search query is missing.", cancellationToken: cancellationToken);
        return;
    }

    var apiKey = Environment.GetEnvironmentVariable("GoogleApiKey")!;
    var cx = Environment.GetEnvironmentVariable("GoogleCx")!;

    var searchUrl = $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(query)}&searchType=image&key={apiKey}&cx={cx}";
    var result = await http.GetFromJsonAsync<JsonElement>(searchUrl, cancellationToken);

    if (!result.TryGetProperty("items", out var itemsElement))
    {
        await bot.SendMessage(chatId, $"No images found for '{query}'.", cancellationToken: cancellationToken);
        return;
    }

    var items = itemsElement.EnumerateArray()
        .Select(i => i.GetProperty("link").GetString())
        .Where(s => !string.IsNullOrEmpty(s))
        .ToList();

    if (!items.Any())
    {
        await bot.SendMessage(chatId, $"No images found for '{query}'.", cancellationToken: cancellationToken);
        return;
    }

    // --- Image Caching and Selection Logic ---
    if (!imageCache.ContainsKey(query))
        imageCache[query] = new List<string>();

    var remaining = items.Except(imageCache[query]).ToList();
    if (!remaining.Any())
    {
        imageCache[query].Clear();
        remaining = items;
        await bot.SendMessage(chatId, $"Resetting image pool for '{query}'. Sending the first image again.", cancellationToken: cancellationToken);
    }

    var random = new Random();
    var chosen = remaining[random.Next(remaining.Count)]!;

    imageCache[query].Add(chosen);

    // --- Caption Creation with Username ---
    var caption = $"Result for: {query}";
    if (username != null)
    {
        // Add the @username to the caption.
        // Telegram supports markdown for mentions, but for a simple username, text is fine.
        caption += $" requested by @{username}";
    }

    // --- Create Inline Keyboard ---
    var callbackData = $"{ImageCallbackPrefix}{query}";
    var inlineKeyboard = new InlineKeyboardMarkup(
        InlineKeyboardButton.WithCallbackData("🖼️ More Images", callbackData)
    );

    // --- Send Photo with Username in Caption ---
    await bot.SendPhoto(
        chatId: chatId,
        photo: InputFile.FromUri(chosen),
        caption: caption, // <-- Updated caption
        replyMarkup: inlineKeyboard,
        cancellationToken: cancellationToken
    );
}

// -----------------------------------------------------------------------------------------------------

async void OnGetImage(TelegramBotClient bot, Update update, Message msg)
{
    var query = msg!.Text!.Replace("/image", "").Trim();

    await SendNextImage(bot, msg.Chat.Id, query, msg.From?.Username);
}

// -----------------------------------------------------------------------------------------------------

async void OnUpdate(TelegramBotClient bot, Update update)
{
    // --- 1. Handle Callback Queries (Button Clicks) ---
    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callbackQuery)
    {
        await bot.AnswerCallbackQuery(callbackQuery.Id);

        var data = callbackQuery.Data;
        if (data != null && data.StartsWith(ImageCallbackPrefix))
        {
            var query = data.Replace(ImageCallbackPrefix, "");
            var chatId = callbackQuery.Message?.Chat.Id ?? callbackQuery.From.Id;

            await SendNextImage(bot, chatId, query, callbackQuery.From.Username);
        }
    }

    // --- 2. Handle Message Updates (Text Commands) ---
    else if (update.Type == UpdateType.Message && update.Message is { } msg && msg.Text is not null)
    {
        if (msg.Text.StartsWith("/image"))
        {
            OnGetImage(bot, update, msg);
        }
        else
        {
            await bot.SendMessage(msg.Chat.Id, $"{msg.From?.FirstName} said: {msg.Text}\nTry /image <term> to search for an image!");
        }
    }
}
