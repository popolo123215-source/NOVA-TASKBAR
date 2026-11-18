using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using Windows.UI.Notifications;

namespace Nova.WindowsClient.Services;

internal sealed class WindowsToastNotificationService : ITrainReminderNotifier
{
    private const string ToastGroup = "NOVA-Train";
    private const string NotificationGroup = "NOVA-Notification";

    public void Show(TrainDepartureReminder reminder)
    {
        try
        {
            ShowToast(
                "Скоро отправление!",
                reminder.Direction,
                $"Поезд: {reminder.DepartureTime:HH:mm} · № {reminder.TrainNumber} · " +
                    $"до отправления {reminder.LeadMinutes} {GetMinuteWord(reminder.LeadMinutes)}",
                reminder.Id[..16],
                ToastGroup);
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TrainReminderNotificationException(
                $"Windows не приняла уведомление о поезде {reminder.TrainNumber}: {ex.Message}",
                ex);
        }
    }

    public void ShowNotification(string title, string message, string id)
    {
        try
        {
            ShowToast(title, message, null, id, NotificationGroup);
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ToastNotificationDeliveryException(
                $"Windows не приняла уведомление «{title}»: {ex.Message}",
                ex);
        }
    }

    private static void ShowToast(string title, string message, string? detail, string id, string group)
    {
        var content = new ToastContentBuilder()
            .AddText(title)
            .AddText(message);
        if (detail is not null)
            content.AddText(detail);
        var notification = new ToastNotification(content.GetXml())
        {
            Tag = id.Length <= 64 ? id : id[..64],
            Group = group
        };
        ToastNotificationManager.CreateToastNotifier(WindowsToastRegistration.AppUserModelId).Show(notification);
    }

    private static string GetMinuteWord(int minutes)
    {
        var lastTwoDigits = minutes % 100;
        if (lastTwoDigits is >= 11 and <= 14)
            return "минут";
        return (minutes % 10) switch
        {
            1 => "минута",
            2 or 3 or 4 => "минуты",
            _ => "минут"
        };
    }
}
