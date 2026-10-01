using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayPollo.Models;
using PlayPollo.Server;

namespace PlayPollo.Settings
{
    public class PlayPolloSettingsViewModel : ObservableObject, ISettings
    {
        private readonly PlayPolloPlugin plugin;
        private PlayPolloSettings editingClone;
        private PlayPolloSettings settings;
        private string statusText = string.Empty;
        private bool busy;

        public PlayPolloSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public IEnumerable<ServerKind> ServerKinds { get; } = Enum.GetValues(typeof(ServerKind)).Cast<ServerKind>().ToList();

        public IEnumerable<LaunchMode> LaunchModes { get; } = Enum.GetValues(typeof(LaunchMode)).Cast<LaunchMode>().ToList();

        public string StatusText { get => statusText; set => SetValue(ref statusText, value); }

        public bool Busy
        {
            get => busy;
            set => SetValue(ref busy, value);
        }

        public RelayCommand BrowseCommand { get; }

        public RelayCommand AutoDetectCommand { get; }

        public RelayCommand TestConnectionCommand { get; }

        public RelayCommand ForgetCertificateCommand { get; }

        public PlayPolloSettingsViewModel(PlayPolloPlugin plugin, PlayPolloSettings saved)
        {
            this.plugin = plugin;
            Settings = saved;
            WatchEndpointChanges(Settings);

            BrowseCommand = new RelayCommand(Browse);
            AutoDetectCommand = new RelayCommand(AutoDetect);
            TestConnectionCommand = new RelayCommand(async () => await TestConnectionAsync(), () => !Busy);
            ForgetCertificateCommand = new RelayCommand(() =>
            {
                Settings.PinnedCertThumbprint = string.Empty;
                StatusText = "Certificate forgotten. Use Test connection to trust the server again.";
            });
        }

        // A pinned certificate belongs to one host:port, so changing either drops the pin.
        private void WatchEndpointChanges(PlayPolloSettings target)
        {
            target.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(PlayPolloSettings.Host) || e.PropertyName == nameof(PlayPolloSettings.Port))
                {
                    target.PinnedCertThumbprint = string.Empty;
                }
            };
        }

        private void Browse()
        {
            var folder = plugin.PlayniteApi.Dialogs.SelectFolder();
            if (string.IsNullOrEmpty(folder))
            {
                return;
            }

            Settings.InstallPath = folder;
            ApplyDetection(ServerDetector.DetectFromPaths(new[] { folder }), folder);
        }

        private void AutoDetect()
        {
            ApplyDetection(ServerDetector.Detect(null), null);
        }

        private void ApplyDetection(DetectedServer server, string searchedFolder)
        {
            if (server == null)
            {
                StatusText = searchedFolder == null
                    ? "No Sunshine or Apollo install found. Browse to the folder that contains sunshine.exe."
                    : $"sunshine.exe wasn't found in {searchedFolder}.";
                return;
            }

            Settings.InstallPath = server.InstallPath;
            Settings.Port = server.WebPort;
            StatusText = $"Found {(server.Kind == ServerKind.Auto ? "a server" : server.Kind.ToString())} in {server.InstallPath} (web UI port {server.WebPort}).";
        }

        private async Task TestConnectionAsync()
        {
            Busy = true;
            StatusText = "Connecting...";
            try
            {
                var snapshot = Settings.Clone();
                // The user pressing this button is the moment they decide to trust the certificate.
                var trustFirstUse = string.IsNullOrEmpty(snapshot.PinnedCertThumbprint);
                var info = await Task.Run(async () =>
                {
                    using (var client = await ServerClientFactory.CreateAsync(snapshot, trustFirstUse).ConfigureAwait(false))
                    {
                        return await client.GetInfoAsync().ConfigureAwait(false);
                    }
                });

                if (trustFirstUse && !string.IsNullOrEmpty(info.CertThumbprint))
                {
                    Settings.PinnedCertThumbprint = info.CertThumbprint;
                }

                var name = string.IsNullOrEmpty(info.HostName) ? string.Empty : $" \"{info.HostName}\"";
                StatusText = $"Connected to {info.Kind}{name}: {info.AppCount} apps.";
            }
            catch (ServerException ex)
            {
                StatusText = ex.Message;
            }
            finally
            {
                Busy = false;
            }
        }

        public void BeginEdit()
        {
            editingClone = Settings.Clone();
        }

        public void CancelEdit()
        {
            Settings = editingClone;
            WatchEndpointChanges(Settings);
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            if (string.IsNullOrWhiteSpace(Settings.Host))
            {
                errors.Add("Enter the streaming server's host name or IP address.");
            }

            if (Settings.Port < 1 || Settings.Port > 65535)
            {
                errors.Add("The port must be between 1 and 65535.");
            }

            if (Settings.ExitTimeoutSeconds < 0)
            {
                errors.Add("The exit timeout can't be negative.");
            }

            return errors.Count == 0;
        }
    }
}
