using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;

namespace Nova.WindowsClient.Services;

internal sealed class WindowsToastNotificationService : ITrainReminderNotifier
{
    private const string ToastGroup = "NOVA-Train";

    public void Show(TrainDepartureReminder reminder)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Скоро отправление!")
                .AddText(reminder.Direction)
                .AddText(
                    $"Поезд: {reminder.DepartureTime:HH:mm} · № {reminder.TrainNumber} · " +
                    $"до отправления {reminder.LeadMinutes} {GetMinuteWord(reminder.LeadMinutes)}")
                .Show(toast =>
                {
                    toast.Tag = reminder.Id[..16];
                    toast.Group = ToastGroup;
                });
        }
        catch (Exception ex) when (
            ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            throw new TrainReminderNotificationException(
                $"Windows не приняла уведомление о поезде {reminder.TrainNumber}: {ex.Message}",
                ex);
        }
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
