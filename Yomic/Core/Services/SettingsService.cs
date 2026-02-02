using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Yomic.Core.Services
{
    public class SettingsService
    {
        private readonly string _settingsFilePath;

        public bool IsDarkMode { get; set; } = true;
        
        private bool _isOfflineMode = false;
        public bool IsOfflineMode 
        { 
            get => _isOfflineMode;
            set
            {
                if (_isOfflineMode != value)
                {
                    _isOfflineMode = value;
                    OfflineModeChanged?.Invoke(value);
                }
            }
        }
        
        public event Action<bool>? OfflineModeChanged;

        public bool SecureScreen { get; set; } = false;
        public bool UpdateOnStart { get; set; } = false;
        public bool CheckAppUpdateOnStart { get; set; } = true;
        public bool IsFirstRun { get; set; } = true;

        public SettingsService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appData, "Yomic");
            if (!Directory.Exists(appDir))
            {
                Directory.CreateDirectory(appDir);
            }
            _settingsFilePath = Path.Combine(appDir, "settings.json");
            
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var settings = JsonSerializer.Deserialize<SettingsModel>(json);
                    
                    if (settings != null)
                    {
                        IsDarkMode = settings.IsDarkMode;
                        IsOfflineMode = settings.IsOfflineMode;
                        SecureScreen = settings.SecureScreen;
                        UpdateOnStart = settings.UpdateOnStart;
                        CheckAppUpdateOnStart = settings.CheckAppUpdateOnStart;
                        IsFirstRun = settings.IsFirstRun;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
            }
        }

        public void Save()
        {
            try
            {
                var settings = new SettingsModel
                {
                    IsDarkMode = IsDarkMode,
                    IsOfflineMode = IsOfflineMode,
                    SecureScreen = SecureScreen,
                    UpdateOnStart = UpdateOnStart,
                    CheckAppUpdateOnStart = CheckAppUpdateOnStart,
                    IsFirstRun = IsFirstRun
                };

                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public void Reset()
        {
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    File.Delete(_settingsFilePath);
                }

                // Reset properties to default
                // Reset properties to default
                IsDarkMode = true;
                IsOfflineMode = false;
                SecureScreen = false;
                UpdateOnStart = false;
                CheckAppUpdateOnStart = true;
                IsFirstRun = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error resetting settings: {ex.Message}");
            }
        }

        // Helper class for serialization
        private class SettingsModel
        {
            public bool IsDarkMode { get; set; }
            public bool IsOfflineMode { get; set; }
            public bool SecureScreen { get; set; }
            public bool UpdateOnStart { get; set; }
            public bool CheckAppUpdateOnStart { get; set; }
            public bool IsFirstRun { get; set; }
        }
    }
}
