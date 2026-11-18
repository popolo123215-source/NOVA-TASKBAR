using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Nova.WindowsClient.Services;
using Forms = System.Windows.Forms;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfButton = System.Windows.Controls.Button;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Nova.WindowsClient;

public partial class ShellWindow
{
    private readonly ObservableCollection<TaskRow> _productivityTaskRows = [];
    private readonly ObservableCollection<NoteRow> _productivityNoteRows = [];
    private readonly ObservableCollection<TimerRow> _productivityTimerRows = [];
    private readonly ObservableCollection<ReminderRow> _productivityReminderRows = [];
    private readonly ObservableCollection<CalendarRow> _productivityCalendarRows = [];
    private readonly ObservableCollection<MemoryRow> _productivityMemoryRows = [];
    private readonly ObservableCollection<ActionRow> _productivityActionRows = [];
    private readonly ObservableCollection<WorkflowRow> _productivityWorkflowRows = [];
    private readonly ObservableCollection<WorkflowStepRow> _productivityWorkflowDraftRows = [];
    private readonly HashSet<string> _timerCompletionSoundPlayed = new(StringComparer.Ordinal);
    private DispatcherTimer? _productivityAutoSaveTimer;
    private string? _editingProductivityTaskId;
    private string? _editingProductivityNoteId;
    private string? _editingProductivityReminderId;
    private string? _editingProductivityCalendarId;
    private string? _editingProductivityMemoryId;
    private string? _editingProductivityActionId;
    private string? _editingProductivityWorkflowId;
    private bool _loadingProductivityForm;
    private bool _productivityTaskDirty;
    private bool _productivityNoteDirty;
    private bool _productivityMemoryDirty;
    private string _productivityStatusOverride = string.Empty;

    private void InitializeProductivityUi(ProductivityService productivityService)
    {
        ProductivityTaskList.ItemsSource = _productivityTaskRows;
        ProductivityNoteList.ItemsSource = _productivityNoteRows;
        ProductivityTimerList.ItemsSource = _productivityTimerRows;
        ProductivityReminderList.ItemsSource = _productivityReminderRows;
        ProductivityCalendarList.ItemsSource = _productivityCalendarRows;
        ProductivityMemoryList.ItemsSource = _productivityMemoryRows;
        ProductivityActionList.ItemsSource = _productivityActionRows;
        ProductivityWorkflowList.ItemsSource = _productivityWorkflowRows;
        ProductivityWorkflowDraftList.ItemsSource = _productivityWorkflowDraftRows;
        ProductivityCalendarDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        ProductivityTaskPriorityCombo.SelectedIndex = 0;
        ProductivityTimerKindCombo.SelectedIndex = 0;
        ProductivityCalendarRecurrenceCombo.SelectedIndex = 0;
        ProductivityCalendarTimeBox.Text = "09:00";
        ProductivityCalendarLeadBox.Text = "10";
        ProductivityTimerMinutesBox.Text = "5";
        ProductivityMemoryKindCombo.SelectedIndex = 0;
        ProductivityActionKindCombo.SelectedIndex = 0;
        ProductivityReminderInputBox.Text = string.Empty;
        ProductivityCalendarTaskCombo.ItemsSource = Array.Empty<TaskLinkOption>();
        ProductivityWorkflowActionCombo.ItemsSource = _productivityActionRows;
        _productivityAutoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _productivityAutoSaveTimer.Tick += ProductivityAutoSaveTimer_Tick;
        productivityService.Changed += ProductivityService_Changed;
        if (productivityService.IsAvailable)
        {
            foreach (var timer in productivityService.GetTimerSnapshots(DateTimeOffset.UtcNow)
                         .Where(snapshot => snapshot.Timer.IsCompleted))
                _timerCompletionSoundPlayed.Add(timer.Timer.Id);
        }
        UpdateProductivityUi();
        UpdateProductivitySection("Tasks");
        UpdateActionTargetVisibility();
    }

    private void DisposeProductivityUi()
    {
        _productivityAutoSaveTimer?.Stop();
        _productivityAutoSaveTimer = null;
        if (_productivityTaskDirty)
            SaveProductivityTaskForm(showStatus: false);
        if (_productivityNoteDirty)
            SaveProductivityNoteForm(showStatus: false);
        if (_productivityMemoryDirty)
            SaveProductivityMemoryForm(showStatus: false);
        _productivityService.Changed -= ProductivityService_Changed;
    }

    private void ProductivityService_Changed(object? sender, EventArgs e)
    {
        if (_islandDisposed)
            return;
        Dispatcher.BeginInvoke(new Action(UpdateProductivityUi), DispatcherPriority.Background);
    }

    private void UpdateProductivityUi()
    {
        if (_islandDisposed)
            return;
        ProductivityStatusText.Text = _productivityService.LastError ??
            (_productivityStatusOverride.Length > 0
                ? _productivityStatusOverride
                : "Данные сохраняются только локально. История срабатываний находится также в центре уведомлений.");
        if (!_productivityService.IsAvailable)
        {
            ProductivityStatusText.Text = _productivityService.LastError ?? "Локальное хранилище недоступно.";
            return;
        }

        UpdateProductivityLists();
        RefreshProductivityTimers();
    }

