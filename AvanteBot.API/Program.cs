using System.Net.Http.Json;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

var builder = WebApplication.CreateBuilder(args);
//var token = builder.Configuration["BotToken"]!;
var token = Environment.GetEnvironmentVariable("BotToken")!; // set your bot token in appsettings.json

var webhookUrl = Environment.GetEnvironmentVariable("BotWebhookUrl")!; // set your bot token in appsettings.json
//var webhookUrl = builder.Configuration["BotWebhookUrl"]!;   // set your bot webhook public url in appsettings.json

builder.Services.AddHttpClient("tgwebhook")
                .RemoveAllLoggers()
                .AddTypedClient(httpClient => new TelegramBotClient(token, httpClient));

var app = builder.Build();
//app.UseHttpsRedirection();

var imageCache = new Dictionary<string, List<string>>();
var http = new HttpClient();

app.MapGet("/bot/setWebhook", async (TelegramBotClient bot) =>
{
    await bot.SetWebhook(webhookUrl); 
    return $"Webhook set to {webhookUrl}";
});


app.MapGet("/", () => "AvanteBot is online!");
app.Run();

app.MapPost("/bot", OnUpdate);

app.Run();

async void OnUpdate(TelegramBotClient bot, Update update)
{
    if (update.Message is null) return;
    if (update.Message.Text is null) return;

    var msg = update.Message;

    if (msg.Text.StartsWith("/image"))
    {
        var query = msg.Text.Replace("/image", "").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            await bot.SendMessage(msg.Chat.Id, "Usage: /image <search term>");
            return;
        }

        var searchUrl = $"https://www.googleapis.com/customsearch/v1?q={Uri.EscapeDataString(query)}&searchType=image&key={builder.Configuration["GoogleApiKey"]}&cx={builder.Configuration["GoogleCx"]}";
        var result = await http.GetFromJsonAsync<JsonElement>(searchUrl);

        if (!result.TryGetProperty("items", out var itemsElement))
        {
            await bot.SendMessage(msg.Chat.Id, $"No images found for '{query}'.");
            return;
        }

        var items = itemsElement.EnumerateArray().Select(i => i.GetProperty("link").GetString()).Where(s => !string.IsNullOrEmpty(s)).ToList();

        if (!items.Any())
        {
            await bot.SendMessage(msg.Chat.Id, $"No images found for '{query}'.");
            return;
        }

        if (!imageCache.ContainsKey(query))
            imageCache[query] = new List<string>();

        var remaining = items.Except(imageCache[query]).ToList();
        if (!remaining.Any())
        {
            imageCache[query].Clear();
            remaining = items;
        }

        var random = new Random();
        var chosen = remaining[random.Next(remaining.Count)]!;

        imageCache[query].Add(chosen);
        await bot.SendPhoto(msg.Chat.Id, chosen, caption: $"Result for: {query}");
    }
    else
    {
        await bot.SendMessage(msg.Chat.Id, $"{msg.From?.FirstName} said: {msg.Text}\nTry /image <term> to search for an image!");
    }
}
