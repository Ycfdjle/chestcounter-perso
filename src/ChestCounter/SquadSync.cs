using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace ChestCounter;

/// <summary>
/// Synchro squad : chaque ChestCounter de la squad envoie ses propres coffres (exacts, lus dans son replay) dans un
/// canal partagé, et lit ceux des autres. Le canal est un sujet du service gratuit ntfy.sh nommé d'après le code de
/// squad : pas de compte ni de serveur à gérer. Les messages restent 12 h sur ntfy.sh, donc chaque appli renvoie
/// régulièrement ses dernières parties et garde ce qu'elle a reçu dans config.json.
/// Ce qui est envoyé : l'id Epic, le pseudo, et pour chaque partie récente son identifiant et le nombre de coffres.
/// </summary>
public sealed class SquadSync
{
    public const string Server = "https://ntfy.sh";
    private const int MaxMessageBytes = 3800;   // ntfy.sh : au-delà de 4096 octets, le message devient une pièce jointe
    private const int MaxMatchesSent = 40;
    private const int MaxSessionsKept = 600;
    // ntfy.sh : 1 requête toutes les 5 s en continu, 250 messages par jour. Lire toutes les 10 s reste dessous ;
    // on n'envoie qu'à la fin d'une partie (et toutes les 2 h), pas pendant.
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RepublishEvery = TimeSpan.FromHours(2);

    private static readonly HttpClient Http = NewClient();

    private readonly Monitor _monitor;
    private readonly SemaphoreSlim _wake = new(0);
    private volatile bool _stop;
    private string _lastSent = "";
    private DateTime _nextRepublish = DateTime.MinValue;

    public SquadSync(Monitor monitor) => _monitor = monitor;

    /// <summary>État lisible (dernière synchro, erreur…), pour la fenêtre Squad.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Mates reçus : pseudo → nombre de parties connues.</summary>
    public IReadOnlyDictionary<string, int> Mates { get; private set; } = new Dictionary<string, int>();

    public DateTime? LastSyncLocal { get; private set; }

    public void Start() => _ = Task.Run(LoopAsync);

    public void Stop()
    {
        _stop = true;
        _wake.Release();
    }

    /// <summary>Synchronise tout de suite (code changé, nouvelle partie).</summary>
    public void Kick()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    /// <summary>Code valide : 6 à 40 lettres, chiffres, tirets ou soulignés.</summary>
    public static bool IsValidCode(string? code) =>
        code is { Length: >= 6 and <= 40 } && code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_');

    /// <summary>Nouveau code aléatoire, long pour qu'on ne puisse pas le deviner.</summary>
    public static string NewCode()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(14);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    private static string TopicUrl(string code) => $"{Server}/chestcounter-{code}";

