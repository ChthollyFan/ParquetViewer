using Microsoft.Win32;
using ParquetViewer.Exceptions;
using System;
using System.Net.Http;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ParquetViewer.Helpers
{
    public static class Constants
    {
        //本仓库是 mukunku/ParquetViewer 的衍生项目，用户指南与文档均以本仓库 Wiki 为准，避免跳转到上游项目
        public const string WikiURL = "https://github.com/ChthollyFan/ParquetViewer/wiki";
        public const string DuckDBSqlSyntaxURL = "https://duckdb.org/docs/stable/sql/query_syntax/select";
    }

    public static class User
    {
        public static bool IsAdministrator =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent())
                .IsInRole(WindowsBuiltInRole.Administrator);

        public static char NumberDecimalSeparator =
            Thread.CurrentThread.CurrentUICulture.NumberFormat.NumberDecimalSeparator.ToCharArray()[0];
    }

    public static class Env
    {
        #region Assembly Version
        private static SemanticVersion? _assemblyVersion = null;
        public static SemanticVersion AssemblyVersion => _assemblyVersion ??= ParseAssemblyVersion();

        private static SemanticVersion ParseAssemblyVersion()
        {
            var assemblyVersionString = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty;

            if (!SemanticVersion.TryParse(assemblyVersionString, out var assemblyVersion))
                //遥测移除后不再"记录后继续"：版本号解析失败直接抛出，交由顶层异常处理器提示
                throw new UnsupportedAssemblyVersionException(assemblyVersionString);

            return assemblyVersion;
        }
        #endregion

        #region Latest Release Version
        //"最新版本"指的是本应用的发布版本，因此只能查询本仓库的 releases，查询上游仓库会返回与本应用无关的旧版本号
        private const string RELEASES_API_URL = "https://api.github.com/repos/ChthollyFan/ParquetViewer/releases";
        //GitHub 接口偶尔不返回 html_url，这里按 tag 拼出发布页面地址兜底，保证"更新"按钮始终能跳转
        private const string RELEASE_PAGE_URL_FORMAT = "https://github.com/ChthollyFan/ParquetViewer/releases/tag/v{0}";
        private static SemanticVersion? _latestReleaseVersion = null;
        private static Uri? _releaseUri = null;
        private static DateTime? _latestReleaseLastCheckedOn = null;
        public static async Task<(SemanticVersion? Version, Uri? Url)> FetchLatestRelease()
        {
            //We cache the http response for 30 minutes (even if it failed) so we don't spam http requests
            if (_latestReleaseLastCheckedOn.HasValue && DateTime.UtcNow.Subtract(_latestReleaseLastCheckedOn.Value) < TimeSpan.FromMinutes(30))
            {
                return (_latestReleaseVersion, _releaseUri);
            }

            //Clear previously cached values
            _latestReleaseVersion = null;
            _releaseUri = null;

            try
            {

                _latestReleaseLastCheckedOn = DateTime.UtcNow;

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Add("User-Agent", "Parquet Viewer"); //GitHub requires a user agent
                using var response = await httpClient.GetAsync(RELEASES_API_URL);
                if (response.IsSuccessStatusCode)
                {
                    using var responseJson = JsonDocument.Parse(response.Content.ReadAsStream());
                    foreach (var release in responseJson.RootElement.EnumerateArray())
                    {
                        //CI 会为 PR 创建预发布 Release，它不代表用户可以升级到的正式版本，跳过以免误导用户
                        if (release.TryGetProperty("prerelease", out var prerelease)
                            && prerelease.ValueKind == JsonValueKind.True)
                        {
                            continue;
                        }

                        Uri? releaseUri = null;
                        if (release.TryGetProperty("html_url", out var releaseUrl)
                            && releaseUrl.GetString() is string url)
                        {
                            releaseUri = new Uri(url);
                        }

                        if (release.TryGetProperty("tag_name", out var releaseTag)
                            && releaseTag.GetString() is string tag
                            && SemanticVersion.TryParse(tag, out var semanticVersion))
                        {
                            //接口未返回 html_url 时用 tag 兜底，避免用户点击"更新"却没有可跳转的地址
                            _releaseUri = releaseUri ?? new Uri(string.Format(RELEASE_PAGE_URL_FORMAT, semanticVersion));
                            _latestReleaseVersion = semanticVersion;
                            return (semanticVersion, _releaseUri);
                        }

                        //该条目不是可解析的正式版本，继续检查下一条（原实现直接中断，会漏掉后面的正式版本）
                    }
                }
            }
            catch
            {
                //遥测移除后不再上报查询失败，静默降级为“获取不到最新版本”
            }

            return (null, null);
        }
        #endregion

        #region Windows Theme
        /// <summary>
        /// Gets a value indicating whether the current Windows theme for apps is set to dark mode.
        /// `null` is returned if the value cannot be determined.
        /// </summary>
        public static bool? AppsUseDarkTheme
        {
            get
            {
                try
                {
                    if (Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "AppsUseLightTheme", -1)
                        is int appsUseLightTheme)
                    {
                        return appsUseLightTheme switch
                        {
                            0 => true, //Dark
                            1 => false, //Light
                            _ => null //Unknown
                        };
                    }
                }
                catch { /*Swallow*/ }

                return null;
            }
        }
        /// <summary>
        /// Gets a value indicating whether the current Windows theme for the system is set to dark mode.
        /// `null` is returned if the value cannot be determined.
        /// </summary>
        public static bool? SystemUsesDarkTheme
        {
            get
            {
                try
                {
                    if (Registry.GetValue("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", "SystemUsesLightTheme", -1)
                        is int systemUsesLightTheme)
                    {
                        return systemUsesLightTheme switch
                        {
                            0 => true, //Dark
                            1 => false, //Light
                            _ => null //Unknown
                        };
                    }
                }
                catch { /*Swallow*/ }

                return null;
            }
        }
        #endregion
    }

    public enum DateFormat
    {
        Default = 0,
        ISO8601 = 2,
        //1, 3, 4, and 5 have been discontinued. Should not be reused!
        Custom = 6,
    }

    public enum FileType
    {
        CSV = 0,
        XLS,
        JSON,
        PARQUET,
        XLSX,
    }
}