using System;
using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using Yomic.Core.Services;

namespace Yomic.Views.Helpers
{
    public class SecureImageLoader : AvaloniaObject
    {
        // Global static instance for Attached Property access (A bit hacky but common for Attached Props)
        // Ideally we should use a ServiceLocator or passed via Resource, but for simplicity:
        public static SecureImageService? Service { get; set; }

        public static readonly AttachedProperty<string?> UrlProperty =
            AvaloniaProperty.RegisterAttached<SecureImageLoader, Image, string?>("Url");

        public static string? GetUrl(Image element) => element.GetValue(UrlProperty);
        public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);

        public static readonly AttachedProperty<string?> RefererProperty =
            AvaloniaProperty.RegisterAttached<SecureImageLoader, Image, string?>("Referer");

        public static string? GetReferer(Image element) => element.GetValue(RefererProperty);
        public static void SetReferer(Image element, string? value) => element.SetValue(RefererProperty, value);

        static SecureImageLoader()
        {
            UrlProperty.Changed.Subscribe(OnUrlChanged);
        }

        private static void OnUrlChanged(AvaloniaPropertyChangedEventArgs<string?> args)
        {
            if (args.Sender is not Image image) return;
            
            var url = args.NewValue.Value;
            var referer = GetReferer(image);

            // Cancel previous load if strictly needed (complexity), but simplest is "Last Writer Wins"
            // For now, just set source to null or loading
            image.Source = null; // Clear old image immediately
            // Optional: Set a logical "Loading" state if we had a placeholder asset
            
            if (string.IsNullOrEmpty(url)) return;

            if (Service == null)
            {
                System.Diagnostics.Debug.WriteLine("[SecureImageLoader] Service not initialized!");
                return;
            }

            // Async Load
            // Using Dispatcher.UIThread to invoke async but not block
            _ = LoadImageAsync(image, url, referer);
        }

        private static async System.Threading.Tasks.Task LoadImageAsync(Image image, string url, string? referer)
        {
            try
            {
                // Verify we are still requesting this URL (Simple concurrency check)
                if (GetUrl(image) != url) return;

                var bitmap = await Service!.LoadImageAsync(url, referer);

                // Double check before setting
                Dispatcher.UIThread.Post(() =>
                {
                    if (GetUrl(image) == url)
                    {
                        if (bitmap != null)
                        {
                            image.Source = bitmap;
                        }
                        else
                        {
                            // Set Error Placeholder if needed
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SecureImageLoader] Error attached load: {ex.Message}");
            }
        }
    }
}
