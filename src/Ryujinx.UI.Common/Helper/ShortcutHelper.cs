using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using ShellLink;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.UI.Common.Helper
{
    public static class ShortcutHelper
    {
        private const string SteamDevToolsEndpoint = "http://127.0.0.1:8080/json";
        private const string ArmadaGameLauncherPath = "/usr/libexec/armada/armada-game-launch";
        private const string ArmadaLaunchOptions = "PATH=/usr/bin:/bin:/usr/local/bin:$PATH /usr/libexec/armada/armada-game-launch %command%";

        [SupportedOSPlatform("windows")]
        private static void CreateShortcutWindows(string applicationFilePath, string applicationId, byte[] iconData, string iconPath, string cleanedAppName, string desktopPath)
        {
            string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppDomain.CurrentDomain.FriendlyName + ".exe");
            iconPath += ".ico";

            MemoryStream iconDataStream = new(iconData);
            using SKBitmap image = SKBitmap.Decode(iconDataStream);
            image.Resize(new SKImageInfo(128, 128), SKFilterQuality.High);
            SaveBitmapAsIcon(image, iconPath);

            Shortcut shortcut = Shortcut.CreateShortcut(basePath, GetArgsString(applicationFilePath, applicationId), iconPath, 0);
            shortcut.StringData.NameString = cleanedAppName;
            shortcut.WriteToFile(Path.Combine(desktopPath, cleanedAppName + ".lnk"));
        }

        [SupportedOSPlatform("linux")]
        private static async Task CreateShortcutLinux(string applicationFilePath, string applicationName, string applicationId, byte[] iconData, string iconPath)
        {
            iconPath += ".png";

            SKBitmap image = SKBitmap.Decode(iconData);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream file = new(iconPath, FileMode.Create, FileAccess.Write, FileShare.None);
            data.SaveTo(file);
            file.Close();

            string executablePath = GetLinuxExecutablePath();
            string workingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            string arguments = GetArgsString(applicationFilePath, applicationId);
            string commandLine = $"\"{executablePath}\" {arguments}";
            string launchOptions = File.Exists(ArmadaGameLauncherPath)
                ? $"{ArmadaLaunchOptions} {arguments}"
                : arguments;

            using HttpClient httpClient = new()
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            using CancellationTokenSource cancellationTokenSource = new(TimeSpan.FromSeconds(15));
            using JsonDocument targets = JsonDocument.Parse(await httpClient.GetStringAsync(SteamDevToolsEndpoint, cancellationTokenSource.Token));

            string webSocketUrl = null;

            foreach (JsonElement target in targets.RootElement.EnumerateArray())
            {
                if (target.TryGetProperty("title", out JsonElement title) &&
                    title.GetString() == "SharedJSContext" &&
                    target.TryGetProperty("webSocketDebuggerUrl", out JsonElement url))
                {
                    webSocketUrl = url.GetString();
                    break;
                }
            }

            if (string.IsNullOrEmpty(webSocketUrl))
            {
                throw new InvalidOperationException("O Steam está aberto, mas a interface necessária para adicionar o atalho não está disponível.");
            }

            string Js(string value) => $"\"{JsonEncodedText.Encode(value)}\"";
            string expression =
                "(async () => {" +
                "const apps = SteamClient && SteamClient.Apps;" +
                "if (!apps || !apps.AddShortcut) throw new Error('Steam shortcut API unavailable');" +
                $"const appId = await apps.AddShortcut({Js(applicationName)}, {Js(executablePath)}, {Js(arguments)}, {Js(commandLine)});" +
                "if (!appId) throw new Error('Steam did not return a shortcut id');" +
                $"await apps.SetShortcutName(appId, {Js(applicationName)});" +
                $"await apps.SetShortcutStartDir(appId, {Js(workingDirectory)});" +
                $"await apps.SetShortcutLaunchOptions(appId, {Js(launchOptions)});" +
                $"await apps.SetShortcutIcon(appId, {Js(iconPath)});" +
                "await SteamClient.URL.ExecuteSteamURL('steam://nav/games/details/' + appId);" +
                "return appId;" +
                "})()";

            using ClientWebSocket webSocket = new();
            await webSocket.ConnectAsync(new Uri(webSocketUrl), cancellationTokenSource.Token);

            string requestJson =
                "{\"id\":1,\"method\":\"Runtime.evaluate\",\"params\":{" +
                $"\"expression\":{Js(expression)}," +
                "\"awaitPromise\":true,\"returnByValue\":true}}";
            byte[] request = Encoding.UTF8.GetBytes(requestJson);

            await webSocket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, cancellationTokenSource.Token);

            using MemoryStream responseStream = new();
            byte[] responseBuffer = new byte[4096];

            while (true)
            {
                WebSocketReceiveResult receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(responseBuffer), cancellationTokenSource.Token);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("O Steam encerrou a conexão antes de criar o atalho.");
                }

                responseStream.Write(responseBuffer, 0, receiveResult.Count);

                if (receiveResult.EndOfMessage)
                {
                    using JsonDocument response = JsonDocument.Parse(responseStream.ToArray());

                    if (response.RootElement.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 1)
                    {
                        if (response.RootElement.TryGetProperty("error", out JsonElement protocolError))
                        {
                            throw new InvalidOperationException(protocolError.ToString());
                        }

                        JsonElement evaluationResult = response.RootElement.GetProperty("result");

                        if (evaluationResult.TryGetProperty("exceptionDetails", out JsonElement exceptionDetails))
                        {
                            throw new InvalidOperationException(exceptionDetails.ToString());
                        }

                        break;
                    }

                    responseStream.SetLength(0);
                }
            }

            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }

        private static string GetLinuxExecutablePath()
        {
            // AppImage exports the stable path of the image in APPIMAGE. The
            // process itself runs from a temporary mount and must not be saved
            // in a Steam shortcut.
            string appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");

            if (!string.IsNullOrEmpty(appImagePath) && File.Exists(appImagePath))
            {
                return Path.GetFullPath(appImagePath);
            }

            if (!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            {
                return Path.GetFullPath(Environment.ProcessPath);
            }

            return Path.Combine(AppContext.BaseDirectory, "Ryujinx");
        }

        [SupportedOSPlatform("macos")]
        private static void CreateShortcutMacos(string appFilePath, string applicationId, byte[] iconData, string desktopPath, string cleanedAppName)
        {
            string basePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Ryujinx");
            string plistFile = EmbeddedResources.ReadAllText("Ryujinx.UI.Common/shortcut-template.plist");
            string shortcutScript = EmbeddedResources.ReadAllText("Ryujinx.UI.Common/shortcut-launch-script.sh");
            // Macos .App folder
            string contentFolderPath = Path.Combine("/Applications", cleanedAppName + ".app", "Contents");
            string scriptFolderPath = Path.Combine(contentFolderPath, "MacOS");

            if (!Directory.Exists(scriptFolderPath))
            {
                Directory.CreateDirectory(scriptFolderPath);
            }

            // Runner script
            const string ScriptName = "runner.sh";
            string scriptPath = Path.Combine(scriptFolderPath, ScriptName);
            using StreamWriter scriptFile = new(scriptPath);

            scriptFile.Write(shortcutScript, basePath, GetArgsString(appFilePath, applicationId));

            // Set execute permission
            FileInfo fileInfo = new(scriptPath);
            fileInfo.UnixFileMode |= UnixFileMode.UserExecute;

            // img
            string resourceFolderPath = Path.Combine(contentFolderPath, "Resources");
            if (!Directory.Exists(resourceFolderPath))
            {
                Directory.CreateDirectory(resourceFolderPath);
            }

            const string IconName = "icon.png";
            SKBitmap image = SKBitmap.Decode(iconData);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream file = File.OpenWrite(Path.Combine(resourceFolderPath, IconName));
            data.SaveTo(file);

            // plist file
            using StreamWriter outputFile = new(Path.Combine(contentFolderPath, "Info.plist"));
            outputFile.Write(plistFile, ScriptName, cleanedAppName, IconName);
        }

        public static async Task CreateAppShortcut(string applicationFilePath, string applicationName, string applicationId, byte[] iconData)
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string cleanedAppName = string.Join("_", applicationName.Split(Path.GetInvalidFileNameChars()));

            if (OperatingSystem.IsWindows())
            {
                string iconPath = Path.Combine(AppDataManager.BaseDirPath, "games", applicationId, "app");

                CreateShortcutWindows(applicationFilePath, applicationId, iconData, iconPath, cleanedAppName, desktopPath);

                return;
            }

            if (OperatingSystem.IsLinux())
            {
                string iconPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "icons", "Ryujinx");

                Directory.CreateDirectory(iconPath);
                await CreateShortcutLinux(applicationFilePath, applicationName, applicationId, iconData, Path.Combine(iconPath, applicationId));

                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                CreateShortcutMacos(applicationFilePath, applicationId, iconData, desktopPath, cleanedAppName);

                return;
            }

            throw new NotImplementedException("Shortcut support has not been implemented yet for this OS.");
        }

        private static string GetArgsString(string appFilePath, string applicationId)
        {
            // args are first defined as a list, for easier adjustments in the future
            List<string> argsList = new();

            if (!string.IsNullOrEmpty(CommandLineState.BaseDirPathArg))
            {
                argsList.Add("--root-data-dir");
                argsList.Add($"\"{CommandLineState.BaseDirPathArg}\"");
            }

            if (appFilePath.ToLower().EndsWith(".xci"))
            {
                argsList.Add("--application-id");
                argsList.Add($"\"{applicationId}\"");
            }

            argsList.Add($"\"{appFilePath}\"");

            return String.Join(" ", argsList);
        }

        /// <summary>
        /// Creates a Icon (.ico) file using the source bitmap image at the specified file path.
        /// </summary>
        /// <param name="source">The source bitmap image that will be saved as an .ico file</param>
        /// <param name="filePath">The location that the new .ico file will be saved too (Make sure to include '.ico' in the path).</param>
        [SupportedOSPlatform("windows")]
        private static void SaveBitmapAsIcon(SKBitmap source, string filePath)
        {
            // Code Modified From https://stackoverflow.com/a/11448060/368354 by Benlitz
            byte[] header = [0, 0, 1, 0, 1, 0, 0, 0, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0, 22, 0, 0, 0];
            using FileStream fs = new(filePath, FileMode.Create);

            fs.Write(header);
            // Writing actual data
            using var data = source.Encode(SKEncodedImageFormat.Png, 100);
            data.SaveTo(fs);
            // Getting data length (file length minus header)
            long dataLength = fs.Length - header.Length;
            // Write it in the correct place
            fs.Seek(14, SeekOrigin.Begin);
            fs.WriteByte((byte)dataLength);
            fs.WriteByte((byte)(dataLength >> 8));
            fs.WriteByte((byte)(dataLength >> 16));
            fs.WriteByte((byte)(dataLength >> 24));
        }
    }
}
