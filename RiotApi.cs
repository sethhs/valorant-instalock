using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Instalock
{
    public class RiotSession
    {
        public string Access, Entitlement, Puuid, Glz, Version, Port;
        public AuthenticationHeaderValue LocalAuth;
    }

    public class RiotAuthExpired : Exception { }

    public class RiotBusy : Exception { }

    static class RiotApi
    {
        const string Platform = "ew0KCSJwbGF0Zm9ybVR5cGUiOiAiUEMiLA0KCSJwbGF0Zm9ybU9TIjogIldpbmRvd3MiLA0KCSJwbGF0Zm9ybU9TVmVyc2lvbiI6ICIxMC4wLjE5MDQyLjEuMjU2LjY0Yml0IiwNCgkicGxhdGZvcm1DaGlwc2V0IjogIlVua25vd24iDQp9";
        static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly HttpClient LocalClient = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => message.RequestUri.Host == "127.0.0.1"
        }) { Timeout = TimeSpan.FromSeconds(5) };
        static readonly HttpClient Web = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        static string ReadShared(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static async Task<RiotSession> Connect()
        {
            var lockfile = Path.Combine(Local, "Riot Games", "Riot Client", "Config", "lockfile");
            var log = Path.Combine(Local, "VALORANT", "Saved", "Logs", "ShooterGame.log");
            if (!File.Exists(lockfile) || !File.Exists(log)) return null;
            var parts = ReadShared(lockfile).Split(':');
            if (parts.Length < 4) return null;
            var shard = Regex.Matches(ReadShared(log), @"https://glz-([a-z0-9-]+?)-1\.([a-z0-9]+)\.a\.pvp\.net");
            if (shard.Count == 0) return null;
            var last = shard[shard.Count - 1];

            var localAuth = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + parts[3])));
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{parts[2]}/entitlements/v1/token");
            request.Headers.Authorization = localAuth;
            using var response = await LocalClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            using var version = JsonDocument.Parse(await Web.GetStringAsync("https://valorant-api.com/v1/version"));
            return new RiotSession
            {
                Access = tokens.RootElement.GetProperty("accessToken").GetString(),
                Entitlement = tokens.RootElement.GetProperty("token").GetString(),
                Puuid = tokens.RootElement.GetProperty("subject").GetString(),
                Glz = $"https://glz-{last.Groups[1].Value}-1.{last.Groups[2].Value}.a.pvp.net",
                Port = parts[2],
                LocalAuth = localAuth,
                Version = version.RootElement.GetProperty("data").GetProperty("riotClientVersion").GetString()
            };
        }

        static async Task<HttpResponseMessage> Send(RiotSession s, HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, s.Glz + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.Access);
            request.Headers.Add("X-Riot-Entitlements-JWT", s.Entitlement);
            request.Headers.Add("X-Riot-ClientPlatform", Platform);
            request.Headers.Add("X-Riot-ClientVersion", s.Version);
            var response = await Web.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                response.Dispose();
                throw new RiotAuthExpired();
            }
            if ((int)response.StatusCode == 429)
            {
                response.Dispose();
                throw new RiotBusy();
            }
            return response;
        }

        public static async Task<bool?> InAgentSelect(RiotSession s)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{s.Port}/chat/v4/presences");
            request.Headers.Authorization = s.LocalAuth;
            using var response = await LocalClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("presences", out var list)) return null;
            foreach (var p in list.EnumerateArray())
            {
                if (!p.TryGetProperty("puuid", out var id) || id.GetString() != s.Puuid) continue;
                if (!p.TryGetProperty("product", out var product) || product.GetString() != "valorant") continue;
                if (!p.TryGetProperty("private", out var secret) || secret.ValueKind != JsonValueKind.String) return null;
                try
                {
                    return Encoding.UTF8.GetString(Convert.FromBase64String(secret.GetString())).Contains("\"PREGAME\"");
                }
                catch (FormatException)
                {
                    return null;
                }
            }
            return null;
        }

        public static async Task<(bool PickOpen, bool AlreadyLocked)> PickPhase(RiotSession s, string match)
        {
            using var response = await Send(s, HttpMethod.Get, $"/pregame/v1/matches/{match}");
            if (!response.IsSuccessStatusCode) return (false, false);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            bool open = root.TryGetProperty("PregameState", out var state) && state.GetString() == "character_select_active";
            bool locked = false;
            if (root.TryGetProperty("AllyTeam", out var team) && team.ValueKind == JsonValueKind.Object && team.TryGetProperty("Players", out var players))
                foreach (var p in players.EnumerateArray())
                    if (p.TryGetProperty("Subject", out var id) && id.GetString() == s.Puuid
                        && p.TryGetProperty("CharacterSelectionState", out var pick) && pick.GetString() == "locked")
                        locked = true;
            return (open, locked);
        }

        public static async Task<string> PregameMatch(RiotSession s)
        {
            using var response = await Send(s, HttpMethod.Get, $"/pregame/v1/players/{s.Puuid}");
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("MatchID", out var id) ? id.GetString() : null;
        }

        public static async Task<string> Lock(RiotSession s, string match, string agentId, System.Threading.CancellationToken ct)
        {
            using (var select = await Send(s, HttpMethod.Post, $"/pregame/v1/matches/{match}/select/{agentId}"))
                if (!select.IsSuccessStatusCode) return $"SELECT {(int)select.StatusCode}";
            await Task.Delay(500, ct);
            using var locked = await Send(s, HttpMethod.Post, $"/pregame/v1/matches/{match}/lock/{agentId}");
            return locked.IsSuccessStatusCode ? null : $"LOCK {(int)locked.StatusCode}";
        }
    }
}
