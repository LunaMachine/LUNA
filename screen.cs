#:package Iot.Device.Bindings@3.2.0
#:package System.Drawing.Common@8.0.0
#:package SkiaSharp@2.88.6
#:package Iot.Device.Bindings.SkiaSharpAdapter@3.2.0
#:property PublishAot=false

using System.Device.I2c;
using System.Device.Gpio;
using Iot.Device.Ssd13xx;
using Iot.Device.Graphics;
using Iot.Device.Graphics.SkiaSharpAdapter;
using System.Drawing;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using System.Text.Json;

// Check for GPIO and I2C support before starting
try
{
    // Try to access GPIO controller to verify hardware support
    using var gpioTest = new GpioController();
    Console.WriteLine("GPIO support detected");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: GPIO not supported on this system: {ex.Message}");
    Console.WriteLine("This program requires GPIO and I2C hardware support (Raspberry Pi).");
    return;
}

try
{
    // Try to access I2C to verify it's enabled
    using var i2cTest = I2cDevice.Create(new I2cConnectionSettings(0, 0x3C));
    Console.WriteLine("I2C support detected");
}
catch (Exception ex)
{
    Console.WriteLine($"ERROR: I2C not available: {ex.Message}");
    Console.WriteLine("Please enable I2C in raspi-config or /boot/firmware/config.txt");
    return;
}

// Register SkiaSharp for graphics operations
SkiaSharpAdapter.Register();

// I2C Setup
using var oled = new Ssd1306(I2cDevice.Create(new I2cConnectionSettings(0, 0x3C)), 128, 64);

// HTTP client for Ollama
using var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromSeconds(30);

// Constants for OLED display layout
const int MaxCharsPerLine = 25;
const int MaxMessageY = 60;

// Constants for Ollama API
const double OllamaTemperature = 0.9;
const int OllamaMaxTokens = 50;

// Helper function to get AI message from LUNA
async Task<string> GetLunaMessageAsync() {
    try {
        var hour = DateTime.Now.Hour;
        var promptType = (DateTime.Now.Minute / 15) % 4; // Rotate message type every 15 minutes
        
        var prompt = promptType switch {
            0 => "Generate a brief status update from an AI assistant named LUNA (1 short sentence, max 25 words).",
            1 => hour < 12 ? "Generate a brief good morning greeting from LUNA (1 short sentence, max 25 words)." :
                 hour < 18 ? "Generate a brief good afternoon greeting from LUNA (1 short sentence, max 25 words)." :
                            "Generate a brief good evening greeting from LUNA (1 short sentence, max 25 words).",
            2 => "Generate a brief quirky comment about LUNA's mood as an AI (1 short sentence, max 25 words).",
            _ => "Share one brief interesting tech fact from LUNA (1 short sentence, max 25 words)."
        };

        var requestBody = new {
            model = "llama3.1:8b-instruct-q4_K_M",
            prompt = prompt,
            stream = false,
            options = new { temperature = OllamaTemperature, num_predict = OllamaMaxTokens }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await httpClient.PostAsync("http://localhost:11434/api/generate", content);
        
        if (response.IsSuccessStatusCode) {
            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var message = doc.RootElement.GetProperty("response").GetString() ?? "Monitoring systems...";
            return message;
        }
    } catch {
        // Fallback messages if Ollama is unavailable
        var fallbacks = new[] {
            "Monitoring systems...",
            "All systems nominal.",
            "Standing by.",
            "Ready to assist."
        };
        return fallbacks[DateTime.Now.Minute % fallbacks.Length];
    }
    return "Ready.";
}

// Helper function to draw text on OLED
void DrawDisplay(string ip, int cpu, int ram, string message, int scrollOffset) {
    // Create a bitmap for the display
    using var image = BitmapImage.CreateBitmap(128, 64, PixelFormat.Format32bppArgb);
    image.Clear(Color.Black);
    
    var g = image.GetDrawingApi();
    
    // Font settings
    var font = "DejaVu Sans Bold";
    var fontSize = 8; // Small font for multiple lines
    
    // Line 1: LUNA: CPU and RAM
    g.DrawText($"LUNA: CPU: {cpu}% RAM: {ram}%", font, fontSize, Color.White, new Point(0, 0));
    
    // Line 2: IP address
    g.DrawText($"IP: {ip}", font, fontSize, Color.White, new Point(0, 10));
    
    // Line 3+: AI Message
    if (message.Length > 25) {
        var full = message + " " + message;
        var scrolled = full.Substring(scrollOffset % message.Length, Math.Min(25, full.Length - (scrollOffset % message.Length)));
        g.DrawText(scrolled, font, fontSize, Color.White, new Point(0, 20));
    } else {
        var words = message.Split(' ');
        var line = "";
        var y = 20;
        foreach (var word in words) {
            var testLine = line.Length == 0 ? word : line + " " + word;
            if (testLine.Length > MaxCharsPerLine && line.Length > 0) {
                g.DrawText(line, font, fontSize, Color.White, new Point(0, y));
                y += 8;
                line = word;
                if (y > MaxMessageY) break;
            } else {
                line = testLine;
            }
        }
        if (line.Length > 0 && y <= MaxMessageY) {
            g.DrawText(line, font, fontSize, Color.White, new Point(0, y));
        }
    }
    
    // Send the bitmap to the display
    oled.DrawBitmap(image);
}

// Refresh AI message content every 5 minutes (message type rotates every 15 minutes)
var lunaMessage = "Initializing...";
var lastMessageUpdate = DateTime.MinValue;
var scrollOffset = 0;

while (true) {
    var localIpAddr = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("100."))?.Address.ToString();
    var localIp = localIpAddr ?? "N/A";
    using var cpuProc = Process.Start(new ProcessStartInfo("bash", "-c \"top -bn1 | grep 'Cpu(s)' | awk '{print int($2)}'\"") { RedirectStandardOutput = true });
    var cpuOut = cpuProc?.StandardOutput.ReadToEnd().Trim() ?? "";
    var cpuUsage = int.TryParse(cpuOut, out var cpu) ? cpu : 0;
    using var ramProc = Process.Start(new ProcessStartInfo("bash", "-c \"free | grep Mem | awk '{print int($3/$2 * 100)}'\"") { RedirectStandardOutput = true });
    var ramOut = ramProc?.StandardOutput.ReadToEnd().Trim() ?? "";
    var ramUsage = int.TryParse(ramOut, out var ram) ? ram : 0;

    // Update LUNA message every 5 minutes
    if ((DateTime.Now - lastMessageUpdate).TotalMinutes >= 5) {
        lunaMessage = await GetLunaMessageAsync();
        lastMessageUpdate = DateTime.Now;
    }

    // Draw to OLED display
    DrawDisplay(localIp, cpuUsage, ramUsage, lunaMessage, scrollOffset);

    // Scroll if message is long
    if (lunaMessage.Length > 25) {
        scrollOffset = (scrollOffset + 6) % lunaMessage.Length;
    } else {
        scrollOffset = 0;
    }

    await Task.Delay(1000);
}