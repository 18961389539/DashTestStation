using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DustLoadScada;

public partial class MainWindow : Window
{
    private readonly StationView view = new();
    private readonly string dataFolder;
    private readonly FrameworkElement[] pages;
    private readonly Mutex appMutex;
    private StationStore store = null!;
    private Dictionary<MonitorPointId, MonitorPoint> monitorPoints = new();
    private Dictionary<ManualPointId, ManualPoint> manualPoints = new();
    private Dictionary<RecipePointId, ushort> recipePoints = new();
    private Dictionary<string, InspectionPoint> inspectionPoints = new();
    private Button? heldManualButton;
    private Key heldManualKey;
    private Task manualPress = Task.CompletedTask;
    private Task manualRelease = Task.CompletedTask;
    private bool manualReleasePending;
    private CancellationTokenSource? pollCancellation;
    private Task polling = Task.CompletedTask;
    private Task recipeDispatch = Task.CompletedTask;
    private uint lastCompletedCycle;
    private bool selectingRecipe;
    private bool closing;
    private bool allowClose;
    private bool ownsMutex;
    private string? storageError;
    private int currentPage;

    public MainWindow()
    {
        InitializeComponent();
        pages = [MonitorPage, ManualPage, RecipePage, HistoryPage, ContactPage, AccessPage, DevicesPage];
        string? overrideFolder = Environment.GetEnvironmentVariable("DUSTLOADSCADA_DATA_DIR");
        dataFolder = string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DustLoadScada")
            : Path.GetFullPath(overrideFolder);
        var folderHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(dataFolder.ToUpperInvariant())));
        appMutex = new Mutex(false, $"Local\\DustLoadScada-{folderHash}");
        Loaded += Start;
        Closing += CloseStation;
        Deactivated += (_, _) => ReleaseHeldManual();
        Closed += (_, _) =>
        {
            if (ownsMutex)
                appMutex.ReleaseMutex();
            appMutex.Dispose();
        };
    }

    private async void Start(object sender, RoutedEventArgs e)
    {
        Loaded -= Start;
        try
        {
            try { ownsMutex = appMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex)
                throw new InvalidOperationException("此数据目录已有一个测试台窗口在运行。");

            store = new StationStore(dataFolder);
            monitorPoints = store.LoadMonitorPoints();
            manualPoints = store.LoadManualPoints();
            recipePoints = store.LoadRecipePoints();
            inspectionPoints = store.LoadInspectionPoints();
            view.SetManualPoints(manualPoints);
            view.NeedsAccount = store.Users().Count == 0;
            view.ActiveRecipe = store.ActiveRecipe() ?? store.Recipes()[0];
            await view.Station.ApplyAsync(view.ActiveRecipe);
            DataContext = view;
            DevicesPage.Initialize(store, dataFolder, settings => SwitchStationAsync(settings), () => SwitchStationAsync(null));
            LoadRecipes(view.ActiveRecipe.Id);
            view.Use(view.Station);
            StartPolling();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法启动灰尘负载测试台", MessageBoxButton.OK, MessageBoxImage.Error);
            allowClose = true;
            Close();
        }
    }

    private async void CloseStation(object? sender, CancelEventArgs e)
    {
        if (allowClose)
            return;
        e.Cancel = true;
        if (!view.NotBusy || DevicesPage.IsBusy || closing)
            return;
        closing = true;
        ReleaseHeldManual();
        await recipeDispatch;
        try { await FlushManualReleaseAsync(); }
        catch (Exception ex)
        {
            closing = false;
            view.Notice = $"手动命令未确认清零。{ex.Message}";
            return;
        }
        await StopPollingAsync();
        CloseStationConnection();
        await Task.WhenAll(StationDevices.Camera.DisconnectAsync(), StationDevices.Scanner.DisconnectAsync());
        allowClose = true;
        Close();
    }

    private void StartPolling()
    {
        pollCancellation = new CancellationTokenSource();
        polling = Task.WhenAll(PollAsync(view.Station, pollCancellation.Token),
            PollManualAsync(view.Station, pollCancellation.Token));
    }

    private async Task StopPollingAsync()
    {
        if (pollCancellation is null)
            return;
        pollCancellation.Cancel();
        await polling;
        pollCancellation.Dispose();
        pollCancellation = null;
    }

    private async Task PollAsync(IStation station, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                StationReading? reading = null;
                try { reading = await station.ReadAsync(cancellation); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    if (!cancellation.IsCancellationRequested)
                        view.Offline(ex.Message);
                }

                if (reading is { } sample && !cancellation.IsCancellationRequested)
                {
                    view.Update(sample.Test, sample.Feedback, sample.UnknownFeedback);
                    if (sample.Test.Running || sample.Test.Stage == TestStage.Fault)
                        ReleaseHeldManual();
                    if (sample.Test.Stage == TestStage.Completed)
                    {
                        try { Archive(station, sample.Test); }
                        catch (Exception ex)
                        {
                            storageError = $"测试结果存档失败，将重试。{ex.Message}";
                            view.Notice = storageError;
                        }
                    }
                }

                await Task.Delay(reading.HasValue ? station.PollMilliseconds : 1000, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task PollManualAsync(IStation station, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (currentPage == 1 || manualReleasePending)
                {
                    try
                    {
                        if (manualReleasePending && manualRelease.IsCompleted)
                        {
                            await station.ReleaseManualAsync();
                            manualReleasePending = false;
                        }
                        ushort feedback = await station.ReadManualAsync(cancellation);
                        if (cancellation.IsCancellationRequested)
                            break;
                        if (currentPage == 1)
                            view.UpdateManual(feedback);
                        view.ManualError = "";
                    }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
                    catch (Exception ex)
                    {
                        if (cancellation.IsCancellationRequested)
                            break;
                        view.UpdateManual(null);
                        ReleaseHeldManual();
                        view.ManualError = $"手动反馈读取失败。{ex.Message}";
                    }
                }
                await Task.Delay(station.PollMilliseconds, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void Archive(IStation station, TestSnapshot test)
    {
        if (test.CycleId == lastCompletedCycle)
            return;
        bool saved = store.Record(station.Source, test);
        lastCompletedCycle = test.CycleId;
        storageError = null;
        if (!saved)
            return;
        view.Notice = $"{(view.IsSimulation ? "模拟" : "PLC")}测试结果已保存。";
        if (currentPage == 3)
            TryRefreshHistory();
    }

    private async Task PerformAsync(Func<Task> action)
    {
        if (!view.NotBusy || closing || DataContext is null)
            return;
        ReleaseHeldManual();
        view.SetBusy(true);
        try { await action(); }
        catch (Exception ex)
        {
            view.Notice = ex.Message;
            MessageBox.Show(this, ex.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { view.SetBusy(false); }
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string parameter } || !int.TryParse(parameter, out int index))
            return;
        Navigate(index);
    }

    private void Navigate(int index)
    {
        currentPage = index;
        if (index != 1)
        {
            ReleaseHeldManual();
            view.UpdateManual(null);
        }
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in Navigation.Children.OfType<Button>())
            button.Tag = (string)button.CommandParameter == index.ToString(CultureInfo.InvariantCulture) ? "active" : null;
        if (index == 3)
            TryRefreshHistory();
    }

    private void Connection_Click(object sender, RoutedEventArgs e) => Navigate(6);

    private async Task SwitchStationAsync(PlcSettings? settings)
    {
        if (!view.CanSwitchMode || closing)
            throw new InvalidOperationException("请等待当前操作或测试结束。");
        if (settings is null && view.IsSimulation)
            return;

        view.SetBusy(true);
        try
        {
            await FlushManualReleaseAsync();
            await StopPollingAsync();
            try
            {
                if (settings is not null)
                {
                    settings.Validate();
                    StationDevices.Plc.Configure(settings, monitorPoints, manualPoints, recipePoints, inspectionPoints);
                }
            }
            catch
            {
                StartPolling();
                throw;
            }

            CloseStationConnection();
            IStation next = settings is null ? new SimulationStation() : StationDevices.Plc;
            if (next is SimulationStation)
                await next.ApplyAsync(view.ActiveRecipe);
            lastCompletedCycle = 0;
            storageError = null;
            view.Use(next);
            view.Notice = view.IsSimulation ? "模拟模式已就绪。" : "正在连接 PLC。";
            StartPolling();
        }
        finally { view.SetBusy(false); }
    }

    private void CloseStationConnection()
    {
        if (view.Station is ModbusStation plc)
            plc.CloseConnection();
        else
            view.Station.Dispose();
    }

    private async void EditManualPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || closing || heldManualButton is not null
            || sender is not Button { CommandParameter: ManualAction action })
            return;

        await PerformAsync(async () =>
        {
            await FlushManualReleaseAsync();
            var dialog = new ManualPointDialog(action) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Point is not { } point || point == action.Point)
                return;
            if (view.Snapshot.Running)
                throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改点位。");

            var updated = new Dictionary<ManualPointId, ManualPoint>(manualPoints) { [action.Id] = point };
            if (view.Station is ModbusStation station)
                await station.UpdateManualPointsAsync(updated, () => store.SaveManualPoints(updated));
            else
            {
                string configPath = Path.Combine(dataFolder, "plc.json");
                var settings = File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings();
                _ = InspectionPoints.Validate(settings, inspectionPoints, updated);
                store.SaveManualPoints(updated);
            }
            manualPoints = updated;
            view.UpdateManual(null);
            view.SetManualPoints(updated);
            view.Notice = $"{action.Label}地址已保存。";
        });
    }

    private void Manual_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Button button && CanStartManual(button))
        {
            button.Focus();
            if (button.CaptureMouse())
                BeginManual(button, Key.None);
        }
    }

    private void Manual_MouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender == heldManualButton && heldManualKey == Key.None)
            ReleaseHeldManual();
    }

    private void Manual_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (sender == heldManualButton && heldManualKey == Key.None)
            ReleaseHeldManual();
    }

    private void Manual_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        e.Handled = true;
        if (!e.IsRepeat && sender is Button button && CanStartManual(button))
            BeginManual(button, e.Key);
    }

    private void Manual_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
            return;
        e.Handled = true;
        if (sender == heldManualButton && heldManualKey == e.Key)
            ReleaseHeldManual();
    }

    private void Manual_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender == heldManualButton && heldManualKey != Key.None)
            ReleaseHeldManual();
    }

    private bool CanStartManual(Button button) => !closing && currentPage == 1 && view.CanUseManual
        && button.CommandParameter is ManualPointId && heldManualButton is null
        && manualRelease.IsCompleted && !manualReleasePending;

    private async void BeginManual(Button button, Key key)
    {
        var station = view.Station;
        var id = (ManualPointId)button.CommandParameter;
        heldManualButton = button;
        heldManualKey = key;
        try
        {
            manualPress = station.SetManualAsync(id, true);
            await manualPress;
        }
        catch (Exception ex)
        {
            view.Notice = $"手动命令写入失败。{ex.Message}";
            if (heldManualButton == button)
                ReleaseHeldManual();
        }
    }

    private void ReleaseHeldManual()
    {
        if (heldManualButton is not { } button)
            return;
        heldManualButton = null;
        if (button.IsMouseCaptured)
            button.ReleaseMouseCapture();
        manualRelease = ReleaseManualButtonAsync(view.Station, (ManualPointId)button.CommandParameter, manualPress);
    }

    private async Task ReleaseManualButtonAsync(IStation station, ManualPointId id, Task press)
    {
        try { await press; }
        catch { /* A failed response does not prove the PLC missed the write. */ }
        try { await station.SetManualAsync(id, false); }
        catch (Exception ex)
        {
            manualReleasePending = true;
            view.UpdateManual(null);
            view.Notice = $"手动命令未确认清零，通讯恢复后重试。{ex.Message}";
        }
    }

    private async Task FlushManualReleaseAsync()
    {
        ReleaseHeldManual();
        await manualRelease;
        await view.Station.ReleaseManualAsync();
        manualReleasePending = false;
    }

    private async void EditMonitorPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || closing
            || sender is not Button { CommandParameter: MonitorPointId id })
            return;

        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            string configPath = Path.Combine(dataFolder, "plc.json");
            var settings = station?.Settings ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
            var definition = MonitorPointDefinition.For(id);
            var current = monitorPoints.GetValueOrDefault(id) ?? definition.DefaultAt(settings.TelemetryAddress);
            var dialog = new MonitorPointDialog(definition, current) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Point is not { } point || point == current)
                return;

            var updated = new Dictionary<MonitorPointId, MonitorPoint>(monitorPoints) { [id] = point };
            if (station is not null)
                await station.UpdateMonitorPointsAsync(updated, () => store.SaveMonitorPoints(updated));
            else
            {
                var monitoring = new MonitorReadPlan(settings, updated);
                _ = new RecipeWritePlan(settings, recipePoints, monitoring);
                store.SaveMonitorPoints(updated);
            }

            monitorPoints = updated;
            view.Notice = $"{definition.Label}已保存 · 地址 {point.Address} · {point.DataType}";
        });
    }

    private async void EditRecipePoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe || sender is not Button { CommandParameter: RecipePointId id })
            return;

        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            string configPath = Path.Combine(dataFolder, "plc.json");
            var settings = station?.Settings ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
            var definition = RecipePoint.All[id];
            ushort? defaultAddress = definition.DefaultAddress(settings);
            bool hasOverride = recipePoints.TryGetValue(id, out ushort existing);
            var dialog = new RecipePointDialog(definition, hasOverride ? existing : defaultAddress, defaultAddress) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            if (view.Snapshot.Running)
                throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改地址。");

            var updated = new Dictionary<RecipePointId, ushort>(recipePoints);
            if (dialog.Address is ushort address && address != defaultAddress)
                updated[id] = address;
            else
                updated.Remove(id);

            if (station is not null)
                await station.UpdateRecipePointsAsync(updated, () => store.SaveRecipePoints(updated));
            else
            {
                _ = new RecipeWritePlan(settings, updated, new MonitorReadPlan(settings, monitorPoints));
                store.SaveRecipePoints(updated);
            }
            recipePoints = updated;
            view.Notice = (dialog.Address ?? defaultAddress) is ushort saved
                ? $"{definition.Label}写入地址已设为 {saved}。" : $"{definition.Label}写入地址已清除。";
        });
    }

    private void StartDemo_Click(object sender, RoutedEventArgs e) => StartDemo();
    private void DemoBarcode_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            StartDemo();
        }
    }

    private async void StartDemo()
    {
        if (!view.CanSimulate)
            return;
        await PerformAsync(async () =>
        {
            if (storageError is not null)
                throw new InvalidOperationException(storageError);
            var simulator = (SimulationStation)view.Station;
            simulator.Start(DemoBarcode.Text.Trim(), DemoFailure.IsChecked == true);
            DemoBarcode.Clear();
            var reading = await simulator.ReadAsync(CancellationToken.None);
            view.Update(reading.Test, reading.Feedback);
            view.Notice = "模拟测试运行中。";
        });
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (view.CanReset)
            await PerformAsync(async () =>
            {
                if (storageError is not null)
                    throw new InvalidOperationException(storageError);
                var reading = await view.Station.ReadAsync(CancellationToken.None);
                if (reading.Test.Stage == TestStage.Completed)
                    Archive(view.Station, reading.Test);
                await view.Station.ResetAsync();
                view.Notice = view.IsSimulation ? "模拟命令已执行。" : "PLC 已接收命令，请查看设备反馈。";
            });
    }

    private async void InspectionSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { DataContext: InspectionSwitch item } toggle)
            return;

        toggle.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
        if (!item.Available || item.Feedback is not bool current)
            return;

        await PerformAsync(async () =>
        {
            await view.Station.SetInspectionSwitchAsync(item.Key, !current);
            if (view.IsSimulation)
            {
                var reading = await view.Station.ReadAsync(CancellationToken.None);
                view.Update(reading.Test, reading.Feedback);
            }
            view.Notice = view.IsSimulation ? $"{item.Label}：{item.State}。" : $"{item.Label}已写入，请查看 PLC 反馈。";
        });
    }

    private async void EditInspectionPoint_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditMonitorPoints || sender is not Button { CommandParameter: InspectionSwitch item })
            return;

        await PerformAsync(async () =>
        {
            var station = view.Station as ModbusStation;
            string configPath = Path.Combine(dataFolder, "plc.json");
            var settings = station?.Settings ?? (File.Exists(configPath) ? PlcSettings.Load(configPath) : new PlcSettings());
            var current = InspectionPoints.Resolve(settings, inspectionPoints, item.Key);
            var dialog = new ManualPointDialog(item.Label, current.WriteAddress, current.FeedbackAddress, true) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Addresses is not { } point || point == current)
                return;
            if (view.Snapshot.Running)
                throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改地址。");

            if (settings.Commands.TryGetValue(item.Key, out ushort defaultWrite) && point.WriteAddress == defaultWrite)
                point = point with { WriteAddress = null };
            var updated = new Dictionary<string, InspectionPoint>(inspectionPoints);
            if (point is { WriteAddress: null, FeedbackAddress: null })
                updated.Remove(item.Key);
            else
                updated[item.Key] = point;

            if (station is not null)
                await station.UpdateInspectionPointsAsync(updated, () => store.SaveInspectionPoints(updated));
            else
            {
                _ = InspectionPoints.Validate(settings, updated, manualPoints);
                store.SaveInspectionPoints(updated);
            }
            inspectionPoints = updated;
            if (station is not null)
            {
                item.Feedback = null;
                item.Available = false;
            }
            view.Notice = $"{item.Label}地址已保存。";
        });
    }

    private void LoadRecipes(string? id)
    {
        selectingRecipe = true;
        try
        {
            view.Recipes = store.Recipes();
            var selected = view.Recipes.FirstOrDefault(item => item.Id == id) ?? view.Recipes[0];
            RecipePicker.SelectedValue = selected.Id;
            view.Draft = selected with { };
        }
        finally { selectingRecipe = false; }
    }

    private void RecipePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!selectingRecipe && RecipePicker.SelectedItem is Recipe selected)
            view.Draft = selected with { };
    }

    private async void NewRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe)
            return;

        await PerformAsync(() =>
        {
            var template = RecipePicker.SelectedItem as Recipe ?? view.ActiveRecipe;
            Recipe? created = null;
            var dialog = new NewRecipeDialog(template, recipe =>
            {
                if (view.Snapshot.Running)
                    throw new InvalidOperationException("测试进行中，请在测试结束后创建配方。");
                store.SaveRecipe(recipe, false);
                created = recipe;
            }) { Owner = this };
            if (dialog.ShowDialog() == true && created is not null)
            {
                LoadRecipes(created.Id);
                view.Notice = $"已创建配方“{created.Name}”。";
            }
            return Task.CompletedTask;
        });
    }

    private Recipe ValidDraft()
    {
        if (Validation.GetHasError(RecipeName) || RecipeFields.Children.OfType<TextBox>()
            .Concat(RecipeFields.Children.OfType<DockPanel>().SelectMany(panel => panel.Children.OfType<TextBox>()))
            .Any(Validation.GetHasError))
            throw new InvalidOperationException("请修正标红的参数输入。");
        var recipe = view.Draft with { };
        recipe.Validate();
        return recipe;
    }

    private async void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe)
            return;
        await PerformAsync(() =>
        {
            var recipe = ValidDraft();
            store.SaveRecipe(recipe, false);
            LoadRecipes(recipe.Id);
            view.Notice = "配方已保存。";
            return Task.CompletedTask;
        });
    }

    private async void ApplyRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanApplyRecipe)
            return;
        await PerformAsync(async () =>
        {
            var recipe = ValidDraft();
            if (!view.Recipes.Contains(recipe))
                throw new InvalidOperationException("请先保存配方，再启用或下发。");
            var station = view.Station;
            if (view.IsSimulation)
            {
                store.SaveRecipe(recipe, true);
                await station.ApplyAsync(recipe);
                view.ActiveRecipe = recipe;
                view.Notice = $"已启用模拟配方“{recipe.Name}”。";
            }
            else
            {
                store.SaveRecipe(recipe, true);
                view.ActiveRecipe = recipe;
                view.Notice = $"已设为当前配方，正在下发“{recipe.Name}”。";
                view.SetSendingRecipe(true);
                recipeDispatch = DispatchRecipeAsync(station, recipe);
            }
        });
    }

    private async Task DispatchRecipeAsync(IStation station, Recipe recipe)
    {
        try
        {
            await station.ApplyAsync(recipe);
            view.Notice = $"配方“{recipe.Name}”已下发。";
        }
        catch (Exception ex)
        {
            view.Notice = $"配方“{recipe.Name}”下发失败。{ex.Message}";
        }
        finally { view.SetSendingRecipe(false); }
    }

    private async void DeleteRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanEditRecipe || RecipePicker.SelectedItem is not Recipe recipe)
            return;
        if (!Confirm($"删除配方“{recipe.Name}”？"))
            return;
        await PerformAsync(() =>
        {
            store.DeleteRecipe(recipe.Id);
            LoadRecipes(view.ActiveRecipe.Id);
            view.Notice = "配方已删除。";
            return Task.CompletedTask;
        });
    }

    private void DateValidation_Error(object sender, DatePickerDateValidationErrorEventArgs e) =>
        view.Notice = "日期格式无效，请使用日历选择日期。";

    private static DateTime? ReadDate(DatePicker picker)
    {
        if (string.IsNullOrWhiteSpace(picker.Text))
            return null;
        if (DateTime.TryParse(picker.Text, CultureInfo.GetCultureInfo("zh-CN"), DateTimeStyles.None, out var date))
            return date.Date;
        throw new InvalidOperationException("日期格式无效，请使用日历选择日期。");
    }

    private IEnumerable<TestRecord> HistoryResults() => store.History(ReadDate(DateFrom), ReadDate(DateTo), BarcodeFilter.Text);

    private void RefreshHistory() => view.Records = HistoryResults().ToList();

    private void TryRefreshHistory()
    {
        try { RefreshHistory(); }
        catch (Exception ex) { view.Notice = ex.Message; }
    }

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();
    private async void BarcodeFilter_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await QueryAsync();
        }
    }

    private Task QueryAsync() => PerformAsync(() =>
    {
        RefreshHistory();
        view.Notice = "历史数据查询完成。";
        return Task.CompletedTask;
    });

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(async () =>
        {
            var records = HistoryResults();
            var save = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"灰尘负载测试记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                AddExtension = true
            };
            if (save.ShowDialog(this) != true)
                return;
            await Task.Run(() => StationStore.Export(save.FileName, records));
            view.Notice = $"已导出至 {save.FileName}";
        });
    }

    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanDelete || HistoryGrid.SelectedItem is not TestRecord record || !Confirm($"删除测试记录 #{record.Id}？"))
            return;
        await PerformAsync(() =>
        {
            store.DeleteRecord(record.Id);
            RefreshHistory();
            view.Notice = "测试记录已删除。";
            return Task.CompletedTask;
        });
    }

    private async void CopyPhone_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(() =>
        {
            Clipboard.SetText("18550969605");
            view.Notice = "联系电话已复制。";
            return Task.CompletedTask;
        });
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await PerformAsync(async () =>
        {
            bool setup = view.NeedsAccount;
            var dialog = new CredentialsDialog(setup ? "创建负责人" : "登录", setup, false) { Owner = this };
            if (dialog.ShowDialog() != true)
                return;
            string name = dialog.UserName;
            string password = dialog.Password;
            if (setup)
                await Task.Run(() => store.CreateUser(name, password, UserRole.Supervisor, true));
            view.NeedsAccount = false;
            view.User = await Task.Run(() => store.Authenticate(name, password))
                ?? throw new InvalidOperationException("用户名或密码错误，或账号已停用。");
            view.Notice = $"已登录为 {view.OperatorLabel}。";
        });
    }

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (!view.CanLogout)
            return;
        view.User = null;
        view.Notice = "已退出登录。";
    }

    private void Users_Click(object sender, RoutedEventArgs e)
    {
        if (view.CanManageUsers && view.User is { } user)
            new UsersDialog(store, user) { Owner = this }.ShowDialog();
    }

    private bool Confirm(string message) =>
        MessageBox.Show(this, message, "灰尘负载测试台", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    internal static void AddDialogButton(Panel panel, string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0) };
        if (primary)
            button.Style = (Style)Application.Current.FindResource("Primary");
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }
}
