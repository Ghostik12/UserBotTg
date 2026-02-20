using System.Text.RegularExpressions;
using Microsoft.Playwright;
using TL;
using WTelegram;

class Program
{
    private static Client _tgClient;
    private static User _behavtopBot;
    private static IPlaywright _playwright;
    private static IBrowserContext _browserContext; // persistent профиль
    private static readonly SemaphoreSlim _semaphore = new(1, 1); // чтобы не обрабатывать 2 задачи одновременно
    private static readonly Random _rnd = new();

    static async Task Main()
    {
        Console.WriteLine("Запуск Behavtop Farmer v1.0...");

        // === 1. Telegram Userbot ===
        _tgClient = new Client(what =>
        {
            return what switch
            {
                "api_id" => "ТОЙ СВОЙ api_id",          // my.telegram.org
                "api_hash" => "ТОЙ СВОЙ api_hash",
                "phone_number" => "+7916XXXXXXX",       // твой номер
                "verification_code" => Console.ReadLine(), // введёшь при первом запуске
                "password" => "твой 2FA пароль если есть",
                _ => null
            };
        });

        _tgClient.OnUpdates += OnUpdate;
        var myself = await _tgClient.LoginUserIfNeeded();
        Console.WriteLine($"Залогинен как {myself.username ?? myself.first_name}");

        // Находим @Behavtop_bot
        _behavtopBot = (await _tgClient.Contacts_ResolveUsername("Behavtop_bot")).User;
        Console.WriteLine("Behavtop_bot найден");

        // === 2. Playwright (persistent профиль Behance) ===
        _playwright = await Playwright.CreateAsync();
        _browserContext = await _playwright.Chromium.LaunchPersistentContextAsync(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BehanceProfile"),
            new BrowserTypeLaunchPersistentContextOptions
            {
                Headless = false,           // false = видно окно (рекомендую для первого теста)
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1366, Height = 768 },
                DeviceScaleFactor = 1,
                IsMobile = false,
                HasTouch = false,
                BypassCSP = true,
                IgnoreHTTPSErrors = true
            });

        Console.WriteLine("Браузер готов. Если первый запуск — зайди вручную на behance.net и залогинься, потом закрой окно.");
        Console.WriteLine("Бот запущен и ждёт задания от @Behavtop_bot... Нажми Ctrl+C для остановки.");

        await Task.Delay(-1); // бесконечный запуск
    }

    private static async Task OnUpdate(UpdatesBase updates)
    {
        foreach (var update in updates.UpdateList)
        {
            if (update is not UpdateNewMessage unm) continue;
            var msg = unm.message as Message;
            if (msg == null) continue;

            if (msg.peer_id.ID != _behavtopBot.id) continue;

            var text = msg.message ?? "";
            Console.WriteLine($"Новое сообщение от бота: {text}");

            var match = Regex.Match(text, @"(https?://www\.behance\.net/gallery/\d+/[^ \n]+)");
            if (!match.Success) continue;

            var projectUrl = match.Groups[1].Value;
            Console.WriteLine($"Найдена задача → {projectUrl}");

            await _semaphore.WaitAsync();
            try
            {
                await ProcessBehanceTask(projectUrl, msg.id, msg.peer_id);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    private static async Task ProcessBehanceTask(string url, int msgId, Peer peer)
    {
        IPage page = null;
        try
        {
            page = await _browserContext.NewPageAsync();

            Console.WriteLine("Открываю проект...");
            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60000 });

            await SmoothHumanScroll(page);

            await Task.Delay(1500 + _rnd.Next(1000));

            // ─── ИСПРАВЛЕННЫЙ ПОИСК КНОПКИ ────────────────────────────────
            // Вариант 1: getByRole с точным текстом (если язык известен)
            // Вариант 2: более надёжный — Locator + Filter с Regex

            ILocator appreciateBtn;

            // Попробуем через getByRole (Name — только строка!)
            appreciateBtn = page.GetByRole(AriaRole.Button, new() { Name = "Appreciate" });
            if (await appreciateBtn.CountAsync() == 0)
                appreciateBtn = page.GetByRole(AriaRole.Button, new() { Name = "Оценить" });

            // Если всё ещё не нашли → используем Filter + Regex (самый гибкий вариант)
            if (await appreciateBtn.CountAsync() == 0)
            {
                appreciateBtn = page.Locator("button").Filter(new()
                {
                    HasTextRegex = new Regex(@"Оценить|Appreciate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                }).First;
            }

            if (await appreciateBtn.IsVisibleAsync())
            {
                Console.WriteLine("Кликаем Оценить...");
                await appreciateBtn.ClickAsync(new LocatorClickOptions { Delay = 150 + _rnd.Next(100) });
                await Task.Delay(2000 + _rnd.Next(1500));
                Console.WriteLine("✅ Лайк поставлен!");
            }
            else
            {
                Console.WriteLine("⚠ Кнопка 'Оценить/Appreciate' не найдена (возможно уже лайкнуто)");
            }

            // ─── Отправка "Готово" ─────────────────────────────────────────
            // Используем InputPeer от _behavtopBot (он уже User → InputPeerUser)
            await _tgClient.SendMessageAsync(_behavtopBot, "Готово");

            // Пробуем нажать inline-кнопку, если есть
            await TryClickGotovoButton(msgId, peer);

            Console.WriteLine("✅ Задача выполнена!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex.Message}");
        }
        finally
        {
            if (page != null) await page.CloseAsync();
        }

        // задержка 2+ минуты
        var delaySec = 120 + _rnd.Next(30, 90);
        Console.WriteLine($"Ждём {delaySec} сек...");
        await Task.Delay(delaySec * 1000);
    }

    private static async Task SmoothHumanScroll(IPage page)
    {
        await page.EvaluateAsync(@"
            async () => {
                const totalHeight = document.body.scrollHeight;
                let current = 0;
                const step = 80 + Math.random() * 40;

                while (current < totalHeight) {
                    window.scrollBy(0, step);
                    current += step;
                    await new Promise(r => setTimeout(r, 25 + Math.random() * 35)); // 25-60ms
                }
                
                window.scrollBy(0, -150);
                await new Promise(r => setTimeout(r, 800));
                window.scrollBy(0, 200);
            }
        ");
        await Task.Delay(800);
    }

    private static async Task TryClickGotovoButton(int msgId, Peer peer)
    {
        try
        {
            var msgs = await _tgClient.Messages_GetHistory(new InputPeerUser(_behavtopBot.id, _behavtopBot.access_hash), limit: 5);
            var lastMsg = msgs.Messages[0] as Message;

            if (lastMsg?.reply_markup is ReplyInlineMarkup markup)
            {
                foreach (var row in markup.rows)
                {
                    foreach (var btn in row.buttons)
                    {
                        if (btn is KeyboardButtonCallback cb &&
                            (cb.text.Contains("Готово") || cb.text.Contains("готово")))
                        {
                            Console.WriteLine($"Нажимаем inline-кнопку '{cb.text}'...");
                            await _tgClient.Messages_GetBotCallbackAnswer(
                                new InputPeerUser(_behavtopBot.id, _behavtopBot.access_hash),
                                msgId,
                                data: cb.data);
                            return;
                        }
                    }
                }
            }
        }
        catch { /* тихо */ }
    }
}
