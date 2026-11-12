using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Infrastructure.Network;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Service.Model.MirrorChyan;
using Wpf.Ui.Violeta.Controls;

namespace BetterGenshinImpact.Service;

public class UpdateService : IUpdateService
{
    private readonly ILogger<UpdateService> _logger;
    private readonly IConfigService _configService;

    private const string NoticeUrl = "https://hui-config.oss-cn-hangzhou.aliyuncs.com/bgi/notice.json";
    private const string DownloadPageUrl = "https://www.bettergi.com/download.html";

    /// <summary>由 fork 发布构建写入的 GitHub 更新仓库。</summary>
    private static readonly string? ForkUpdateRepository = typeof(UpdateService).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "ForkUpdateRepository")?.Value;

    /// <summary>
    /// 命名 HttpClient 工厂，版本检查与更新日志使用默认客户端
    /// </summary>
    private readonly IHttpClientFactory _httpClientFactory;

    public AllConfig Config { get; set; }

    public UpdateService(IConfigService configService, IHttpClientFactory httpClientFactory)
    {
        _logger = App.GetLogger<UpdateService>();
        _configService = configService;
        _httpClientFactory = httpClientFactory;
        Config = _configService.Get();
    }


    /// <summary>
    /// Please call me in main thread
    /// </summary>
    /// <param name="option"></param>
    public async Task CheckUpdateAsync(UpdateOption option)
    {
        try
        {
#if DEBUG && false
            return;
#endif
            var forkRelease = ForkUpdateRepository is null ? null : await GetForkReleaseAsync(ForkUpdateRepository);
            string newVersion = ForkUpdateRepository is null
                ? await GetLatestVersionAsync(option)
                : forkRelease?.Value<string>("tag_name") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(newVersion))
            {
                return;
            }

            // ---- 如果是调试模式且手动的检查更新的情况下，强制打开更新窗口 -----
            // 方便调试窗口
            if (RuntimeHelper.IsDebuggerAttached && option.Trigger == UpdateTrigger.Manual)
            {
                await OpenCheckUpdateWindow(option, newVersion, forkRelease);
                return;
            }
            // ---- 如果是调试模式且手动的检查更新的情况下，强制打开更新窗口 -----

            if (!Global.IsNewVersion(newVersion))
            {
                if (option.Trigger == UpdateTrigger.Manual)
                {
                    await ThemedMessageBox.InformationAsync("当前已是最新版本！");
                }

                return;
            }

            if (!string.IsNullOrEmpty(Config.NotShowNewVersionNoticeEndVersion)
                && !Global.IsNewVersion(Config.NotShowNewVersionNoticeEndVersion, newVersion)
                && option.Trigger == UpdateTrigger.Auto)
            {
                return;
            }

            await OpenCheckUpdateWindow(option, newVersion, forkRelease);
        }
        catch (Exception e)
        {
            Debug.WriteLine("获取最新版本信息失败：" + e.Source + "\r\n--" + Environment.NewLine + e.StackTrace + "\r\n---" +
                            Environment.NewLine + e.Message);
            _logger.LogWarning("获取 BetterGI 最新版本信息失败");
        }
    }

    private async Task OpenCheckUpdateWindow(UpdateOption option, string newVersion, JObject? forkRelease)
    {
        CheckUpdateWindow win = new(option, ForkUpdateRepository)
        {
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = $"发现新版本 {newVersion}",
            UserInteraction = async (sender, button) =>
            {
                CheckUpdateWindow win = (CheckUpdateWindow)sender;

                switch (button)
                {
                    case CheckUpdateWindow.CheckUpdateWindowButton.BackgroundUpdate:
                        // TBD
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.OtherUpdate:
                        if (ForkUpdateRepository is not null)
                        {
                            var releaseUrl = $"https://github.com/{ForkUpdateRepository}/releases/tag/{Uri.EscapeDataString(newVersion)}";
                            Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                        }
                        else if (option.Channel == UpdateChannel.Stable)
                        {
                            Process.Start(new ProcessStartInfo(DownloadPageUrl) { UseShellExecute = true });
                        }
                        else
                        {
                            Process.Start(new ProcessStartInfo("https://cnb.cool/bettergi/better-genshin-impact/-/releases") { UseShellExecute = true });
                        }
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Update:
                    {
                        // 唤起更新程序
                        string updaterExePath = Global.Absolute("BetterGI.update.exe");
                        if (!File.Exists(updaterExePath))
                        {
                            await ThemedMessageBox.ErrorAsync("更新程序不存在，请选择其他更新方式！");
                            return;
                        }

                        // 启动
                        Process.Start(updaterExePath, ForkUpdateRepository is null ? "-I" : "-I --source github");

                        // 退出程序
                        Application.Current.Shutdown();
                    }
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Ignore:
                        Config.NotShowNewVersionNoticeEndVersion = newVersion;
                        win.Close();
                        break;

                    case CheckUpdateWindow.CheckUpdateWindowButton.Cancel:
                        win.ShowUpdateStatus = false;
                        win.Close();
                        break;
                }
            }
        };

        if (forkRelease is not null || option.Channel == UpdateChannel.Stable)
        {
            win.NavigateToHtml(await GetReleaseMarkdownHtmlAsync(forkRelease));
        }

        win.ShowDialog();
    }

    /// <summary>读取 fork 最新正式 Release，只有安装包已上传时才提示更新。</summary>
    private async Task<JObject?> GetForkReleaseAsync(string repository)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
        using var response = await httpClient.GetAsync($"https://api.github.com/repos/{repository}/releases/latest");
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var release = JObject.Parse(await response.Content.ReadAsStringAsync());
        var version = release.Value<string>("tag_name");
        var installerName = $"BetterGI.Install.{version}.exe";
        return !string.IsNullOrWhiteSpace(version) && release["assets"]?.Any(asset =>
            asset.Value<string>("name") == installerName && asset.Value<string>("state") == "uploaded") == true
            ? release
            : null;
    }

    private async Task<string> GetLatestVersionAsync(UpdateOption option)
    {
        if (option.Channel == UpdateChannel.Stable)
        {
            return await UpdateFromOss();
        }
        else
        {
            return await UpdateFromMirrorChyan();
        }
    }

    /// <summary>
    /// 文档
    /// https://apifox.com/apidoc/shared/ffdc8453-597d-4ba6-bd3c-5e375c10c789
    /// </summary>
    /// <returns></returns>
    private async Task<string> UpdateFromMirrorChyan()
    {
        try
        {
            const string url = "https://mirrorchyan.com/api/resources/BGI/latest";
            var queryParams = new Dictionary<string, string>
            {
                { "user_agent", "BetterGI" },
                { "os", "win" },
                { "arch", "x64" },
                { "channel", "alpha" }
            };

            var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);

            var finalUrl = $"{url}?{string.Join("&", queryParams.Select(x => $"{x.Key}={x.Value}"))}";
            var response = await httpClient.GetAsync(finalUrl);
            LatestResponse? result = null;
            if (response.StatusCode == HttpStatusCode.OK)
            {
                response.EnsureSuccessStatusCode();
                result = await response.Content.ReadFromJsonAsync<LatestResponse>();
            }
            else
            {
                // 即使是403、400也尝试读取响应体
                var content = await response.Content.ReadAsStringAsync();
                result = JsonConvert.DeserializeObject<LatestResponse>(content);
            }

            if (result != null)
            {
                if (result.Code == 0)
                {
                    return result.Data.VersionName;
                }
                else if (result.Code < 0)
                {
                    Toast.Error(
                        $"Mirror酱源更新检查失败，意料之外的严重错误，请及时联系 Mirror 酱的技术支持处理\n，错误代码：{result.Code}，错误信息：{result.Msg}");
                    return string.Empty;
                }
                else
                {
                    ToastError(result);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Mirror源更新检查失败");
            Toast.Warning($"Mirror源更新检查失败,{e.Message}");

        }

        return string.Empty;
    }

    private static void ToastError(LatestResponse response)
    {
        if (response.Code == 7001)
        {
            Toast.Warning("Mirror酱 CDK 已过期，请重新获取CDK");
        }
        else if (response.Code == 7002)
        {
            Toast.Warning("Mirror酱 CDK 错误!");
        }
        else if (response.Code == 7003)
        {
            Toast.Warning("Mirror酱 CDK 今日下载次数已达上限");
        }
        else if (response.Code == 7004)
        {
            Toast.Warning("Mirror酱 CDK 类型和待下载的资源不匹配");
        }
        else if (response.Code == 7005)
        {
            Toast.Warning("Mirror酱 CDK 已被封禁");
        }
        else
        {
            Toast.Warning($"Mirror酱源更新检查失败，错误信息：{response.Msg}");
        }
    }

    private async Task<string> UpdateFromOss()
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
            Notice? notice = await httpClient.GetFromJsonAsync<Notice>(NoticeUrl);
            string deviceId = DeviceIdHelper.DeviceId;

            if (notice != null)
            {
                // 灰度发布逻辑：deviceId 做哈希取余。string.GetHashCode 每个进程随机，
                // 改用 SHA256 前 4 字节，同一设备每次启动的灰度结果相同
                var hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)), 0);
                var mod = (int)(hash % 10);
                if (mod < notice.Gray)
                {
                    return notice.Version;
                }
            }
        }
        catch (Exception e)
        {
            _ = e;
        }

        return string.Empty;
    }

    private async Task<string> GetReleaseMarkdownHtmlAsync(JObject? forkRelease)
    {
        try
        {
            // GitHub API 要求带 UA，默认客户端会补上 BetterGI/{版本}
            var httpClient = _httpClientFactory.CreateClient(HttpClientNames.Default);
            var release = forkRelease ?? JObject.Parse(await httpClient.GetStringAsync(
                "https://api.github.com/repos/babalae/better-genshin-impact/releases/latest"));

            if (release.HasValues)
            {
                string? name = release.Value<string>("name");
                string? body = release.Value<string>("body");
                string md = $"# {name}{new string('\n', 2)}{body}";

                md = WebUtility.HtmlEncode(md);
                string md2html = File.ReadAllText(Global.Absolute(@"Assets\Strings\md2html.html"), Encoding.UTF8);
                var html = md2html.Replace("{{content}}", md);

                return html;
            }
        }
        catch (Exception e)
        {
            _ = e;
        }

        return GetReleaseMarkdownHtmlFallback();
    }

    private string GetReleaseMarkdownHtmlFallback()
    {
        return
            """
            <!DOCTYPE html>
            <html lang="zh">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>更新日志</title>
                <style>
                    body {
                        background-color: #212121;
                        color: white;
                        font-family: Arial, sans-serif;
                        display: flex;
                        justify-content: center;
                        align-items: center;
                        height: 100vh;
                        margin: 0;
                    }
                    .message {
                        text-align: center;
                        font-size: 20px;
                    }
                </style>
            </head>
            <body>
                <div class="message">
                    获取更新日志失败，请自行选择是否更新！
                </div>
            </body>
            </html>
            """;
    }
}
