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
                // EmailJS API Endpoint
                const string url = "https://api.emailjs.com/api/v1.0/email/send";

                // IMPORTANT: The user needs to provide their Public Key, Private Key, and Template ID.
                // Replace these with the actual values from the EmailJS dashboard (Account tab).
                const string serviceId = "service_6n6gvgp";
                const string templateId = "template_wc89jwa";
                const string publicKey = "pXJLrmTiytYnLjiVF";
                const string privateKey = "RKUs8L_QA9M7C8Anqe8sR";

                var payload = new
                {
                    service_id = serviceId,
                    template_id = templateId,
                    user_id = publicKey,
                    accessToken = privateKey,
                    template_params = new
                    {
                        message = text,
                        // Add other template parameters here if needed, like 'reply_to' or 'from_name'
                    }
                };

                string jsonPayload = JsonSerializer.Serialize(payload);

                using (var client = new HttpClient())
                {
                    var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(url, content);

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine("[Feedback] EmailJS sent successfully!");
                        _showNotificationCallback?.Invoke("Feedback sent successfully! Thank you.", NotificationType.Success);
                    }
                    else
                    {
                        string errorResponse = await response.Content.ReadAsStringAsync();
                        Console.WriteLine($"[Feedback] EmailJS error: {response.StatusCode} - {errorResponse}");
                        _showNotificationCallback?.Invoke("Failed to send feedback. Please try again later.", NotificationType.Error);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Feedback] Error sending via EmailJS: {ex.Message}");
                _showNotificationCallback?.Invoke("An error occurred while sending feedback.", NotificationType.Error);
            }
        }
    }
}
