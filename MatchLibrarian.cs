using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Net;
using System.Text;
using System.Text.Json;

namespace MatchLibrarian;

public class MatchLibrarian : BasePlugin
{
    public override string ModuleName => "MatchLibrarian";
    public override string ModuleVersion => "1.0.2";
    public override string ModuleAuthor => "VinSix";

    private readonly HttpListener _httpListener = new();
    private string _basePath = "";
    private bool _isServerRunning = false;
    private const int ApiPort = 5000;

    public override void Load(bool hotReload)
    {
        // Define path to matches folder
        _basePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", "MatchLibrarian", "matches");
        Directory.CreateDirectory(_basePath);

        // Start the API Server
        StartHttpServer();
    }

    public override void Unload(bool hotReload)
    {
        _isServerRunning = false;
        try { _httpListener.Stop(); _httpListener.Close(); } catch { }
    }

    private void StartHttpServer()
    {
        Task.Run(async () =>
        {
            try
            {
                if (!_httpListener.IsListening)
                {
                    _httpListener.Prefixes.Add($"http://*:{ApiPort}/");
                    _httpListener.Start();
                }
                _isServerRunning = true;

                while (_isServerRunning)
                {
                    try
                    {
                        var context = await _httpListener.GetContextAsync();
                        var request = context.Request;
                        var response = context.Response;

                        // Allow CORS
                        response.Headers.Add("Access-Control-Allow-Origin", "*");

                        string path = request.Url?.AbsolutePath.ToLower() ?? "";
                        byte[] buffer = Array.Empty<byte>();

                        // Endpoint: Get list of dates
                        if (path == "/api/dates")
                        {
                            var dates = new List<string>();
                            if (Directory.Exists(_basePath))
                            {
                                foreach (var yearDir in Directory.GetDirectories(_basePath))
                                {
                                    var year = Path.GetFileName(yearDir);
                                    foreach (var monthDir in Directory.GetDirectories(yearDir))
                                    {
                                        var month = Path.GetFileName(monthDir);
                                        foreach (var dayDir in Directory.GetDirectories(monthDir))
                                        {
                                            var day = Path.GetFileName(dayDir);
                                            dates.Add($"{year}-{month}-{day}");
                                        }
                                    }
                                }
                            }
                            dates.Sort((a, b) => string.Compare(b, a));
                            string json = JsonSerializer.Serialize(dates);
                            buffer = Encoding.UTF8.GetBytes(json);
                            response.ContentType = "application/json";
                        }
                        // Endpoint: Get list of matches for a date
                        else if (path == "/api/matches")
                        {
                            string dateParam = request.QueryString["date"] ?? "";
                            var parts = dateParam.Split('-');
                            if (parts.Length == 3)
                            {
                                string targetPath = Path.Combine(_basePath, parts[0], parts[1], parts[2]);
                                if (Directory.Exists(targetPath))
                                {
                                    var files = Directory.GetFiles(targetPath, "*.json")
                                                         .Select(Path.GetFileName)
                                                         .ToList();
                                    buffer = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(files));
                                }
                                else { buffer = Encoding.UTF8.GetBytes("[]"); }
                            }
                            else { buffer = Encoding.UTF8.GetBytes("[]"); }
                            response.ContentType = "application/json";
                        }
                        // Endpoint: Get specific match JSON
                        else if (path == "/api/data")
                        {
                            string fileParam = request.QueryString["file"] ?? "";
                            if (!string.IsNullOrEmpty(fileParam) && !fileParam.Contains(".."))
                            {
                                var pathParts = fileParam.Split('/');
                                if (pathParts.Length == 2)
                                {
                                    var dateParts = pathParts[0].Split('-');
                                    string fileName = pathParts[1];
                                    if (dateParts.Length == 3)
                                    {
                                        string fullPath = Path.Combine(_basePath, dateParts[0], dateParts[1], dateParts[2], fileName);
                                        if (File.Exists(fullPath))
                                        {
                                            string content = File.ReadAllText(fullPath);
                                            buffer = Encoding.UTF8.GetBytes(content);
                                        }
                                    }
                                }
                            }
                            if (buffer.Length == 0) buffer = Encoding.UTF8.GetBytes("{}");
                            response.ContentType = "application/json";
                        }

                        response.ContentLength64 = buffer.Length;
                        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                        response.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[MatchLibrarian] Request Error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MatchLibrarian] Server Error: {ex.Message}");
            }
        });
    }
}