    private void UpdateProductivityLists()
    {
        if (!_productivityService.IsAvailable)
            return;
        var taskFilter = ProductivityTaskSearchBox.Text.Trim();
        ReplaceRows(_productivityTaskRows, _productivityService.Tasks
            .Where(item => Contains(item.Title, item.Details, item.Category, taskFilter))
            .Select(item => new TaskRow(item)));

        var noteFilter = ProductivityNoteSearchBox.Text.Trim();
        ReplaceRows(_productivityNoteRows, _productivityService.Notes
            .Where(item => Contains(item.Title, item.Content, item.Category, noteFilter))
            .Select(item => new NoteRow(item)));

        var now = DateTimeOffset.UtcNow;
        ReplaceRows(_productivityTimerRows, _productivityService.GetTimerSnapshots(now)
            .Select(snapshot => new TimerRow(snapshot)));

        ReplaceRows(_productivityReminderRows, _productivityService.Reminders.Select(item => new ReminderRow(item)));
        ReplaceRows(_productivityCalendarRows, _productivityService.CalendarEvents.Select(item => new CalendarRow(item, _productivityService.Tasks)));

        ReplaceRows(_productivityMemoryRows, _productivityService.SearchContext(ProductivityMemorySearchBox.Text)
            .Select(item => new MemoryRow(item)));

        ReplaceRows(_productivityActionRows, _productivityService.QuickActions.Select(item => new ActionRow(item)));
        ReplaceRows(_productivityWorkflowRows, _productivityService.Workflows.Select(item =>
            new WorkflowRow(item, _productivityService.GetWorkflowActions(item.Id))));

        var selectedTaskId = ProductivityCalendarTaskCombo.SelectedValue as string;
        var linkOptions = new[] { new TaskLinkOption(null, "Без связанной задачи") }
            .Concat(_productivityService.Tasks.Where(task => !task.IsCompleted)
                .Select(task => new TaskLinkOption(task.Id, task.Title)))
            .ToArray();
        ProductivityCalendarTaskCombo.ItemsSource = linkOptions;
        ProductivityCalendarTaskCombo.SelectedValue = linkOptions.Any(option => option.Id == selectedTaskId)
            ? selectedTaskId
            : null;
        var selectedActionId = ProductivityWorkflowActionCombo.SelectedValue as string;
        ProductivityWorkflowActionCombo.ItemsSource = _productivityActionRows;
        ProductivityWorkflowActionCombo.SelectedValue = _productivityActionRows.Any(row => row.Id == selectedActionId)
            ? selectedActionId
            : null;
    }

    private void RefreshProductivityTimers()
    {
        if (!_productivityService.IsAvailable || _islandDisposed)
            return;
        ReplaceRows(_productivityTimerRows,
            _productivityService.GetTimerSnapshots(DateTimeOffset.UtcNow).Select(snapshot => new TimerRow(snapshot)));
        foreach (var snapshot in _productivityService.GetTimerSnapshots(DateTimeOffset.UtcNow)
                     .Where(item => item.Timer.IsCompleted))
        {
            if (_timerCompletionSoundPlayed.Add(snapshot.Timer.Id) && _timerCompletionSound)
                System.Media.SystemSounds.Exclamation.Play();
        }
        UpdateCompactActivity();
    }

