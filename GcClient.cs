using System.Reflection;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.GC;
using DotaGC = SteamKit2.GC.Dota.Internal;
using SteamInt = SteamKit2.Internal;

namespace DotaMmrTracker;

/// Один матч из истории, которую отдаёт Game Coordinator Dota 2.
public record GcMatch(
    long MatchId, long StartTime, int HeroId, bool Won,
    int Lobby, int GameMode, int Duration,
    int PreviousRank,   // рейтинг ДО матча (0 — не рейтинговый / не выдан)
    int RankChange);    // изменение рейтинга за матч

public record GcResult(uint AccountId, string AccountName, string? NewRefreshToken, List<GcMatch> Matches, string Dump);

public class TokenRejectedException : Exception
{
    public TokenRejectedException() : base("Сохранённая сессия Steam больше недействительна.") { }
}

/// Запрашивает у пользователя код Steam Guard.
sealed class PromptAuthenticator : IAuthenticator
{
    readonly Func<string, Task<string>> ask;
    readonly Action<string> notify;
    public PromptAuthenticator(Func<string, Task<string>> ask, Action<string> notify)
    {
        this.ask = ask;
        this.notify = notify;
    }

    public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect) =>
        ask(previousCodeWasIncorrect
            ? "Код неверный. Введите актуальный код Steam Guard из мобильного приложения:"
            : "Введите код Steam Guard из мобильного приложения:");

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect) =>
        ask(previousCodeWasIncorrect
            ? "Код неверный. Введите код из письма Steam:"
            : $"Введите код Steam Guard, отправленный на {email}:");

    // Если у аккаунта вход подтверждается кнопкой «Одобрить» в приложении Steam — ждём нажатия.
    public Task<bool> AcceptDeviceConfirmationAsync()
    {
        notify("Откройте приложение Steam на телефоне и нажмите «Одобрить» для входа…");
        return Task.FromResult(true);
    }
}

static class GcClient
{
    const uint DotaAppId = 570;

    static async Task<T> Wait<T>(Task<T> t, int seconds, string what, CancellationToken ct)
    {
        var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        if (done != t) throw new TimeoutException($"Таймаут: {what}.");
        return await t;
    }

