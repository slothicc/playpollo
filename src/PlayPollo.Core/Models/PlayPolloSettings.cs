using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayPollo.Models
{
    public class PlayPolloSettings : ObservableObject
    {
        private ServerKind serverKind = ServerKind.Auto;
        private string host = "localhost";
        private int port = 47990;
        private string username = string.Empty;
        private string protectedPassword = string.Empty;
        private string installPath = string.Empty;
        private LaunchMode defaultLaunchMode = LaunchMode.Uri;
        private bool exportCovers = true;
        private bool autoSync = true;
        private bool endStreamWhenGameCloses = true;
        private int exitTimeoutSeconds = 5;
        private string pinnedCertThumbprint = string.Empty;

        public ServerKind ServerKind { get => serverKind; set => SetValue(ref serverKind, value); }

        public string Host { get => host; set => SetValue(ref host, value); }

        public int Port { get => port; set => SetValue(ref port, value); }

        public string Username { get => username; set => SetValue(ref username, value); }

        /// <summary>The password as stored on disk: DPAPI-protected for the current Windows user.</summary>
        public string ProtectedPassword { get => protectedPassword; set => SetValue(ref protectedPassword, value); }

        /// <summary>Folder that contains sunshine.exe. Empty means auto-detect.</summary>
        public string InstallPath { get => installPath; set => SetValue(ref installPath, value); }

        public LaunchMode DefaultLaunchMode { get => defaultLaunchMode; set => SetValue(ref defaultLaunchMode, value); }

        public bool ExportCovers { get => exportCovers; set => SetValue(ref exportCovers, value); }

        public bool AutoSync { get => autoSync; set => SetValue(ref autoSync, value); }

        /// <summary>When an exported game closes in Playnite, tell the server to end the stream of that game.</summary>
        public bool EndStreamWhenGameCloses { get => endStreamWhenGameCloses; set => SetValue(ref endStreamWhenGameCloses, value); }

        public int ExitTimeoutSeconds { get => exitTimeoutSeconds; set => SetValue(ref exitTimeoutSeconds, value); }

        /// <summary>SHA-1 thumbprint of the server's self-signed certificate, pinned by "Test connection".</summary>
        public string PinnedCertThumbprint { get => pinnedCertThumbprint; set => SetValue(ref pinnedCertThumbprint, value); }

        [DontSerialize]
        public string Password
        {
            get => SecretProtector.Unprotect(ProtectedPassword);
            set => ProtectedPassword = SecretProtector.Protect(value);
        }

        /// <summary>A copy with no event subscribers. MemberwiseClone would carry the original's PropertyChanged handlers along.</summary>
        public PlayPolloSettings Clone()
        {
            return new PlayPolloSettings
            {
                ServerKind = ServerKind,
                Host = Host,
                Port = Port,
                Username = Username,
                ProtectedPassword = ProtectedPassword,
                InstallPath = InstallPath,
                DefaultLaunchMode = DefaultLaunchMode,
                ExportCovers = ExportCovers,
                AutoSync = AutoSync,
                EndStreamWhenGameCloses = EndStreamWhenGameCloses,
                ExitTimeoutSeconds = ExitTimeoutSeconds,
                PinnedCertThumbprint = PinnedCertThumbprint
            };
        }
    }

    internal static class SecretProtector
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PlayPollo.v1");

        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                return string.Empty;
            }

            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(data);
        }

        public static string Unprotect(string protectedValue)
        {
            if (string.IsNullOrEmpty(protectedValue))
            {
                return string.Empty;
            }

            try
            {
                var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is FormatException)
            {
                // Settings copied from another user or machine can't be decrypted; make the user re-enter it.
                return string.Empty;
            }
        }
    }
}