    private static void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
            target.Add(row);
    }

    private static bool Contains(string title, string details, string category, string query) =>
        query.Length == 0 ||
        title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        details.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        category.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private void ProductivitySection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section })
            UpdateProductivitySection(section);
    }

    private void UpdateProductivitySection(string section)
    {
        ProductivityTasksSection.Visibility = section == "Tasks" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityNotesSection.Visibility = section == "Notes" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityTimersSection.Visibility = section == "Timers" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityRemindersSection.Visibility = section == "Reminders" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityCalendarSection.Visibility = section == "Calendar" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityMemorySection.Visibility = section == "Memory" ? Visibility.Visible : Visibility.Collapsed;
        ProductivityActionsSection.Visibility = section == "Actions" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "Tasks")
            ProductivityTaskTitleBox.Focus();
    }

    private void ProductivityTaskForm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingProductivityForm)
            return;
        _productivityTaskDirty = true;
        ScheduleProductivityAutoSave();
    }

    private void ProductivityTaskForm_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ProductivityTaskForm_Changed(sender, e);

    private void ProductivityTaskForm_DateChanged(object sender, SelectionChangedEventArgs e) =>
        ProductivityTaskForm_Changed(sender, e);

    private void ProductivityTaskSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateProductivityLists();

    private void ProductivityTaskSave_Click(object sender, RoutedEventArgs e) =>
        SaveProductivityTaskForm(showStatus: true);

    private bool SaveProductivityTaskForm(bool showStatus)
    {
        if (_loadingProductivityForm)
            return false;
        if (string.IsNullOrWhiteSpace(ProductivityTaskTitleBox.Text))
        {
            if (showStatus)
                SetProductivityStatus("Введите название задачи.");
            return false;
        }
        if (!TryReadOptionalLocalDateTime(
                ProductivityTaskDueDatePicker,
                ProductivityTaskDueTimeBox,
                out var dueAt,
                out var dateError))
        {
            if (showStatus)
                SetProductivityStatus(dateError);
            return false;
        }
        try
        {
            var priority = ReadSelectedEnum<ProductivityPriority>(ProductivityTaskPriorityCombo);
            var now = DateTimeOffset.UtcNow;
            ProductivityTask task;
            if (_editingProductivityTaskId is { } id)
            {
                var existing = _productivityService.Tasks.First(item => item.Id == id);
                task = _productivityService.UpdateTask(
                    id,
                    ProductivityTaskTitleBox.Text,
                    ProductivityTaskDetailsBox.Text,
                    ProductivityTaskCategoryBox.Text,
                    priority,
                    dueAt,
                    existing.IsCompleted,
                    ProductivityTaskPinnedCheckBox.IsChecked == true,
                    now);
            }
            else
            {
                task = _productivityService.CreateTask(
                    ProductivityTaskTitleBox.Text,
                    ProductivityTaskDetailsBox.Text,
                    ProductivityTaskCategoryBox.Text,
                    priority,
                    dueAt,
                    ProductivityTaskPinnedCheckBox.IsChecked == true,
                    now);
                _editingProductivityTaskId = task.Id;
            }
            _productivityTaskDirty = false;
            _productivityStatusOverride = showStatus
                ? "Задача сохранена локально."
                : string.Empty;
            if (showStatus)
                SetProductivityStatus(_productivityStatusOverride);
            UpdateProductivityLists();
            return true;
        }
        catch (Exception ex) when (IsProductivityInputError(ex))
        {
            if (showStatus)
                SetProductivityStatus(ex.Message);
            return false;
        }
    }

    private void ProductivityTaskClear_Click(object sender, RoutedEventArgs e)
    {
        _loadingProductivityForm = true;
        _editingProductivityTaskId = null;
        ProductivityTaskTitleBox.Clear();
        ProductivityTaskDetailsBox.Clear();
        ProductivityTaskCategoryBox.Clear();
        ProductivityTaskDueDatePicker.SelectedDate = null;
        ProductivityTaskDueTimeBox.Clear();
        ProductivityTaskPriorityCombo.SelectedIndex = 0;
        ProductivityTaskPinnedCheckBox.IsChecked = false;
        _loadingProductivityForm = false;
        _productivityTaskDirty = false;
        SetProductivityStatus("Форма задачи очищена. Несохранённые поля удалены.");
    }

    private void ProductivityTaskEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskRow row })
            return;
        _loadingProductivityForm = true;
        _editingProductivityTaskId = row.Task.Id;
        ProductivityTaskTitleBox.Text = row.Task.Title;
        ProductivityTaskDetailsBox.Text = row.Task.Details;
        ProductivityTaskCategoryBox.Text = row.Task.Category;
        ProductivityTaskPriorityCombo.SelectedIndex = row.Task.Priority switch
        {
            ProductivityPriority.Normal => 0,
            ProductivityPriority.High => 1,
            _ => 2
        };
        ProductivityTaskDueDatePicker.SelectedDate = row.Task.DueAt?.ToLocalTime().DateTime.Date;
        ProductivityTaskDueTimeBox.Text = row.Task.DueAt?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;
        ProductivityTaskPinnedCheckBox.IsChecked = row.Task.IsPinned;
        _loadingProductivityForm = false;
        _productivityTaskDirty = false;
        UpdateProductivitySection("Tasks");
        SetProductivityStatus("Редактирование задачи. Изменения сохраняются автоматически.");
    }

    private void ProductivityTaskToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskRow row })
            return;
        TryProductivity(() =>
        {
            _productivityService.SetTaskCompleted(row.Task.Id, !row.Task.IsCompleted, DateTimeOffset.UtcNow);
            SetProductivityStatus(row.Task.IsCompleted ? "Задача возвращена в активные." : "Задача отмечена выполненной.");
        });
    }

    private void ProductivityTaskDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TaskRow row } ||
            !ConfirmDelete($"Удалить задачу «{row.Task.Title}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteTask(row.Task.Id);
            if (_editingProductivityTaskId == row.Task.Id)
                ProductivityTaskClear_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Задача удалена; связь с календарём снята.");
        });
    }

    private void ProductivityNoteForm_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingProductivityForm)
            return;
        _productivityNoteDirty = true;
        ScheduleProductivityAutoSave();
    }

    private void ProductivityNoteSave_Click(object sender, RoutedEventArgs e) =>
        SaveProductivityNoteForm(showStatus: true);

    private bool SaveProductivityNoteForm(bool showStatus)
    {
        if (_loadingProductivityForm)
            return false;
        if (string.IsNullOrWhiteSpace(ProductivityNoteTitleBox.Text))
        {
            if (showStatus)
                SetProductivityStatus("Введите название заметки.");
            return false;
        }
        try
        {
            var now = DateTimeOffset.UtcNow;
            ProductivityNote note;
            if (_editingProductivityNoteId is { } id)
            {
                note = _productivityService.UpdateNote(
                    id,
                    ProductivityNoteTitleBox.Text,
                    ProductivityNoteContentBox.Text,
                    ProductivityNoteCategoryBox.Text,
                    ProductivityNotePinnedCheckBox.IsChecked == true,
                    now);
            }
            else
            {
                note = _productivityService.CreateNote(
                    ProductivityNoteTitleBox.Text,
                    ProductivityNoteContentBox.Text,
                    ProductivityNoteCategoryBox.Text,
                    ProductivityNotePinnedCheckBox.IsChecked == true,
                    now);
                _editingProductivityNoteId = note.Id;
            }
            _productivityNoteDirty = false;
            _productivityStatusOverride = showStatus ? "Заметка сохранена локально." : string.Empty;
            if (showStatus)
                SetProductivityStatus(_productivityStatusOverride);
            UpdateProductivityLists();
            return true;
        }
        catch (Exception ex) when (IsProductivityInputError(ex))
        {
            if (showStatus)
                SetProductivityStatus(ex.Message);
            return false;
        }
    }

    private void ProductivityNoteClear_Click(object sender, RoutedEventArgs e)
    {
        _loadingProductivityForm = true;
        _editingProductivityNoteId = null;
        ProductivityNoteTitleBox.Clear();
        ProductivityNoteContentBox.Clear();
        ProductivityNoteCategoryBox.Clear();
        ProductivityNotePinnedCheckBox.IsChecked = false;
        _loadingProductivityForm = false;
        _productivityNoteDirty = false;
        SetProductivityStatus("Форма заметки очищена.");
    }

    private void ProductivityNoteSearch_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateProductivityLists();

    private void ProductivityNoteEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NoteRow row })
            return;
        _loadingProductivityForm = true;
        _editingProductivityNoteId = row.Note.Id;
        ProductivityNoteTitleBox.Text = row.Note.Title;
        ProductivityNoteContentBox.Text = row.Note.Content;
        ProductivityNoteCategoryBox.Text = row.Note.Category;
        ProductivityNotePinnedCheckBox.IsChecked = row.Note.IsPinned;
        _loadingProductivityForm = false;
        _productivityNoteDirty = false;
        UpdateProductivitySection("Notes");
        SetProductivityStatus("Редактирование заметки. Изменения сохраняются автоматически.");
    }

    private void ProductivityNoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: NoteRow row } ||
            !ConfirmDelete($"Удалить заметку «{row.Note.Title}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteNote(row.Note.Id);
            if (_editingProductivityNoteId == row.Note.Id)
                ProductivityNoteClear_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Заметка удалена.");
        });
    }

    private void ProductivityAutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _productivityAutoSaveTimer?.Stop();
        if (_productivityTaskDirty)
            SaveProductivityTaskForm(showStatus: false);
        if (_productivityNoteDirty)
            SaveProductivityNoteForm(showStatus: false);
        if (_productivityMemoryDirty)
            SaveProductivityMemoryForm(showStatus: false);
    }

    private void ScheduleProductivityAutoSave()
    {
        if (_productivityAutoSaveTimer is null || _islandDisposed)
            return;
        _productivityAutoSaveTimer.Stop();
        _productivityAutoSaveTimer.Start();
    }

    private void ProductivityTimerKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductivityTimerMinutesBox is not null)
            ProductivityTimerMinutesBox.IsEnabled =
                ReadSelectedEnum<ProductivityTimerKind>(ProductivityTimerKindCombo) == ProductivityTimerKind.Countdown;
    }

    private void ProductivityTimerCreate_Click(object sender, RoutedEventArgs e)
    {
        TryProductivity(() =>
        {
            var kind = ReadSelectedEnum<ProductivityTimerKind>(ProductivityTimerKindCombo);
            var name = ProductivityTimerNameBox.Text.Trim();
            if (name.Length == 0)
                name = kind == ProductivityTimerKind.Stopwatch ? "Секундомер" : "Таймер";
            var duration = TimeSpan.Zero;
            if (kind == ProductivityTimerKind.Countdown)
            {
                if (!int.TryParse(ProductivityTimerMinutesBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ||
                    minutes is < 1 or > 10080)
                    throw new ArgumentOutOfRangeException(nameof(ProductivityTimerMinutesBox), "Введите целое число минут от 1 до 10080.");
                duration = TimeSpan.FromMinutes(minutes);
            }
            _productivityService.CreateTimer(name, kind, duration, DateTimeOffset.UtcNow);
            ProductivityTimerNameBox.Clear();
            SetProductivityStatus(kind == ProductivityTimerKind.Countdown
                ? "Таймер создан. Запустите его кнопкой на карточке."
                : "Секундомер создан и сохранён.");
        });
    }

    private void ProductivityTimerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimerRow row })
            return;
        TryProductivity(() =>
        {
            if (row.Timer.Timer.IsCompleted)
                throw new InvalidOperationException("Сначала сбросьте завершившийся таймер.");
            if (row.Timer.IsRunning)
                _productivityService.PauseTimer(row.Id, DateTimeOffset.UtcNow);
            else
                _productivityService.StartTimer(row.Id, DateTimeOffset.UtcNow);
            SetProductivityStatus(row.Timer.IsRunning ? "Таймер приостановлен." : "Таймер запущен.");
        });
    }

    private void ProductivityTimerReset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimerRow row })
            return;
        TryProductivity(() =>
        {
            _productivityService.ResetTimer(row.Id);
            SetProductivityStatus("Таймер сброшен и готов к запуску.");
        });
    }

    private void ProductivityTimerDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimerRow row } ||
            !ConfirmDelete($"Удалить таймер «{row.Name}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteTimer(row.Id);
            SetProductivityStatus("Таймер удалён.");
        });
    }

    private void ProductivityReminderInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (ProductivityReminderPreviewText is null)
            return;
        try
        {
            ProductivityReminderPreviewText.Text = SmartReminderParser.TryParse(
                    ProductivityReminderInputBox.Text,
                    DateTimeOffset.Now,
                    TimeZoneInfo.Local,
                    out var parsed,
                    out var error)
                ? $"{parsed!.Title} · {parsed.DueAt.ToLocalTime():dd.MM.yyyy HH:mm} · {RecurrenceText(parsed.Recurrence)}"
                : error;
        }
        catch (InvalidOperationException ex)
        {
            ProductivityReminderPreviewText.Text = ex.Message;
        }
    }

    private void ProductivityReminderSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!SmartReminderParser.TryParse(
                    ProductivityReminderInputBox.Text,
                    DateTimeOffset.Now,
                    TimeZoneInfo.Local,
                    out var parsed,
                    out var error))
            {
                SetProductivityStatus(error);
                return;
            }
            var now = DateTimeOffset.UtcNow;
            if (_editingProductivityReminderId is { } id)
                _productivityService.UpdateReminder(id, parsed!, string.Empty, now);
            else
                _productivityService.CreateReminder(parsed!, string.Empty, now);
            _editingProductivityReminderId = null;
            ProductivityReminderInputBox.Clear();
            SetProductivityStatus("Напоминание сохранено. Доставка выполняется через Windows Toast.");
        }
        catch (Exception ex) when (IsProductivityInputError(ex))
        {
            SetProductivityStatus(ex.Message);
        }
    }

    private void ProductivityReminderCancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editingProductivityReminderId = null;
        ProductivityReminderInputBox.Clear();
        SetProductivityStatus("Редактирование напоминания отменено.");
    }

    private void ProductivityReminderSnooze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReminderRow row })
            return;
        TryProductivity(() =>
        {
            _productivityService.SnoozeReminder(row.Id, TimeSpan.FromMinutes(10), DateTimeOffset.UtcNow);
            SetProductivityStatus("Напоминание перенесено на 10 минут от текущего времени.");
        });
    }

    private void ProductivityReminderEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReminderRow row })
            return;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(row.Reminder.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(row.Reminder.DueAt, zone);
        var repeat = row.Reminder.Recurrence switch
        {
            ProductivityRecurrence.Daily => "ежедневно ",
            ProductivityRecurrence.Weekly => "еженедельно ",
            ProductivityRecurrence.Monthly => "ежемесячно ",
            _ => string.Empty
        };
        _editingProductivityReminderId = row.Id;
        ProductivityReminderInputBox.Text =
            $"{repeat}{local:dd.MM.yyyy HH:mm} {row.Title}";
        UpdateProductivitySection("Reminders");
        SetProductivityStatus("Измените текст или время и нажмите «Запланировать»; для отмены нажмите кнопку отмены.");
    }

    private void ProductivityReminderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReminderRow row })
            return;
        TryProductivity(() =>
        {
            _productivityService.SetReminderEnabled(row.Id, !row.Reminder.IsEnabled, DateTimeOffset.UtcNow);
            SetProductivityStatus(row.Reminder.IsEnabled ? "Напоминание отключено." : "Напоминание включено.");
        });
    }

    private void ProductivityReminderDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ReminderRow row } ||
            !ConfirmDelete($"Удалить напоминание «{row.Title}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteReminder(row.Id);
            if (_editingProductivityReminderId == row.Id)
                ProductivityReminderCancelEdit_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Напоминание удалено.");
        });
    }

    private void ProductivityCalendarSave_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadRequiredLocalDateTime(
                ProductivityCalendarDatePicker,
                ProductivityCalendarTimeBox,
                out var startsAt,
                out var dateError))
        {
            SetProductivityStatus(dateError);
            return;
        }
        if (!int.TryParse(ProductivityCalendarLeadBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var leadMinutes) ||
            leadMinutes is < 0 or > 1440)
        {
            SetProductivityStatus("Введите время напоминания от 0 до 1440 минут.");
            return;
        }
        TryProductivity(() =>
        {
            var recurrence = ReadSelectedEnum<ProductivityRecurrence>(ProductivityCalendarRecurrenceCombo);
            var taskId = ProductivityCalendarTaskCombo.SelectedValue as string;
            var now = DateTimeOffset.UtcNow;
            if (_editingProductivityCalendarId is { } id)
            {
                _productivityService.UpdateCalendarEvent(
                    id,
                    ProductivityCalendarTitleBox.Text,
                    ProductivityCalendarDetailsBox.Text,
                    startsAt,
                    leadMinutes,
                    recurrence,
                    TimeZoneInfo.Local.Id,
                    taskId,
                    now);
            }
            else
            {
                _productivityService.CreateCalendarEvent(
                    ProductivityCalendarTitleBox.Text,
                    ProductivityCalendarDetailsBox.Text,
                    startsAt,
                    leadMinutes,
                    recurrence,
                    TimeZoneInfo.Local.Id,
                    taskId,
                    now);
            }
            ProductivityCalendarClear_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Событие календаря сохранено локально.");
        });
    }

    private void ProductivityCalendarClear_Click(object sender, RoutedEventArgs e)
    {
        _editingProductivityCalendarId = null;
        ProductivityCalendarTitleBox.Clear();
        ProductivityCalendarDetailsBox.Clear();
        ProductivityCalendarDatePicker.SelectedDate = DateTime.Today.AddDays(1);
        ProductivityCalendarTimeBox.Text = "09:00";
        ProductivityCalendarLeadBox.Text = "10";
        ProductivityCalendarRecurrenceCombo.SelectedIndex = 0;
        ProductivityCalendarTaskCombo.SelectedValue = null;
    }

    private void ProductivityCalendarEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CalendarRow row })
            return;
        var local = TimeZoneInfo.ConvertTime(
            row.CalendarEvent.StartsAt,
            TimeZoneInfo.FindSystemTimeZoneById(row.CalendarEvent.TimeZoneId));
        _editingProductivityCalendarId = row.Id;
        ProductivityCalendarTitleBox.Text = row.CalendarEvent.Title;
        ProductivityCalendarDetailsBox.Text = row.CalendarEvent.Details;
        ProductivityCalendarDatePicker.SelectedDate = local.Date;
        ProductivityCalendarTimeBox.Text = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        ProductivityCalendarLeadBox.Text = row.CalendarEvent.ReminderMinutes.ToString(CultureInfo.InvariantCulture);
        SelectComboByTag(ProductivityCalendarRecurrenceCombo, row.CalendarEvent.Recurrence);
        ProductivityCalendarTaskCombo.SelectedValue = row.CalendarEvent.RelatedTaskId;
        UpdateProductivitySection("Calendar");
        SetProductivityStatus("Измените событие и сохраните его. Редактирование повторения заменяет следующее вхождение.");
    }

    private void ProductivityCalendarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CalendarRow row })
            return;
        TryProductivity(() =>
        {
            _productivityService.SetCalendarEventEnabled(row.Id, !row.CalendarEvent.IsEnabled, DateTimeOffset.UtcNow);
            SetProductivityStatus(row.CalendarEvent.IsEnabled ? "Событие отключено." : "Событие включено.");
        });
    }

    private void ProductivityCalendarDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: CalendarRow row } ||
            !ConfirmDelete($"Удалить событие «{row.Title}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteCalendarEvent(row.Id);
            if (_editingProductivityCalendarId == row.Id)
                ProductivityCalendarClear_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Событие календаря удалено.");
        });
    }

    private void ProductivityMemoryForm_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingProductivityForm)
            return;
        _productivityMemoryDirty = true;
        ScheduleProductivityAutoSave();
    }

    private void ProductivityMemoryKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingProductivityForm)
            return;
        _productivityMemoryDirty = true;
        ScheduleProductivityAutoSave();
    }

    private void ProductivityMemorySave_Click(object sender, RoutedEventArgs e) =>
        SaveProductivityMemoryForm(showStatus: true);

    private bool SaveProductivityMemoryForm(bool showStatus)
    {
        if (_loadingProductivityForm || string.IsNullOrWhiteSpace(ProductivityMemoryTextBox.Text))
            return false;
        TryProductivity(() =>
        {
            var kind = ReadSelectedEnum<ProductivityMemoryKind>(ProductivityMemoryKindCombo);
            if (_editingProductivityMemoryId is { } id)
                _productivityService.UpdateMemory(id, kind, ProductivityMemoryTextBox.Text, DateTimeOffset.UtcNow);
            else
                _editingProductivityMemoryId = _productivityService.CreateMemory(
                    kind,
                    ProductivityMemoryTextBox.Text,
                    DateTimeOffset.UtcNow).Id;
            _productivityMemoryDirty = false;
            if (showStatus)
                SetProductivityStatus("Запись памяти сохранена только на этом устройстве.");
        });
        return !_productivityMemoryDirty;
    }

    private void ProductivityMemoryClear_Click(object sender, RoutedEventArgs e)
    {
        _loadingProductivityForm = true;
        _editingProductivityMemoryId = null;
        ProductivityMemoryTextBox.Clear();
        ProductivityMemoryKindCombo.SelectedIndex = 0;
        _loadingProductivityForm = false;
        _productivityMemoryDirty = false;
        SetProductivityStatus("Форма локальной памяти очищена.");
    }

    private void ProductivityMemorySearch_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateProductivityLists();

    private void ProductivityMemoryEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MemoryRow row })
            return;
        switch (row.Result.Source)
        {
            case "Заметка":
                if (row.Result.RelatedId is { } noteId &&
                    _productivityService.Notes.FirstOrDefault(item => item.Id == noteId) is { } note)
                    ProductivityNoteEdit_Click(new WpfButton { Tag = new NoteRow(note) }, new RoutedEventArgs());
                return;
            case "Выполненная задача":
            case "Незавершённая задача":
                if (row.Result.RelatedId is { } taskId &&
                    _productivityService.Tasks.FirstOrDefault(item => item.Id == taskId) is { } task)
                    ProductivityTaskEdit_Click(new WpfButton { Tag = new TaskRow(task) }, new RoutedEventArgs());
                return;
            default:
                var memory = _productivityService.Memories.FirstOrDefault(item => item.Id == row.Result.RelatedId);
                if (memory is null)
                    return;
                _loadingProductivityForm = true;
                _editingProductivityMemoryId = memory.Id;
                ProductivityMemoryTextBox.Text = memory.Text;
                SelectComboByTag(ProductivityMemoryKindCombo, memory.Kind);
                _loadingProductivityForm = false;
                _productivityMemoryDirty = false;
                UpdateProductivitySection("Memory");
                SetProductivityStatus("Измените запись и сохраните её. Автосохранение включено.");
                return;
        }
    }

    private void ProductivityMemoryDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MemoryRow row } ||
            row.Result.Source is "Заметка" or "Незавершённая задача" or "Выполненная задача" ||
            !ConfirmDelete($"Удалить локальную запись «{row.Result.Title}»?"))
            return;
        TryProductivity(() =>
        {
            if (row.Result.RelatedId is { } id)
                _productivityService.DeleteMemory(id);
            if (_editingProductivityMemoryId == row.Result.RelatedId)
                ProductivityMemoryClear_Click(this, new RoutedEventArgs());
            SetProductivityStatus("Запись локальной памяти удалена.");
        });
    }

    private void ProductivityActionKind_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateActionTargetVisibility();

    private void UpdateActionTargetVisibility()
    {
        if (ProductivityActionKindCombo is null)
            return;
        var kind = ReadSelectedEnum<ProductivityQuickActionKind>(ProductivityActionKindCombo);
        var needsTarget = kind is ProductivityQuickActionKind.OpenPath or ProductivityQuickActionKind.LaunchApplication;
        if (ProductivityActionTargetPanel is not null)
            ProductivityActionTargetPanel.Visibility = needsTarget ? Visibility.Visible : Visibility.Collapsed;
        if (ProductivityActionBrowseFolderButton is not null)
            ProductivityActionBrowseFolderButton.Visibility = kind == ProductivityQuickActionKind.OpenPath
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ProductivityActionBrowse_Click(object sender, RoutedEventArgs e)
    {
        var kind = ReadSelectedEnum<ProductivityQuickActionKind>(ProductivityActionKindCombo);
        var dialog = new WpfOpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = kind == ProductivityQuickActionKind.LaunchApplication
                ? "Приложения и ярлыки (*.exe;*.lnk)|*.exe;*.lnk"
                : "Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
            ProductivityActionTargetBox.Text = dialog.FileName;
    }

    private void ProductivityActionBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Выберите папку для быстрого открытия",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            ProductivityActionTargetBox.Text = dialog.SelectedPath;
    }

    private void ProductivityActionSave_Click(object sender, RoutedEventArgs e)
    {
        TryProductivity(() =>
        {
            var kind = ReadSelectedEnum<ProductivityQuickActionKind>(ProductivityActionKindCombo);
            if (_editingProductivityActionId is { } id)
                _productivityService.UpdateQuickAction(
                    id,
                    ProductivityActionNameBox.Text,
                    kind,
                    ProductivityActionTargetBox.Text,
                    DateTimeOffset.UtcNow);
            else
                _productivityService.CreateQuickAction(
                    ProductivityActionNameBox.Text,
                    kind,
                    ProductivityActionTargetBox.Text,
                    DateTimeOffset.UtcNow);
            ClearProductivityActionForm();
            SetProductivityStatus("Быстрое действие сохранено локально.");
        });
    }

    private void ProductivityActionEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ActionRow row })
            return;
        _editingProductivityActionId = row.Id;
        ProductivityActionNameBox.Text = row.Action.Name;
        ProductivityActionTargetBox.Text = row.Action.Target;
        SelectComboByTag(ProductivityActionKindCombo, row.Action.Kind);
        UpdateActionTargetVisibility();
        UpdateProductivitySection("Actions");
        SetProductivityStatus("Редактирование быстрого действия.");
    }

    private void ClearProductivityActionForm()
    {
        _editingProductivityActionId = null;
        ProductivityActionNameBox.Clear();
        ProductivityActionTargetBox.Clear();
        ProductivityActionKindCombo.SelectedIndex = 0;
        UpdateActionTargetVisibility();
    }

    private void ProductivityActionDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ActionRow row } ||
            !ConfirmDelete($"Удалить действие «{row.Name}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteQuickAction(row.Id);
            if (_editingProductivityActionId == row.Id)
                ClearProductivityActionForm();
            SetProductivityStatus("Быстрое действие удалено.");
        });
    }

    private async void ProductivityActionRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ActionRow row })
            return;
        var confirmed = ConfirmDangerousActions([row.Action]);
        if (ProductivityQuickActionRunner.RequiresConfirmation(row.Action) && !confirmed)
        {
            SetProductivityStatus("Запуск действия отменён.");
            return;
        }
        try
        {
            await Task.Run(() => ProductivityQuickActionRunner.Execute(row.Action, confirmed));
            SetProductivityStatus($"Действие «{row.Name}» выполнено.");
        }
        catch (Exception ex) when (IsQuickActionError(ex))
        {
            SetProductivityStatus($"Не удалось выполнить «{row.Name}»: {ex.Message}");
        }
    }

    private void ProductivityWorkflowAddStep_Click(object sender, RoutedEventArgs e)
    {
        if (ProductivityWorkflowActionCombo.SelectedValue is not string actionId)
        {
            SetProductivityStatus("Сначала создайте и выберите хотя бы одно быстрое действие.");
            return;
        }
        if (_productivityWorkflowDraftRows.Count >= 50)
        {
            SetProductivityStatus("В сценарии допускается не более 50 шагов.");
            return;
        }
        var action = _productivityService.QuickActions.FirstOrDefault(item => item.Id == actionId);
        if (action is null)
        {
            SetProductivityStatus("Выбранное действие больше не существует.");
            return;
        }
        if (_productivityWorkflowDraftRows.Any(item => item.ActionId == action.Id))
        {
            SetProductivityStatus("Это действие уже добавлено в сценарий.");
            return;
        }
        var containsSystemAction = _productivityWorkflowDraftRows
            .Select(item => item.ActionId)
            .Select(id => _productivityService.QuickActions.FirstOrDefault(saved => saved.Id == id))
            .Any(saved => saved?.Kind is ProductivityQuickActionKind.LockWorkstation or ProductivityQuickActionKind.Sleep);
        if (containsSystemAction)
        {
            SetProductivityStatus("После блокировки или сна нельзя добавлять другие действия.");
            return;
        }
        _productivityWorkflowDraftRows.Add(new WorkflowStepRow(action.Id, action.Name));
    }

    private void ProductivityWorkflowRemoveStep_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorkflowStepRow row })
            _productivityWorkflowDraftRows.Remove(row);
    }

    private void ProductivityWorkflowSave_Click(object sender, RoutedEventArgs e)
    {
        TryProductivity(() =>
        {
            var actionIds = _productivityWorkflowDraftRows.Select(item => item.ActionId).ToArray();
            if (_editingProductivityWorkflowId is { } id)
                _productivityService.UpdateWorkflow(
                    id,
                    ProductivityWorkflowNameBox.Text,
                    actionIds,
                    DateTimeOffset.UtcNow);
            else
                _productivityService.CreateWorkflow(
                    ProductivityWorkflowNameBox.Text,
                    actionIds,
                    DateTimeOffset.UtcNow);
            ClearProductivityWorkflowForm();
            SetProductivityStatus("Сценарий сохранён локально.");
        });
    }

    private void ProductivityWorkflowEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowRow row })
            return;
        _editingProductivityWorkflowId = row.Id;
        ProductivityWorkflowNameBox.Text = row.Name;
        _productivityWorkflowDraftRows.Clear();
        foreach (var action in _productivityService.GetWorkflowActions(row.Id))
            _productivityWorkflowDraftRows.Add(new WorkflowStepRow(action.Id, action.Name));
        UpdateProductivitySection("Actions");
        SetProductivityStatus("Редактирование сценария. Измените шаги и сохраните.");
    }

    private void ProductivityWorkflowClear_Click(object sender, RoutedEventArgs e) =>
        ClearProductivityWorkflowForm();

    private void ClearProductivityWorkflowForm()
    {
        _editingProductivityWorkflowId = null;
        ProductivityWorkflowNameBox.Clear();
        _productivityWorkflowDraftRows.Clear();
    }

    private void ProductivityWorkflowDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowRow row } ||
            !ConfirmDelete($"Удалить сценарий «{row.Name}»?"))
            return;
        TryProductivity(() =>
        {
            _productivityService.DeleteWorkflow(row.Id);
            if (_editingProductivityWorkflowId == row.Id)
                ClearProductivityWorkflowForm();
            SetProductivityStatus("Сценарий удалён.");
        });
    }

    private async void ProductivityWorkflowRun_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorkflowRow row })
            return;
        var actions = _productivityService.GetWorkflowActions(row.Id);
        var confirmed = ConfirmDangerousActions(actions);
        if (actions.Any(ProductivityQuickActionRunner.RequiresConfirmation) && !confirmed)
        {
            SetProductivityStatus("Запуск сценария отменён.");
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                foreach (var action in actions)
                    ProductivityQuickActionRunner.Execute(action, confirmed);
            });
            SetProductivityStatus($"Сценарий «{row.Name}» выполнен.");
        }
        catch (Exception ex) when (IsQuickActionError(ex))
        {
            SetProductivityStatus($"Сценарий «{row.Name}» остановлен: {ex.Message}");
        }
    }

    private bool ConfirmDangerousActions(IReadOnlyCollection<ProductivityQuickAction> actions)
    {
        var dangerous = actions.Where(ProductivityQuickActionRunner.RequiresConfirmation).ToArray();
        if (dangerous.Length == 0)
            return true;
        var list = string.Join(Environment.NewLine, dangerous.Select(action => $"• {action.Name}"));
        return System.Windows.MessageBox.Show(
            this,
            $"Будут запущены действия, требующие подтверждения:{Environment.NewLine}{list}{Environment.NewLine}{Environment.NewLine}Продолжить?",
            "Подтверждение действий NOVA",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private void TryProductivity(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (IsProductivityInputError(ex))
        {
            SetProductivityStatus(ex.Message);
        }
    }

    private static bool IsProductivityInputError(Exception exception) =>
        exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or KeyNotFoundException or JsonException or
            TimeZoneNotFoundException or InvalidTimeZoneException;

    private static bool IsQuickActionError(Exception exception) =>
        IsProductivityInputError(exception) ||
        exception is Win32Exception or COMException or PlatformNotSupportedException;

    private static bool TryReadOptionalLocalDateTime(
        DatePicker datePicker,
        WpfTextBox timeBox,
        out DateTimeOffset? result,
        out string error)
    {
        if (datePicker.SelectedDate is null)
        {
            if (string.IsNullOrWhiteSpace(timeBox.Text))
            {
                result = null;
                error = string.Empty;
                return true;
            }
            result = null;
            error = "Выберите дату срока или очистите поле времени.";
            return false;
        }
        if (!TryReadRequiredLocalDateTime(datePicker, timeBox, out var dueAt, out error))
        {
            result = null;
            return false;
        }
        result = dueAt;
        return true;
    }

    private static bool TryReadRequiredLocalDateTime(
        DatePicker datePicker,
        WpfTextBox timeBox,
        out DateTimeOffset result,
        out string error)
    {
        result = default;
        if (datePicker.SelectedDate is not { } selectedDate ||
            !TimeOnly.TryParseExact(
                timeBox.Text.Trim(),
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            error = "Выберите дату и укажите время в формате ЧЧ:ММ.";
            return false;
        }
        var local = DateTime.SpecifyKind(selectedDate.Date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        var zone = TimeZoneInfo.Local;
        if (zone.IsInvalidTime(local))
        {
            error = "Это локальное время недоступно из-за перехода часов. Выберите другое время.";
            return false;
        }
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        result = new DateTimeOffset(local, offset).ToUniversalTime();
        error = string.Empty;
        return true;
    }

    private static TEnum ReadSelectedEnum<TEnum>(WpfComboBox comboBox) where TEnum : struct, Enum
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<TEnum>(value, out var parsed) &&
            Enum.IsDefined(parsed))
            return parsed;
        throw new InvalidOperationException($"Не выбрано корректное значение для {typeof(TEnum).Name}.");
    }

    private static void SelectComboByTag<TEnum>(WpfComboBox comboBox, TEnum value) where TEnum : struct, Enum
    {
        var tag = value.ToString();
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
    }

    private static string RecurrenceText(ProductivityRecurrence recurrence) => recurrence switch
    {
        ProductivityRecurrence.Daily => "каждый день",
        ProductivityRecurrence.Weekly => "каждую неделю",
        ProductivityRecurrence.Monthly => "каждый месяц",
        _ => "один раз"
    };

    private static bool ConfirmDelete(string message) =>
        System.Windows.MessageBox.Show(message, "NOVA Productivity", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void SetProductivityStatus(string message)
    {
        _productivityStatusOverride = message;
        if (ProductivityStatusText is not null)
            ProductivityStatusText.Text = message;
    }

    private sealed record TaskLinkOption(string? Id, string Title);

    private sealed class TaskRow(ProductivityTask task)
    {
        public ProductivityTask Task { get; } = task;
        public string Id => Task.Id;
        public string Title => $"{(Task.IsPinned ? "◆ " : string.Empty)}{Task.Title}";
        public string Details => Task.Details;
        public string PriorityText => Task.Priority switch
        {
            ProductivityPriority.High => "Высокий приоритет",
            ProductivityPriority.Low => "Низкий приоритет",
            _ => "Обычный"
        };
        public string MetaText => string.Join(" · ", new[]
        {
            Task.Category.Length > 0 ? Task.Category : "Без категории",
            Task.DueAt is { } due ? $"Срок {due.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty,
            Task.DueAt is { } dueAt && dueAt < DateTimeOffset.UtcNow && !Task.IsCompleted
                ? Task.DueNotificationSent ? "Просрочена" : "Срок наступил"
                : string.Empty,
            Task.IsCompleted ? "Выполнена" : "В работе",
            $"Изменена {Task.UpdatedAt.ToLocalTime():dd.MM HH:mm}"
        }.Where(value => value.Length > 0));
        public string CompletionText => Task.IsCompleted ? "Вернуть в работу" : "Выполнено";
    }

    private sealed class NoteRow(ProductivityNote note)
    {
        public ProductivityNote Note { get; } = note;
        public string Id => Note.Id;
        public string Title => $"{(Note.IsPinned ? "◆ " : string.Empty)}{Note.Title}";
        public string Content => Note.Content;
        public string MetaText => $"{(Note.Category.Length == 0 ? "Без категории" : Note.Category)} · {Note.UpdatedAt.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    private sealed class TimerRow(ProductivityTimerSnapshot timer)
    {
        public ProductivityTimerSnapshot Timer { get; } = timer;
        public string Id => Timer.Timer.Id;
        public string Name => Timer.Timer.Name;
        public string StateText => Timer.Timer.IsCompleted
            ? "Завершён"
            : Timer.IsRunning ? "Идёт" : Timer.Elapsed > TimeSpan.Zero ? "Пауза" : "Готов";
        public string ToggleText => Timer.IsRunning ? "Пауза" : Timer.Timer.IsCompleted ? "Сбросьте" : "Запустить";
        public string Display
        {
            get
            {
                var value = Timer.Timer.Kind == ProductivityTimerKind.Countdown
                    ? TimeSpan.FromSeconds(Math.Ceiling(Timer.Remaining.TotalSeconds))
                    : Timer.Elapsed;
                return value.TotalHours >= 1
                    ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
                    : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
            }
        }
    }

    private sealed class ReminderRow(ProductivityReminder reminder)
    {
        public ProductivityReminder Reminder { get; } = reminder;
        public string Id => Reminder.Id;
        public string Title => Reminder.Title;
        public string Body => Reminder.Body;
        public string DateText
        {
            get
            {
                try
                {
                    var local = TimeZoneInfo.ConvertTime(
                        Reminder.DueAt,
                        TimeZoneInfo.FindSystemTimeZoneById(Reminder.TimeZoneId));
                    return $"{local:dd.MM.yyyy HH:mm} · {RecurrenceText(Reminder.Recurrence)}";
                }
                catch (TimeZoneNotFoundException)
                {
                    return $"{Reminder.DueAt:dd.MM.yyyy HH:mm} UTC · часовой пояс недоступен";
                }
            }
        }
        public string StatusText => Reminder.LastOutcome ??
            (Reminder.IsEnabled ? "Запланировано" : "Отключено");
        public string ToggleText => Reminder.IsEnabled ? "Отключить" : "Включить";
    }

    private sealed class CalendarRow(ProductivityCalendarEvent calendarEvent, IReadOnlyList<ProductivityTask> tasks)
    {
        public ProductivityCalendarEvent CalendarEvent { get; } = calendarEvent;
        public string Id => CalendarEvent.Id;
        public string Title => CalendarEvent.Title;
        public string DateText
        {
            get
            {
                try
                {
                    return TimeZoneInfo.ConvertTime(
                        CalendarEvent.StartsAt,
                        TimeZoneInfo.FindSystemTimeZoneById(CalendarEvent.TimeZoneId))
                        .ToString("dd.MM.yyyy HH:mm", CultureInfo.GetCultureInfo("ru-RU"));
                }
                catch (TimeZoneNotFoundException)
                {
                    return $"{CalendarEvent.StartsAt:dd.MM.yyyy HH:mm} UTC";
                }
            }
        }
        public string MetaText
        {
            get
            {
                var linkedTask = tasks.FirstOrDefault(task => task.Id == CalendarEvent.RelatedTaskId);
                return $"{CalendarEvent.Details} · {RecurrenceText(CalendarEvent.Recurrence)} · " +
                    $"напоминание за {CalendarEvent.ReminderMinutes} мин." +
                    (linkedTask is null ? string.Empty : $" · задача: {linkedTask.Title}");
            }
        }
        public string StatusText => CalendarEvent.IsEnabled ? "Активно" : "Отключено";
        public string ToggleText => CalendarEvent.IsEnabled ? "Отключить" : "Включить";
    }

    private sealed class MemoryRow(ProductivitySearchResult result)
    {
        public ProductivitySearchResult Result { get; } = result;
        public string Source => Result.Source;
        public string Title => Result.Title;
        public string Excerpt => Result.Excerpt.Length > 240 ? $"{Result.Excerpt[..240]}…" : Result.Excerpt;
        public bool CanDelete => Result.Source is not "Заметка" and
            not "Незавершённая задача" and not "Выполненная задача";
    }

    private sealed class ActionRow(ProductivityQuickAction action)
    {
        public ProductivityQuickAction Action { get; } = action;
        public string Id => Action.Id;
        public string Name => Action.Name;
        public string Description => Action.Kind switch
        {
            ProductivityQuickActionKind.OpenPath => $"Открыть · {Action.Target}",
            ProductivityQuickActionKind.LaunchApplication => $"Запустить · {Action.Target} · требуется подтверждение",
            ProductivityQuickActionKind.LockWorkstation => "Системное действие · блокировка · требуется подтверждение",
            ProductivityQuickActionKind.Sleep => "Системное действие · спящий режим · требуется подтверждение",
            _ => Action.Target
        };
    }

    private sealed class WorkflowRow(ProductivityWorkflow workflow, IReadOnlyList<ProductivityQuickAction> actions)
    {
        public ProductivityWorkflow Workflow { get; } = workflow;
        public string Id => Workflow.Id;
        public string Name => Workflow.Name;
        public string StepsText => string.Join(" → ", actions.Select(action => action.Name));
    }

    private sealed record WorkflowStepRow(string ActionId, string Name);
}