    static async Task Wait(Task t, int seconds, string what, CancellationToken ct)
    {
        var done = await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(seconds), ct));
        if (done != t) throw new TimeoutException($"Таймаут: {what}.");
        await t;
    }

    // Имена в EDOTAGCMsg отличаются между версиями SteamKit2, поэтому ищем по названию.
    static uint FindMsg(bool response)
    {
        foreach (var name in Enum.GetNames(typeof(DotaGC.EDOTAGCMsg)))
        {
            if (name.Contains("PlayerMatchHistory") && name.EndsWith("Response") == response)
                return Convert.ToUInt32(Enum.Parse(typeof(DotaGC.EDOTAGCMsg), name));
        }
        throw new Exception("В этой версии SteamKit2 не найдено сообщение истории матчей (PlayerMatchHistory).");
    }

    // Поля protobuf читаем через рефлексию: это устойчиво к смене типов (uint/int/bool) между версиями.
    static long Num(object o, string name)
    {
        var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || p.GetIndexParameters().Length > 0) return 0;
        try { return p.GetValue(o) is IConvertible c ? Convert.ToInt64(c) : 0; }
        catch { return 0; }
    }

    static void SetProp(object o, string name, object value)
    {
        var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p == null || !p.CanWrite) return;
        try { p.SetValue(o, Convert.ChangeType(value, p.PropertyType)); } catch { }
    }

    static string DumpOf(object o) =>
        string.Join(", ", o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => { try { return $"{p.Name}={p.GetValue(o)}"; } catch { return p.Name + "=?"; } }));

    public static async Task<GcResult> FetchAsync(
        string username, string? password, string? refreshToken,
        Func<string, Task<string>> askCode, Action<string> log, CancellationToken ct)
    {
        uint historyReqId = FindMsg(false);
        uint historyRespId = FindMsg(true);

        var steam = new SteamClient();
        var mgr = new CallbackManager(steam);
        var user = steam.GetHandler<SteamUser>()!;
        var gc = steam.GetHandler<SteamGameCoordinator>()!;

        var connected = new TaskCompletionSource();
        var loggedOn = new TaskCompletionSource<SteamUser.LoggedOnCallback>();
        var welcome = new TaskCompletionSource();
        var history = new TaskCompletionSource<IPacketGCMsg>();
        bool stop = false, finishing = false;

        mgr.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        mgr.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            if (finishing) return;
            var ex = new Exception("Соединение со Steam разорвано.");
            connected.TrySetException(ex);
            loggedOn.TrySetException(ex);
            welcome.TrySetException(ex);
            history.TrySetException(ex);
        });
        mgr.Subscribe<SteamUser.LoggedOnCallback>(cb => loggedOn.TrySetResult(cb));
        mgr.Subscribe<SteamGameCoordinator.MessageCallback>(cb =>
        {
            if (cb.EMsg == (uint)DotaGC.EGCBaseClientMsg.k_EMsgGCClientWelcome)
                welcome.TrySetResult();
            else if (cb.EMsg == historyRespId)
                history.TrySetResult(cb.Message);
        });

        var pump = Task.Run(() =>
        {
            while (!stop) mgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(250));
        });

        try
        {
            log("Подключение к Steam…");
            steam.Connect();
            await Wait(connected.Task, 20, "подключение к Steam", ct);

            string account = username;
            string token = refreshToken ?? "";
            string? newToken = null;

            if (token == "")
            {
                if (string.IsNullOrEmpty(password)) throw new Exception("Не указан пароль.");
                log("Авторизация…");
                var session = await steam.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = new PromptAuthenticator(askCode, log)
                });
                var poll = await session.PollingWaitForResultAsync();
                account = poll.AccountName;
                token = poll.RefreshToken;
                newToken = token;
            }

            log("Вход в аккаунт…");
            user.LogOn(new SteamUser.LogOnDetails
            {
                Username = account,
                AccessToken = token,
                ShouldRememberPassword = true
            });
            var lo = await Wait(loggedOn.Task, 30, "вход в Steam", ct);
            if (lo.Result != EResult.OK)
            {
                if (refreshToken != null &&
                    (lo.Result == EResult.InvalidPassword || lo.Result == EResult.AccessDenied ||
                     lo.Result == EResult.Expired || lo.Result == EResult.Revoked))
                    throw new TokenRejectedException();
                throw new Exception($"Вход не удался: {lo.Result}");
            }
            uint accountId = lo.ClientSteamID!.AccountID;

            // «запускаем» Dota 2 и здороваемся с Game Coordinator
            log("Подключение к Dota 2 GC…");
            var play = new ClientMsgProtobuf<SteamInt.CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);
            play.Body.games_played.Add(new SteamInt.CMsgClientGamesPlayed.GamePlayed { game_id = new GameID(DotaAppId) });
            steam.Send(play);
            await Task.Delay(2000, ct);

            for (int i = 0; i < 10 && !welcome.Task.IsCompleted; i++)
            {
                var hello = new ClientGCMsgProtobuf<DotaGC.CMsgClientHello>((uint)DotaGC.EGCBaseClientMsg.k_EMsgGCClientHello);
                hello.Body.engine = DotaGC.ESourceEngine.k_ESE_Source2;
                gc.Send(hello, DotaAppId);
                await Task.WhenAny(welcome.Task, Task.Delay(3000, ct));
            }
            await Wait(welcome.Task, 1, "ответ Dota 2 GC (возможно, Dota сейчас запущена)", ct);

            // история матчей
            log("Запрос истории матчей…");
            ClientGCMsgProtobuf<DotaGC.CMsgDOTAGetPlayerMatchHistoryResponse> resp = null!;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                history = new TaskCompletionSource<IPacketGCMsg>();

                var req = new ClientGCMsgProtobuf<DotaGC.CMsgDOTAGetPlayerMatchHistory>(historyReqId);
                req.Body.account_id = accountId;
                req.Body.matches_requested = 20;   // размер страницы GC; больше — GC отвечает пустым списком
                SetProp(req.Body, "request_id", attempt);
                SetProp(req.Body, "start_at_match_id", 0);
                SetProp(req.Body, "include_practice_matches", true);
                SetProp(req.Body, "include_custom_games", true);
                SetProp(req.Body, "include_event_games", true);
                gc.Send(req, DotaAppId);

                var pkt = await Wait(history.Task, 25, "история матчей от GC", ct);
                resp = new ClientGCMsgProtobuf<DotaGC.CMsgDOTAGetPlayerMatchHistoryResponse>(pkt);
                if (resp.Body.matches.Count > 0) break;

                log($"GC вернул пустую историю, повтор {attempt}/3…");
                await Task.Delay(4000, ct);
            }

            var list = new List<GcMatch>();
            var dump = new System.Text.StringBuilder();
            dump.AppendLine($"account_id={accountId}, request msg id={historyReqId}, response msg id={historyRespId}");
            dump.AppendLine("enum names with 'History': " +
                string.Join(", ", Enum.GetNames(typeof(DotaGC.EDOTAGCMsg)).Where(x => x.Contains("History"))));
            dump.AppendLine("response: " + DumpOf(resp.Body));
            dump.AppendLine($"matches in response: {resp.Body.matches.Count}");
            int n = 0;
            foreach (var m in resp.Body.matches)
            {
                if (n++ < 3) dump.AppendLine(DumpOf(m));
                list.Add(new GcMatch(
                    Num(m, "match_id"), Num(m, "start_time"), (int)Num(m, "hero_id"),
                    Num(m, "winner") != 0, (int)Num(m, "lobby_type"), (int)Num(m, "game_mode"),
                    (int)Num(m, "duration"), (int)Num(m, "previous_rank"), (int)Num(m, "rank_change")));
            }
            return new GcResult(accountId, account, newToken, list, dump.ToString());
        }
        finally
        {
            finishing = true;
            try { user.LogOff(); } catch { }
            try { steam.Disconnect(); } catch { }
            stop = true;
            try { await pump; } catch { }
        }
    }
}