    private async Task LoopAsync()
    {
        while (!_stop)
        {
            var config = _monitor.Config;
            var code = config.SquadCode;
            if (!config.SquadSync || !IsValidCode(code))
            {
                Status = config.SquadSync ? L.T("Código no válido.", "Invalid code.") : L.T("Sincronización desactivada.", "Sync turned off.");
            }
            else
            {
                try
                {
                    await SendAsync(code!);
                    await ReceiveAsync(code!);
                    LastSyncLocal = DateTime.Now;
                    Status = L.F("Sincronizado a las {0}", "Synced at {0}", LastSyncLocal.Value.ToString("HH:mm", L.Culture));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
                {
                    Status = L.T("Sin conexión con el servicio de sincronización; se reintenta en un minuto.", "No connection to the sync service; retrying in a minute.");
                    ErrorLog.Write("Squad", ex);
                }
                catch (Exception ex)
                {
                    Status = L.T("Error de sincronización (ver errors.log).", "Sync error (see errors.log).");
                    ErrorLog.Write("Squad", ex);
                }
            }
            _monitor.RequestPublish();
            try
            {
                await _wake.WaitAsync(PollEvery);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Envoie ses parties récentes si elles ont changé, ou toutes les 2 h pour les mates qui arrivent.</summary>
    private async Task SendAsync(string code)
    {
        var payload = _monitor.SquadPayload(MaxMatchesSent, MaxMessageBytes);
        if (payload == null || (payload == _lastSent && DateTime.UtcNow < _nextRepublish))
        {
            return;
        }
        using var content = new StringContent(payload, Encoding.UTF8, "text/plain");
        using var response = await Http.PostAsync(TopicUrl(code), content);
        response.EnsureSuccessStatusCode();
        _lastSent = payload;
        _nextRepublish = DateTime.UtcNow + RepublishEvery;
    }

    /// <summary>Lit les messages des 12 dernières heures et garde les comptes des autres joueurs.</summary>
    private async Task ReceiveAsync(string code)
    {
        using var response = await Http.GetAsync(TopicUrl(code) + "/json?poll=1&since=12h");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 2_000_000)
        {
            return; // canal anormal : on n'y touche pas
        }

        var me = _monitor.SquadSelfId();
        var incoming = new Dictionary<string, Dictionary<string, int>>();
        var names = new Dictionary<string, string>();
        var totals = new Dictionary<string, long>();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Parse(line) is not { } msg || msg.Id == me)
            {
                continue;
            }
            names[msg.Id] = msg.Name;
            if (msg.Total is { } total)
            {
                totals[msg.Id] = total;
            }
            foreach (var (session, chests) in msg.Matches)
            {
                if (!incoming.TryGetValue(session, out var perMate))
                {
                    incoming[session] = perMate = new Dictionary<string, int>();
                }
                perMate[msg.Id] = chests; // le message le plus récent l'emporte (ils arrivent dans l'ordre)
            }
        }
        _monitor.MergeSquadCounts(incoming, MaxSessionsKept, names, totals);

        // Résumé pour la fenêtre : pseudo → parties connues (tout ce qui est gardé, pas seulement les 12 h).
        var known = _monitor.Config.SquadCounts;
        var savedNames = _monitor.Config.SquadNames ?? new Dictionary<string, string>();
        var summary = new Dictionary<string, int>();
        foreach (var perMate in known.Values)
        {
            foreach (var id in perMate.Keys)
            {
                var label = names.TryGetValue(id, out var n) ? n : savedNames.TryGetValue(id, out var saved) ? saved : id;
                summary[label] = summary.GetValueOrDefault(label) + 1;
            }
        }
        Mates = summary;
    }

    private sealed record Message(string Id, string Name, List<(string Session, int Chests)> Matches, long? Total);

    /// <summary>
    /// Lit une ligne de ntfy.sh ({"event":"message","message":"..."}) et le message de ChestCounter qu'elle porte.
    /// Tout ce qui vient du réseau est vérifié : tailles, types, bornes. Null si la ligne n'est pas valide.
    /// </summary>
    private static Message? Parse(string line)
    {
        try
        {
            using var envelope = JsonDocument.Parse(line);
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var ev) || ev.GetString() != "message"
                || !root.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var raw = text.GetString();
            if (string.IsNullOrEmpty(raw) || raw.Length > 8000)
            {
                return null;
            }
            using var doc = JsonDocument.Parse(raw);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !m.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String
                || !m.TryGetProperty("m", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var id = idEl.GetString() ?? "";
            if (id.Length is 0 or > 64)
            {
                return null;
            }
            var name = m.TryGetProperty("n", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() ?? "" : "";
            if (name.Length > 40)
            {
                name = name[..40];
            }
            var matches = new List<(string, int)>();
            foreach (var item in list.EnumerateArray().Take(100))
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String
                    && item.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var chests)
                    && s.GetString() is { Length: > 0 and <= 128 } session && chests is >= 0 and <= 1000)
                {
                    matches.Add((session, chests));
                }
            }
            long? total = m.TryGetProperty("t", out var tEl) && tEl.ValueKind == JsonValueKind.Number && tEl.TryGetInt64(out var t) && t is >= 0 and <= 10_000_000 ? t : null;
            return new Message(id, name.Length > 0 ? name : id, matches, total);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ChestCounter/{AppInfo.VersionText}");
        return http;
    }
}
