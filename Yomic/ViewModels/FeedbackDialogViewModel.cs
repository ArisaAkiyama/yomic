using System;
using System.Reactive;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ReactiveUI;

namespace Yomic.ViewModels
{
    public class FeedbackDialogViewModel : ViewModelBase
    {
        private string _feedbackText = string.Empty;
        public string FeedbackText
        {
            get => _feedbackText;
            set
            {
                this.RaiseAndSetIfChanged(ref _feedbackText, value);
                this.RaisePropertyChanged(nameof(CanSubmit));
            }
        }

        public bool CanSubmit => !string.IsNullOrWhiteSpace(FeedbackText);

        public ReactiveCommand<Avalonia.Controls.Window, Unit> CancelCommand { get; }
        public ReactiveCommand<Avalonia.Controls.Window, Unit> SubmitCommand { get; }

        private readonly Action<string, NotificationType>? _showNotificationCallback;

        public FeedbackDialogViewModel(Action<string, NotificationType>? showNotificationCallback = null)
        {
            _showNotificationCallback = showNotificationCallback;

            CancelCommand = ReactiveCommand.Create<Avalonia.Controls.Window>(window =>
            {
                window?.Close();
            });

            SubmitCommand = ReactiveCommand.CreateFromTask<Avalonia.Controls.Window>(async window =>
            {
                await SendEmailAsync(FeedbackText);
                window?.Close();
            }, this.WhenAnyValue(x => x.CanSubmit));
        }

        private async Task SendEmailAsync(string text)
        {
            try
            {
                // Google Apps Script Web App URL
                string url = "https://script.google.com/macros/s/AKfycby_qcMGyle2jYmxGmhf3j_L1VcYsyVcy8iBdxfnXzAf5zruVKttJ3FHe8w52XOUVNHi/exec";

                if (url == "YOUR_GOOGLE_APPS_SCRIPT_URL_HERE")
                {
                    _showNotificationCallback?.Invoke("Feedback URL is not configured.", NotificationType.Error);
                    return;
                }

                var payload = new
                {
                    message = text
                };

                string jsonPayload = JsonSerializer.Serialize(payload);

                // Disable auto-redirect to prevent HttpClient from converting POST to GET on a 302 redirect
                using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
                using (var client = new HttpClient(handler))
                {
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(url, content);

                    // Google Apps Script redirects with 302 Found to script.googleusercontent.com
                    if (response.StatusCode == System.Net.HttpStatusCode.Redirect || 
                        response.StatusCode == System.Net.HttpStatusCode.Found ||
                        response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
                    {
                        var redirectUrl = response.Headers.Location;
                        if (redirectUrl != null)
                        {
                            // Send a new POST request to the redirect destination to preserve POST body
                            using (var redirectClient = new HttpClient())
                            {
                                response = await redirectClient.PostAsync(redirectUrl, content);
                            }
                        }
                    }

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[Feedback] Google Apps Script sent successfully!");
                        _showNotificationCallback?.Invoke("Feedback sent successfully! Thank you.", NotificationType.Success);
                    }
                    else
                    {
                        string errorResponse = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[Feedback] Google Apps Script error: {response.StatusCode} - {errorResponse}");
                        _showNotificationCallback?.Invoke("Failed to send feedback. Please try again later.", NotificationType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Feedback] Error sending via Google Apps Script: {ex.Message}");
                _showNotificationCallback?.Invoke("An error occurred while sending feedback.", NotificationType.Error);
            }
        }
    }
}