static class Heroes
{
    static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Anti-Mage", [2] = "Axe", [3] = "Bane", [4] = "Bloodseeker", [5] = "Crystal Maiden",
        [6] = "Drow Ranger", [7] = "Earthshaker", [8] = "Juggernaut", [9] = "Mirana", [10] = "Morphling",
        [11] = "Shadow Fiend", [12] = "Phantom Lancer", [13] = "Puck", [14] = "Pudge", [15] = "Razor",
        [16] = "Sand King", [17] = "Storm Spirit", [18] = "Sven", [19] = "Tiny", [20] = "Vengeful Spirit",
        [21] = "Windranger", [22] = "Zeus", [23] = "Kunkka", [25] = "Lina", [26] = "Lion",
        [27] = "Shadow Shaman", [28] = "Slardar", [29] = "Tidehunter", [30] = "Witch Doctor", [31] = "Lich",
        [32] = "Riki", [33] = "Enigma", [34] = "Tinker", [35] = "Sniper", [36] = "Necrophos",
        [37] = "Warlock", [38] = "Beastmaster", [39] = "Queen of Pain", [40] = "Venomancer",
        [41] = "Faceless Void", [42] = "Wraith King", [43] = "Death Prophet", [44] = "Phantom Assassin",
        [45] = "Pugna", [46] = "Templar Assassin", [47] = "Viper", [48] = "Luna", [49] = "Dragon Knight",
        [50] = "Dazzle", [51] = "Clockwerk", [52] = "Leshrac", [53] = "Nature's Prophet", [54] = "Lifestealer",
        [55] = "Dark Seer", [56] = "Clinkz", [57] = "Omniknight", [58] = "Enchantress", [59] = "Huskar",
        [60] = "Night Stalker", [61] = "Broodmother", [62] = "Bounty Hunter", [63] = "Weaver", [64] = "Jakiro",
        [65] = "Batrider", [66] = "Chen", [67] = "Spectre", [68] = "Ancient Apparition", [69] = "Doom",
        [70] = "Ursa", [71] = "Spirit Breaker", [72] = "Gyrocopter", [73] = "Alchemist", [74] = "Invoker",
        [75] = "Silencer", [76] = "Outworld Destroyer", [77] = "Lycan", [78] = "Brewmaster", [79] = "Shadow Demon",
        [80] = "Lone Druid", [81] = "Chaos Knight", [82] = "Meepo", [83] = "Treant Protector", [84] = "Ogre Magi",
        [85] = "Undying", [86] = "Rubick", [87] = "Disruptor", [88] = "Nyx Assassin", [89] = "Naga Siren",
        [90] = "Keeper of the Light", [91] = "Io", [92] = "Visage", [93] = "Slark", [94] = "Medusa",
        [95] = "Troll Warlord", [96] = "Centaur Warrunner", [97] = "Magnus", [98] = "Timbersaw",
        [99] = "Bristleback", [100] = "Tusk", [101] = "Skywrath Mage", [102] = "Abaddon", [103] = "Elder Titan",
        [104] = "Legion Commander", [105] = "Techies", [106] = "Ember Spirit", [107] = "Earth Spirit",
        [108] = "Underlord", [109] = "Terrorblade", [110] = "Phoenix", [111] = "Oracle", [112] = "Winter Wyvern",
        [113] = "Arc Warden", [114] = "Monkey King", [119] = "Dark Willow", [120] = "Pangolier",
        [121] = "Grimstroke", [123] = "Hoodwink", [126] = "Void Spirit", [128] = "Snapfire", [129] = "Mars",
        [131] = "Ringmaster", [135] = "Dawnbreaker", [136] = "Marci", [137] = "Primal Beast",
        [138] = "Muerta", [145] = "Kez"
    };

    public static string Name(int id) => Names.TryGetValue(id, out var n) ? n : $"Hero #{id}";
}