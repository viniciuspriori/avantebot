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
async Task SendNextImage(
    TelegramBotClient bot,
    long chatId,
    string query,
    string? username = null,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        await bot.SendMessage(chatId, "Search query is missing.", cancellationToken: cancellationToken);
        return;
    }

    var apiKey = Environment.GetEnvironmentVariable("GoogleApiKey");
    var cx = Environment.GetEnvironmentVariable("GoogleCx");
    var serpApiKey = Environment.GetEnvironmentVariable("SerpApiKey");

    List<string> imageLinks = new();

    try
    {
        // --- Try Google Custom Search API ---
        if (!string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(cx))
        {
            var searchUrl =
                $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(query)}&searchType=image&key={apiKey}&cx={cx}";
            var result = await http.GetFromJsonAsync<JsonElement>(searchUrl, cancellationToken);

            if (result.TryGetProperty("items", out var itemsElement))
            {
                var googleImages = itemsElement.EnumerateArray()
                    .Select(i => i.GetProperty("link").GetString())
                    .Where(IsValidImageUrl)
                    .ToList();

                imageLinks.AddRange(googleImages);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Google image search failed: {ex.Message}");
    }

    // --- Fallback: SerpAPI ---
    if (imageLinks.Count == 0 && !string.IsNullOrWhiteSpace(serpApiKey))
    {
        try
        {
            var serpUrl = $"https://serpapi.com/search.json?engine=google_images&q={Uri.EscapeDataString(query)}&api_key={serpApiKey}";
            var serpResult = await http.GetFromJsonAsync<JsonElement>(serpUrl, cancellationToken);

            if (serpResult.TryGetProperty("images_results", out var serpItems))
            {
                var serpImages = serpItems.EnumerateArray()
                    .Select(i => i.GetProperty("original").GetString())
                    .Where(IsValidImageUrl)
                    .ToList();

                imageLinks.AddRange(serpImages);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SerpAPI fallback failed: {ex.Message}");
        }
    }

    if (imageLinks.Count == 0)
    {
        await bot.SendMessage(chatId, $"No valid images found for '{query}'.", cancellationToken: cancellationToken);
        return;
    }

    // --- Image caching logic ---
    if (!imageCache.ContainsKey(query))
        imageCache[query] = new List<string>();

    var remaining = imageLinks.Except(imageCache[query]).ToList();
    if (remaining.Count == 0)
    {
        imageCache[query].Clear();
        remaining = imageLinks;
    }

    var random = new Random();
    var chosen = remaining[random.Next(remaining.Count)]!;
    imageCache[query].Add(chosen);

    var caption = $"Result for: \"{query}\"";
    if (username != null)
        caption += $" requested by @{username}";

    var callbackData = $"{ImageCallbackPrefix}{query}";
    var inlineKeyboard = new InlineKeyboardMarkup(
        InlineKeyboardButton.WithCallbackData("🖼️ More Images", callbackData)
    );

    await bot.SendPhoto(
        chatId: chatId,
        photo: InputFile.FromUri(chosen),
        caption: caption,
        replyMarkup: inlineKeyboard,
        cancellationToken: cancellationToken
    );
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
        if (msg.Text.StartsWith("/teste"))
        {
            var query = msg!.Text!.Replace("/teste", "").Trim();
            await bot.SendMessage(msg.Chat.Id, $"{msg.From?.FirstName} said: {query}\nTry /image <term> to search for an image!");
        }
    }
}


