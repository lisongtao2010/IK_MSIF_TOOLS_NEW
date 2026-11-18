using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace MSIFAITOOLS.Services
{
    /// <summary>界面可编辑的 AI 接口设置。</summary>
    internal sealed class AiSettings
    {
        /// <summary>接口基础地址（不含 /chat/completions）。</summary>
        public string BaseUrl { get; set; }

        /// <summary>API 密钥（内存中为明文）。</summary>
        public string ApiKey { get; set; }

        /// <summary>模型名称。</summary>
        public string Model { get; set; }

        /// <summary>单次请求超时（秒）。</summary>
        public int TimeoutSeconds { get; set; }

        /// <summary>调用方式：<c>api</c>（云端 OpenAI 兼容接口）或 <c>claude-cli</c>（本机 Claude Code CLI）。</summary>
        public string Provider { get; set; }

        /// <summary>本机 Claude Code 的可执行文件路径（留空 = 自动查找）。</summary>
        public string ClaudeCliPath { get; set; }

        /// <summary>本机 Claude Code 的模型名（留空 = 本机默认模型）。</summary>
        public string ClaudeModel { get; set; }

        /// <summary>本机 Claude Code 的追加环境变量（KEY=VALUE，用 ; 分隔）。</summary>
        public string ClaudeCliEnvironment { get; set; }
    }

    /// <summary>
    /// AI 接口设置的用户级持久化（%APPDATA%\MSIFAITOOLS\ai-settings.json）。
    /// 密钥使用 DPAPI（当前用户）加密后写入；读取到明文（例如手工编辑）时按原样使用。
    /// </summary>
    internal static class AiSettingsStore
    {
        /// <summary>用户配置文件夹名。</summary>
        public const string FolderName = "MSIFAITOOLS";

        /// <summary>用户配置文件名。</summary>
        public const string FileName = "ai-settings.json";

        /// <summary>加密值前缀（用于与明文区分）。</summary>
        private const string EncryptedPrefix = "dpapi:";

        /// <summary>DPAPI 附加熵。</summary>
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MSIFAITOOLS.AiSettings.v1");

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        /// <summary>用户配置文件夹（%APPDATA%\MSIFAITOOLS）。</summary>
        public static string SettingsFolderPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
            }
        }

        /// <summary>用户配置文件完整路径。</summary>
        public static string SettingsFilePath
        {
            get { return Path.Combine(SettingsFolderPath, FileName); }
        }

        /// <summary>配置文件是否已存在。</summary>
        public static bool Exists
        {
            get { return File.Exists(SettingsFilePath); }
        }

        /// <summary>
        /// 读取用户配置。文件不存在时返回 null 且 error 为空字符串；解析失败时 error 为原因。
        /// </summary>
        public static AiSettings Load(out string error)
        {
            error = string.Empty;
            string path = SettingsFilePath;

            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                Dictionary<string, object> data = Serializer.Deserialize<Dictionary<string, object>>(
                    File.ReadAllText(path, Encoding.UTF8));

                if (data == null)
                {
                    return null;
                }

                return new AiSettings
                {
                    BaseUrl = GetText(data, "BaseUrl"),
                    ApiKey = Unprotect(GetText(data, "ApiKey")),
                    Model = GetText(data, "Model"),
                    TimeoutSeconds = GetInt(data, "TimeoutSeconds"),
                    Provider = GetText(data, "Provider"),
                    ClaudeCliPath = GetText(data, "ClaudeCliPath"),
                    ClaudeModel = GetText(data, "ClaudeModel"),
                    ClaudeCliEnvironment = GetText(data, "ClaudeCliEnvironment")
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>保存用户配置（文件夹不存在时自动创建）。失败时返回 false 并给出原因。</summary>
        public static bool Save(AiSettings settings, out string error)
        {
            error = string.Empty;

            if (settings == null)
            {
                error = "设置内容不能为空。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(SettingsFolderPath);

                Dictionary<string, object> data = new Dictionary<string, object>();
                data["BaseUrl"] = settings.BaseUrl ?? string.Empty;
                data["ApiKey"] = Protect(settings.ApiKey ?? string.Empty);
                data["Model"] = settings.Model ?? string.Empty;
                data["TimeoutSeconds"] = settings.TimeoutSeconds;
                data["Provider"] = settings.Provider ?? string.Empty;
                data["ClaudeCliPath"] = settings.ClaudeCliPath ?? string.Empty;
                data["ClaudeModel"] = settings.ClaudeModel ?? string.Empty;
                data["ClaudeCliEnvironment"] = settings.ClaudeCliEnvironment ?? string.Empty;

                File.WriteAllText(SettingsFilePath, Serializer.Serialize(data), new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string GetText(Dictionary<string, object> data, string key)
        {
            object value;

            if (!data.TryGetValue(key, out value) || value == null)
            {
                return string.Empty;
            }

            return Convert.ToString(value) ?? string.Empty;
        }

        private static int GetInt(Dictionary<string, object> data, string key)
        {
            object value;

            if (!data.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            int parsed;
            return int.TryParse(Convert.ToString(value), out parsed) ? parsed : 0;
        }

        private static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            try
            {
                byte[] cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy,
                    DataProtectionScope.CurrentUser);
                return EncryptedPrefix + Convert.ToBase64String(cipher);
            }
            catch (Exception)
            {
                // DPAPI 不可用时退化为明文保存（文件仍位于当前用户的配置目录下）
                return plainText;
            }
        }

        private static string Unprotect(string storedText)
        {
            if (string.IsNullOrEmpty(storedText))
            {
                return string.Empty;
            }

            if (!storedText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                return storedText;
            }

            try
            {
                byte[] cipher = Convert.FromBase64String(storedText.Substring(EncryptedPrefix.Length));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, Entropy,
                    DataProtectionScope.CurrentUser));
            }
            catch (Exception)
            {
                // 换了用户或机器时无法解密，视为未配置
                return string.Empty;
            }
        }
    }
}
