using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Instalock
{
    public class Agent : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Role { get; set; }
        public string Icon { get; set; }
        public string IconUrl { get; set; }
        public string RoleIcon { get; set; }
        public string RoleIconUrl { get; set; }
        public ImageSource Image { get; set; }

        bool shown = true;
        public bool Shown
        {
            get => shown;
            set
            {
                shown = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Shown)));
            }
        }

        bool favorite;
        public bool Favorite
        {
            get => favorite;
            set
            {
                if (favorite == value) return;
                favorite = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Favorite)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    static class Agents
    {
        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Instalock");
        static readonly string Cache = Path.Combine(Folder, "cache");
        static readonly HttpClient Web = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        public static async Task<List<Agent>> Fetch()
        {
            Directory.CreateDirectory(Cache);
            var listPath = Path.Combine(Cache, "agents.json");
            JsonDocument doc;
            try
            {
                var json = await Web.GetStringAsync("https://valorant-api.com/v1/agents?isPlayableCharacter=true");
                doc = JsonDocument.Parse(json);
                try { File.WriteAllText(listPath, json); } catch (IOException) { }
            }
            catch (Exception) when (File.Exists(listPath))
            {
                doc = JsonDocument.Parse(File.ReadAllText(listPath));
            }
            using var parsed = doc;
            var agents = doc.RootElement.GetProperty("data").EnumerateArray()
                .Where(a => a.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.Object)
                .Select(a =>
                {
                    var role = a.GetProperty("role");
                    var id = a.GetProperty("uuid").GetString();
                    var roleName = role.GetProperty("displayName").GetString();
                    return new Agent
                    {
                        Name = a.GetProperty("displayName").GetString(),
                        Id = id,
                        Role = roleName,
                        Icon = Path.Combine(Cache, id + ".png"),
                        IconUrl = a.GetProperty("displayIcon").GetString(),
                        RoleIcon = Path.Combine(Cache, "role-" + roleName.ToLowerInvariant() + ".png"),
                        RoleIconUrl = role.GetProperty("displayIcon").GetString()
                    };
                })
                .OrderBy(a => a.Name).ToList();
            var files = agents.Select(a => (a.Icon, a.IconUrl))
                .Concat(agents.GroupBy(a => a.Role).Select(g => (g.First().RoleIcon, g.First().RoleIconUrl)));
            await Task.WhenAll(files.Select(f => Save(f.Item1, f.Item2)));
            agents.RemoveAll(a => !File.Exists(a.Icon) || !File.Exists(a.RoleIcon));
            foreach (var a in agents) a.Image = LoadImage(a.Icon);
            return agents;
        }

        static async Task Save(string path, string url)
        {
            if (File.Exists(path)) return;
            try
            {
                var bytes = await Web.GetByteArrayAsync(url);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, true);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException) { }
        }

        static ImageSource LoadImage(string path)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static void SelfTest()
        {
            var agents = Task.Run(Fetch).Result;
            if (agents.Count < 20) throw new Exception("agents");
            if (agents.Any(a => string.IsNullOrEmpty(a.Role) || a.Image == null)) throw new Exception("roles or images");
            if (Layout.Parse(new Layout { Monitor = new System.Drawing.Rectangle(0, 0, 1920, 1080), Tabs = new System.Drawing.Point[4], Pitch = 79.5 }.ToText())?.Pitch != 79.5)
                throw new Exception("layout round trip");
        }
    }
}
