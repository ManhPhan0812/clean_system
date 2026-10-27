using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: AssemblyTitle("Dọn dẹp ổ cứng")]
[assembly: AssemblyProduct("Dọn dẹp ổ cứng")]
[assembly: AssemblyDescription("Ứng dụng dọn file tạm, cache lập trình và Thùng rác trên Windows.")]
[assembly: AssemblyCompany("Until You")]
[assembly: AssemblyCopyright("by Until You")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace CleanSystem
{
    internal static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            PathPolicy.EnableLongPaths();
            bool smoke = args.Length == 1 && args[0] == "--smoke-test";
            try
            {
                int elevatedTempDays;
                bool elevatedTemp = TempElevation.TryReadArguments(args, out elevatedTempDays);
                var application = new Application();
                application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                    MessageBox.Show("Không thể hoàn tất thao tác.\n" + e.Exception.Message, "Dọn dẹp ổ cứng", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
                var controller = new MainController();
                if (elevatedTemp) controller.ConfigureWindowsTemp(elevatedTempDays);
                if (smoke)
                {
                    // Run the real WPF layout/binding lifecycle without showing a visible window.
                    controller.Window.ShowActivated = false;
                    controller.Window.ShowInTaskbar = false;
                    controller.Window.Opacity = 0;
                    controller.Window.Show();
                    controller.Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
                    controller.VerifyInitialUi();
                    var content = (FrameworkElement)controller.Window.Content;
                    content.UpdateLayout();
                    int width = (int)Math.Ceiling(content.ActualWidth), height = (int)Math.Ceiling(content.ActualHeight);
                    var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    byte[] pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(30, 30, 1, 1), pixel, 4, 0);
                    if (pixel[3] == 0) throw new InvalidOperationException("The UI preview is empty.");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    string directory = AppDomain.CurrentDomain.BaseDirectory;
                    using (var stream = File.Create(Path.Combine(directory, "preview.png"))) encoder.Save(stream);
                    controller.SetMode(true);
                    controller.Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
                    controller.VerifyInitialUi();
                    content.UpdateLayout();
                    bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var stream = File.Create(Path.Combine(directory, "preview-developer.png"))) encoder.Save(stream);
                    controller.VerifyCommandDialogs();
                    controller.VerifyAdminAccess();
                    controller.VerifyTempElevation();
                    controller.VerifyRecyclePage();
                    File.WriteAllText(Path.Combine(directory, "smoke-test.txt"), "PASS: XAML loaded, controls bound, window rendered; no scan or deletion performed.", Encoding.UTF8);
                    controller.Window.Close();
                    return 0;
                }
                application.Run(controller.Window);
                return 0;
            }
            catch (Exception e)
            {
                if (smoke) File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-test.txt"), e.ToString());
                else MessageBox.Show("Không thể mở ứng dụng.\n" + e.Message, "Dọn dẹp ổ cứng", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }

    internal sealed class MainController
    {
        public Window Window { get; private set; }
        private readonly Cleaner cleaner;
        private readonly Func<bool> administrator;
        private readonly Action<string> accessWarning;
        private readonly CommandRunner commandRunner = new CommandRunner();
        private readonly RecycleBinService recycleService;
        private readonly Func<ProcessStartInfo, bool> elevationLauncher;
        private bool declinedTempElevation;
        private Func<string, string, bool> confirmationProbe;
        private readonly string logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"CleanSystem\Logs");
        private ScanResult scan;
        private CancellationTokenSource cancellation;
        private bool busy;
        private bool busyCanCancel;
        private bool developerMode;
        private bool recycleMode;
        private RecycleBinInfo recycleInfo;
        private Button scanButton, cleanButton, previewButton, cancelButton, guideButton, homeButton, developerButton;
        private Button diskRefreshButton;
        private Button recycleNavButton, recycleRefreshButton, recycleEmptyButton, recycleOpenButton;
        private DataGrid groupsGrid;
        private ComboBox ageBox;
        private CheckBox presentOnlyBox;
        private bool onlyPresent;
        private TextBlock statusText, selectedText, selectedCountText;

        public MainController(Func<bool> adminProbe = null, Action<string> warning = null, RecycleBinService recycleOperations = null, Func<ProcessStartInfo, bool> elevatedLauncher = null)
        {
            elevationLauncher = elevatedLauncher ?? (info => { using (var process = Process.Start(info)) return process != null; });
            recycleService = recycleOperations ?? new RecycleBinService();
            administrator = adminProbe ?? WindowsUpdateCache.IsAdministrator;
            cleaner = new Cleaner(administrator);
            accessWarning = warning ?? (message => MessageBox.Show(Window, message, "Cần quyền quản trị", MessageBoxButton.OK, MessageBoxImage.Warning));
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream("CleanSystem.MainWindow.xaml"))
                Window = (Window)XamlReader.Load(source);
            scanButton = Find<Button>("ScanButton"); cleanButton = Find<Button>("CleanButton");
            previewButton = Find<Button>("PreviewButton"); cancelButton = Find<Button>("CancelButton");
            guideButton = Find<Button>("GuideButton"); homeButton = Find<Button>("HomeButton"); developerButton = Find<Button>("DeveloperButton");
            diskRefreshButton = Find<Button>("DiskRefreshButton");
            recycleNavButton = Find<Button>("RecycleNavButton"); recycleRefreshButton = Find<Button>("RecycleRefreshButton");
            recycleEmptyButton = Find<Button>("RecycleEmptyButton"); recycleOpenButton = Find<Button>("RecycleOpenButton");
            groupsGrid = Find<DataGrid>("GroupsGrid"); ageBox = Find<ComboBox>("AgeBox");
            presentOnlyBox = Find<CheckBox>("PresentOnlyBox");
            statusText = Find<TextBlock>("StatusText"); selectedText = Find<TextBlock>("SelectedText"); selectedCountText = Find<TextBlock>("SelectedCountText");
            ResetGroups();
            groupsGrid.PreviewMouseDown += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) {
                if (busy) return;
                var source = e.OriginalSource as DependencyObject;
                var row = source == null ? null : ItemsControl.ContainerFromElement(groupsGrid, source) as DataGridRow;
                var group = row == null ? null : row.Item as CleanupGroup;
                if (group != null && !CanAccessGroup(group.Id)) { e.Handled = true; WarnGroupAccess(); }
            };
            groupsGrid.SelectionChanged += delegate {
                var group = groupsGrid.SelectedItem as CleanupGroup;
                if (group != null && !CanAccessGroup(group.Id)) { groupsGrid.SelectedItem = null; WarnGroupAccess(); }
            };
            groupsGrid.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler(SelectionChanged));
            groupsGrid.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler(SelectionChanged));
            groupsGrid.AddHandler(Button.ClickEvent, new RoutedEventHandler(async delegate(object sender, RoutedEventArgs e) {
                var button = e.OriginalSource as Button;
                var group = button == null ? null : button.DataContext as CleanupGroup;
                if (button == null || group == null || !developerMode || !group.IsDeveloper) return;
                if (Object.Equals(button.Tag, "run-group")) { e.Handled = true; await RunCommandAsync(group.Id); return; }
                if (!Object.Equals(button.Tag, "scan-group")) return;
                e.Handled = true;
                await ScanAsync(group.Id);
            }));
            scanButton.Click += async delegate { await ScanAsync(); };
            diskRefreshButton.Click += delegate { RefreshDisk(); };
            Find<Button>("TempAdminButton").Click += delegate { RequestTempElevation(); };
            cleanButton.Click += async delegate { await CleanAsync(); };
            previewButton.Click += delegate { ShowPreview(); };
            guideButton.Click += delegate { ShowGuide(); };
            cancelButton.Click += delegate { RequestCancel(); };
            ageBox.SelectionChanged += delegate {
                if (busy) return;
                scan = null; ResetGroups(); UpdateSelection();
                statusText.Text = "Đã đổi thời gian giữ file. Quét lại để cập nhật danh sách.";
            };
            presentOnlyBox.Checked += delegate { PresentFilterChanged(); };
            presentOnlyBox.Unchecked += delegate { PresentFilterChanged(); };
            homeButton.Click += delegate { SetMode(false); };
            developerButton.Click += delegate { SetMode(true); };
            recycleNavButton.Click += async delegate { if (busy) return; EnterRecycleMode(); await RefreshRecycleAsync(); };
            recycleRefreshButton.Click += async delegate { await RefreshRecycleAsync(); };
            recycleEmptyButton.Click += async delegate { await EmptyRecycleAsync(); };
            recycleOpenButton.Click += delegate {
                try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "shell:RecycleBinFolder") { UseShellExecute = true }); }
                catch (Exception e) { ShowError(e.Message); }
            };
            Find<Button>("StorageButton").Click += delegate { Open("ms-settings:storagesense"); };
            Find<Button>("LogButton").Click += delegate {
                try { Directory.CreateDirectory(logDirectory); Open(logDirectory); }
                catch (Exception e) { ShowError(e.Message); }
            };
            Window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (busy)
                {
                    e.Cancel = true;
                    if (recycleMode) { Find<TextBlock>("RecycleStatusText").Text = "Đang chờ Windows hoàn tất. Có thể đóng cửa sổ khi thao tác kết thúc."; return; }
                    if (!busyCanCancel) { statusText.Text = "Đang chờ dọn cache Windows Update và khởi động lại dịch vụ. Đóng cửa sổ sau khi thao tác kết thúc."; return; }
                    RequestCancel(); statusText.Text = "Đang dừng thao tác. Bạn có thể đóng cửa sổ khi thao tác kết thúc.";
                }
            };
            RefreshDisk();
            Find<DataGridTemplateColumn>("FolderScanColumn").Visibility = Visibility.Collapsed;
            Find<DataGridTemplateColumn>("RunCommandColumn").Visibility = Visibility.Collapsed;
            UpdateTempElevationNotice();
        }
        private void UpdateTempElevationNotice()
        {
            var notice = Find<StackPanel>("TempAdminNotice");
            notice.Visibility = !developerMode && !recycleMode && !administrator() ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("TempAdminButton").IsEnabled = !busy;
        }
        public void ConfigureWindowsTemp(int days)
        {
            SetMode(false);
            ageBox.SelectedItem = ageBox.Items.Cast<ComboBoxItem>().Single(item => item.Tag.ToString() == days.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var group in (IEnumerable<CleanupGroup>)groupsGrid.ItemsSource) group.Selected = group.Id == "windows-temp";
            groupsGrid.Items.Refresh(); UpdateSelection();
            statusText.Text = administrator() ? "Đã mở bằng quyền quản trị. Bấm Quét ổ C để đọc lại Windows Temp, xem danh sách rồi xác nhận dọn." : "Chưa được cấp quyền quản trị. Có thể quét bằng quyền hiện tại hoặc bấm Cấp quyền Windows Temp.";
        }
        private bool RequestTempElevation()
        {
            if (busy || developerMode || recycleMode || administrator()) return false;
            declinedTempElevation = true;
            if (!ConfirmAction("Cấp quyền dọn Windows Temp?", "Một số mục trong C:\\Windows\\Temp cần quyền quản trị, giống hộp thoại quyền khi bạn xóa bằng File Explorer.\n\nChọn Có để Windows hiện UAC và mở lại ứng dụng bằng quyền quản trị. Sau đó quét lại Windows Temp, xem danh sách và xác nhận dọn. Chưa xóa file ở bước cấp quyền.\n\nChọn Không hoặc từ chối UAC để giữ ứng dụng hiện tại và tiếp tục với các file đang có quyền truy cập. Các mục thiếu quyền sẽ được bỏ qua.")) return false;
            try {
                int days = Int32.Parse(((ComboBoxItem)ageBox.SelectedItem).Tag.ToString());
                var info = TempElevation.CreateStartInfo(Assembly.GetExecutingAssembly().Location, days,
                    new System.Windows.Interop.WindowInteropHelper(Window).Handle);
                if (!elevationLauncher(info)) { statusText.Text = "Không mở được bản quản trị. Ứng dụng hiện tại vẫn hoạt động."; return false; }
                Window.Close(); return true;
            }
            catch (System.ComponentModel.Win32Exception e) {
                statusText.Text = e.NativeErrorCode == 1223 ? "Bạn chưa cấp quyền quản trị. Tiếp tục bằng quyền hiện tại; bỏ qua các mục thiếu quyền." : "Không mở được bản quản trị: " + e.Message;
            }
            catch (Exception e) { statusText.Text = "Không mở được bản quản trị: " + e.Message; }
            return false;
        }
        private void ResetGroups()
        {
            // Presence is re-read here so a cache folder created (or a CLI installed) since launch is picked up.
            cleaner.RefreshEnvironment();
            var groups = cleaner.NewGroups().Where(g => g.IsDeveloper == developerMode)
                .Where(g => !onlyPresent || g.Applicable).ToList();
            foreach (var group in groups) if (!CanAccessGroup(group.Id)) group.State = "Cần Run as administrator · không được truy cập";
            groupsGrid.ItemsSource = groups;
        }
        private void PresentFilterChanged()
        {
            if (busy || recycleMode) return;
            onlyPresent = presentOnlyBox.IsChecked == true;
            scan = null; ResetGroups(); UpdateSelection();
            statusText.Text = onlyPresent
                ? "Đang chỉ hiện mục có thư mục hoặc công cụ trên máy này. Quét lại để đo dung lượng."
                : "Đang hiện tất cả các mục. Quét lại để đo dung lượng.";
        }
        private bool CanAccessGroup(string groupId) { return groupId != WindowsUpdateCache.GroupId || administrator(); }
        private void WarnGroupAccess()
        {
            statusText.Text = "Windows Update · Download bị khóa. Mở ứng dụng bằng Run as administrator.";
            accessWarning(WindowsUpdateCache.AccessWarning);
        }
        private bool RequireGroupAccess(string groupId)
        {
            if (CanAccessGroup(groupId)) return true;
            WarnGroupAccess(); return false;
        }
        public void SetMode(bool developer)
        {
            if (busy) return;
            recycleMode = false; Find<Grid>("RecyclePage").Visibility = Visibility.Collapsed; Find<Grid>("CleanupPage").Visibility = Visibility.Visible;
            recycleNavButton.Background = Brushes.Transparent; recycleNavButton.Foreground = new SolidColorBrush(Color.FromRgb(180, 192, 212));
            developerMode = developer; scan = null; ResetGroups(); UpdateSelection();
            Find<TextBlock>("PageLabel").Text = developer ? "OTHER  /  CACHE VÀ CÔNG CỤ" : "DỌN DẸP  /  Ổ C";
            Find<TextBlock>("PageTitle").Text = developer ? "Biết dữ liệu nào đang chiếm chỗ." : "Thêm chỗ trống. Bớt file tạm.";
            Find<TextBlock>("PageSubtitle").Text = developer ? "Đo cache Windows Update, cache lập trình, SDK và dữ liệu cần giữ." : "Xem dung lượng có thể dọn trước khi quyết định xóa.";
            Find<TextBlock>("ListTitle").Text = developer ? "Cache và dữ liệu công cụ" : "Chọn nhóm cần dọn";
            Find<TextBlock>("ListSubtitle").Text = developer ? "Quét riêng hoặc chạy lệnh trên từng dòng. Lệnh chạy cần xác nhận thư mục đích." : "File tạm được chọn sẵn. Báo cáo lỗi là tùy chọn.";
            Find<TextBlock>("SafetyText").Text = developer ? "Chạy lệnh dọn toàn bộ theo thao tác của từng mục, không áp dụng tuổi file. Kiểm tra lệnh và đích trong bước xác nhận; reset dữ liệu ứng dụng có thể mất cấu hình/lịch sử. Nút Dọn mục đã chọn vẫn chỉ dọn file đủ điều kiện." : "Chỉ dọn các thư mục tạm cho phép trên ổ C. Bỏ qua file đang dùng và đường dẫn liên kết. Không đụng tới Downloads, tài liệu, Registry hay file hệ thống.";
            scanButton.Content = developer ? "Quét Other" : "Quét ổ C";
            UpdateTempElevationNotice();
            Find<DataGridTemplateColumn>("FolderScanColumn").Visibility = developer ? Visibility.Visible : Visibility.Collapsed;
            Find<DataGridTemplateColumn>("RunCommandColumn").Visibility = developer ? Visibility.Visible : Visibility.Collapsed;
            homeButton.Background = new SolidColorBrush(developer ? Colors.Transparent : Color.FromRgb(41, 57, 81));
            developerButton.Background = new SolidColorBrush(developer ? Color.FromRgb(41, 57, 81) : Colors.Transparent);
            homeButton.Foreground = new SolidColorBrush(developer ? Color.FromRgb(180, 192, 212) : Colors.White);
            developerButton.Foreground = new SolidColorBrush(developer ? Colors.White : Color.FromRgb(180, 192, 212));
            statusText.Text = developer ? "Quét các vị trí mặc định trên ổ C. Dữ liệu ngoài phạm vi này không được thống kê." : "Sẵn sàng. Bấm Quét ổ C để bắt đầu.";
        }
        private T Find<T>(string name) where T : class
        {
            T result = Window.FindName(name) as T;
            if (result == null) throw new InvalidOperationException("Missing UI control: " + name);
            return result;
        }
        private void RefreshDisk()
        {
            try
            {
                var drive = new DriveInfo(@"C:\");
                Find<TextBlock>("FreeText").Text = Format.Bytes(drive.AvailableFreeSpace);
                Find<TextBlock>("DiskText").Text = Format.Bytes(drive.TotalSize - drive.TotalFreeSpace) + " đã dùng / " + Format.Bytes(drive.TotalSize);
                var bar = Find<ProgressBar>("DiskBar");
                bar.Value = (drive.TotalSize - drive.TotalFreeSpace) * 100.0 / drive.TotalSize;
                bar.Foreground = new SolidColorBrush(bar.Value > 90 ? Color.FromRgb(214, 110, 57) : Color.FromRgb(83, 143, 208));
            }
            catch (Exception) { Find<TextBlock>("FreeText").Text = "Không đọc được"; Find<TextBlock>("DiskText").Text = "Kiểm tra ổ C trong File Explorer."; }
        }
        private void SelectionChanged(object sender, RoutedEventArgs e)
        {
            // Checked is raised before the binding may finish writing Selected back to its source.
            Window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(UpdateSelection));
        }
        public void VerifyInitialUi()
        {
            if (((ComboBoxItem)ageBox.SelectedItem).Tag.ToString() != "0" ||
                !ageBox.Items.Cast<ComboBoxItem>().Any(item => item.Tag.ToString() == "7"))
                throw new InvalidOperationException("Unlimited age must be the default and optional age retention must remain available.");
            if (!diskRefreshButton.IsVisible || !diskRefreshButton.IsEnabled) throw new InvalidOperationException("Disk refresh button is unavailable on this page.");
            var previousGroups = groupsGrid.ItemsSource; var previousScan = scan;
            Find<TextBlock>("FreeText").Text = "Chưa cập nhật";
            diskRefreshButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (Find<TextBlock>("FreeText").Text == "Chưa cập nhật" || !Object.ReferenceEquals(previousGroups, groupsGrid.ItemsSource) || !Object.ReferenceEquals(previousScan, scan))
                throw new InvalidOperationException("Disk refresh did not update capacity or unexpectedly rescanned folders.");
            if (cleanButton.IsEnabled || previewButton.IsEnabled) throw new InvalidOperationException("Cleanup must be disabled before a scan.");
            var groups = ((IEnumerable<CleanupGroup>)groupsGrid.ItemsSource).ToList();
            if (groups.Any(g => g.Selected != (!developerMode && (g.Id == "user-temp" || g.Id == "windows-temp"))))
                throw new InvalidOperationException("Optional error reports must not be selected by default.");
            if (groups.Count == 0) return;
            var row = groupsGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            CheckBox checkbox = VisualChild<CheckBox>(row);
            if (checkbox == null) throw new InvalidOperationException("Group selection checkbox did not render.");
            bool original = groups[0].Selected;
            checkbox.IsChecked = !original;
            Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
            if (groups[0].Selected == original) throw new InvalidOperationException("Group selection binding failed.");
            checkbox.IsChecked = original;
            Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
            var folderColumn = Find<DataGridTemplateColumn>("FolderScanColumn");
            if (folderColumn.Visibility != (developerMode ? Visibility.Visible : Visibility.Collapsed))
                throw new InvalidOperationException("Folder scan column visibility is incorrect.");
            if (developerMode)
            {
                Button folderButton = RowButton(row, "scan-group");
                if (folderButton == null || !Object.Equals(folderButton.Tag, "scan-group") || !Object.ReferenceEquals(folderButton.DataContext, groups[0]) || !folderButton.IsEnabled)
                    throw new InvalidOperationException("Folder scan button did not render or bind to its row.");
                Button runButton = RowButton(row, "run-group");
                if (runButton == null || !Object.ReferenceEquals(runButton.DataContext, groups[0]) || !runButton.IsEnabled)
                    throw new InvalidOperationException("Command button did not bind to its row.");
            }
        }
        private static Button RowButton(DependencyObject parent, string tag)
        {
            if (parent == null) return null;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                var button = child as Button;
                if (button != null && Object.Equals(button.Tag, tag)) return button;
                var match = RowButton(child, tag); if (match != null) return match;
            }
            return null;
        }
        private static T VisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                T match = child as T ?? VisualChild<T>(child);
                if (match != null) return match;
            }
            return null;
        }
        private List<FileCandidate> SelectedFiles()
        {
            if (scan == null) return new List<FileCandidate>();
            return scan.Groups.Where(g => g.Selected && g.CanClean && g.IsDeveloper == developerMode).SelectMany(g => g.Files).ToList();
        }
        private void UpdateSelection()
        {
            var files = SelectedFiles();
            selectedText.Text = scan == null ? "—" : Format.Bytes(files.Sum(f => f.Length));
            selectedCountText.Text = scan == null ? (developerMode ? "Quét để đo cache và dữ liệu" : "Quét để tìm file tạm") : files.Count.ToString("N0") + " file trong các nhóm đã chọn";
            Find<TextBlock>("EstimateText").Text = developerMode && scan != null ? "Đã đo: " + Format.Bytes(scan.Groups.Sum(g => g.TotalBytes)) + (scan.Groups.Any(g => g.Partial) ? " (một phần)" : "") : "Ước tính theo kích thước file";
            cleanButton.IsEnabled = !busy && scan != null && !scan.Canceled && files.Count > 0;
            previewButton.IsEnabled = !busy && files.Count > 0;
        }
        private void SetBusy(bool value, bool cancellable = true)
        {
            busy = value; busyCanCancel = value && cancellable; scanButton.IsEnabled = !value; ageBox.IsEnabled = !value; groupsGrid.IsEnabled = !value;
            presentOnlyBox.IsEnabled = !value;
            homeButton.IsEnabled = !value; developerButton.IsEnabled = !value; guideButton.IsEnabled = !value;
            recycleNavButton.IsEnabled = !value; recycleRefreshButton.IsEnabled = !value; recycleOpenButton.IsEnabled = !value;
            Find<ProgressBar>("RecycleProgress").Visibility = value && recycleMode ? Visibility.Visible : Visibility.Collapsed;
            cancelButton.Visibility = value && cancellable ? Visibility.Visible : Visibility.Collapsed;
            cancelButton.IsEnabled = value && cancellable; UpdateSelection(); UpdateRecycleInfo();
            UpdateTempElevationNotice();
        }
        private void RequestCancel()
        {
            if (!busyCanCancel) return;
            if (cancellation != null) cancellation.Cancel();
            cancelButton.IsEnabled = false; statusText.Text = "Đang dừng sau mục hiện tại…";
        }
        private async Task ScanAsync(string groupId = null)
        {
            if (busy) return;
            bool singleFolder = groupId != null;
            var displayedGroups = ((IEnumerable<CleanupGroup>)groupsGrid.ItemsSource).ToList();
            var target = singleFolder ? displayedGroups.FirstOrDefault(g => g.Id == groupId && g.IsDeveloper) : null;
            if (singleFolder && (!developerMode || target == null)) return;
            if (singleFolder && !RequireGroupAccess(groupId)) return;
            if (!developerMode && !administrator() && !declinedTempElevation && displayedGroups.Any(group => group.Id == "windows-temp" && group.Selected))
                if (RequestTempElevation()) return;
            if (!singleFolder) { scan = null; ResetGroups(); }
            cancellation = new CancellationTokenSource(); SetBusy(true);
            int days = Int32.Parse(((ComboBoxItem)ageBox.SelectedItem).Tag.ToString());
            string scope = singleFolder ? target.Name : developerMode ? "Other" : "ổ C";
            statusText.Text = "Đang quét " + scope + "…";
            var operation = cancellation;
            var progress = new Progress<int>(count => { if (busy && Object.ReferenceEquals(cancellation, operation) && !operation.IsCancellationRequested) statusText.Text = "Đang quét " + scope + " · " + count.ToString("N0") + " mục đã kiểm tra…"; });
            try
            {
                var update = await Task.Run(() => cleaner.Scan(days, cancellation.Token, count => ((IProgress<int>)progress).Report(count), developerMode, groupId));
                // A canceled full scan also must not leave candidates that a later folder scan could revive.
                if (update.Canceled) foreach (var group in update.Groups) group.Files.Clear();
                scan = singleFolder ? ScanResult.MergeFolderScan(displayedGroups, update, groupId) : update;
                // Keep the visible filter after a full scan: a row stays when it exists, has its tool, or was measured.
                if (!singleFolder && onlyPresent)
                    scan.Groups = scan.Groups.Where(g => g.Measured || g.Applicable).ToList();
                groupsGrid.ItemsSource = scan.Groups;
                if (singleFolder) groupsGrid.SelectedItem = scan.Groups.First(g => g.Id == groupId);
                statusText.Text = (update.Canceled ? "Đã dừng quét " + scope + ". Quét lại mục này trước khi dọn." : update.Limited ? "Có nhóm đạt giới hạn 100.000 mục; kết quả chỉ là một phần." : "Quét xong " + scope + ".") +
                    " Đã kiểm tra " + update.Inspected.ToString("N0") + " mục; bỏ qua " + update.Skipped.ToString("N0") + " mục mới, được bảo vệ hoặc không truy cập được. " + (developerMode ? "Chọn dòng để xem Cách dọn." : "");
            }
            catch (Exception e)
            {
                if (singleFolder)
                {
                    var failedGroup = cleaner.NewGroups().First(g => g.Id == groupId);
                    failedGroup.State = "Quét không hoàn tất · hãy thử lại";
                    scan = ScanResult.MergeFolderScan(displayedGroups, new ScanResult { Groups = new List<CleanupGroup> { failedGroup }, CutoffUtc = days == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddDays(-days) }, groupId);
                    groupsGrid.ItemsSource = scan.Groups;
                }
                else { scan = null; ResetGroups(); }
                statusText.Text = "Quét " + scope + " không hoàn tất: " + e.Message;
            }
            finally { SetBusy(false); cancellation.Dispose(); cancellation = null; RefreshDisk(); }
        }
        private async Task CleanAsync()
        {
            if (busy || scan == null || scan.Canceled) return;
            var files = SelectedFiles(); if (files.Count == 0) return;
            string groupNames = String.Join("\n", scan.Groups.Where(g => g.Selected && g.Count > 0).Select(g => "• " + g.Name + " — " + g.Count.ToString("N0") + " file (" + g.SizeText + ")"));
            string message = "Xóa vĩnh viễn " + files.Count.ToString("N0") + " file, tổng kích thước " + Format.Bytes(files.Sum(f => f.Length)) + "?\n\n" +
                groupNames + "\n\nCác file này không được chuyển vào Thùng rác. File đang dùng hoặc thay đổi sau khi quét sẽ bị bỏ qua.\n\nChọn Có để bắt đầu.";
            if (scan.CutoffUtc == DateTime.MaxValue) message += "\n\nBạn đã chọn Không giới hạn ngày: danh sách gồm cả file tạm mới tạo. Hãy đóng ứng dụng và trình cài đặt liên quan trước khi dọn.";
            if (developerMode) message += "\n\nXác nhận rằng bạn đã đóng IDE và dừng Gradle/build. Cache đã dọn sẽ cần tạo lại; lần build sau có thể chậm hơn.";
            if (!ConfirmAction("Xác nhận dọn file tạm", message)) return;
            DateTime cutoff = scan.CutoffUtc;
            cancellation = new CancellationTokenSource(); SetBusy(true);
            statusText.Text = "Đang kiểm tra lại và dọn file đã chọn…";
            var operation = cancellation;
            var progress = new Progress<int>(count => { if (busy && Object.ReferenceEquals(cancellation, operation) && !operation.IsCancellationRequested) statusText.Text = "Đang dọn · " + count.ToString("N0") + " / " + files.Count.ToString("N0") + " file…"; });
            try
            {
                CleanResult result = await Task.Run(() => cleaner.Clean(files, cutoff, logDirectory, cancellation.Token, count => ((IProgress<int>)progress).Report(count)));
                statusText.Text = (result.Canceled ? "Đã dừng." : "Đã hoàn tất lượt dọn.") + " Đã xóa " + result.Deleted.ToString("N0") + " file (" + Format.Bytes(result.Bytes) +
                    "); bỏ qua " + result.Skipped.ToString("N0") + " file. Quét lại để cập nhật.";
                if (result.Error != null) { statusText.Text += " " + result.Error; ShowError(result.Error); }
                else statusText.Text += " Nhật ký đã lưu.";
            }
            catch (Exception e) { statusText.Text = "Thao tác đã dừng: " + e.Message; ShowError(e.Message); }
            finally
            {
                // Never reuse a snapshot after any cleanup attempt, including cancellation or log failure.
                scan = null; ResetGroups();
                SetBusy(false); cancellation.Dispose(); cancellation = null; RefreshDisk();
            }
        }
        private void EnterRecycleMode()
        {
            if (busy) return;
            recycleMode = true; scan = null;
            Find<Grid>("CleanupPage").Visibility = Visibility.Collapsed; Find<Grid>("RecyclePage").Visibility = Visibility.Visible;
            homeButton.Background = Brushes.Transparent; developerButton.Background = Brushes.Transparent;
            homeButton.Foreground = new SolidColorBrush(Color.FromRgb(180, 192, 212)); developerButton.Foreground = homeButton.Foreground;
            recycleNavButton.Background = new SolidColorBrush(Color.FromRgb(41, 57, 81)); recycleNavButton.Foreground = Brushes.White;
            UpdateRecycleInfo();
        }
        private void UpdateRecycleInfo()
        {
            Find<TextBlock>("RecycleSizeText").Text = recycleInfo == null ? "—" : Format.Bytes(recycleInfo.Bytes);
            Find<TextBlock>("RecycleCountText").Text = recycleInfo == null ? "—" : recycleInfo.Count.ToString("N0") + " mục";
            recycleEmptyButton.IsEnabled = recycleMode && !busy && recycleInfo != null && recycleInfo.Count > 0;
        }
        private async Task RefreshRecycleAsync()
        {
            if (busy || !recycleMode) return;
            recycleInfo = null; SetBusy(true, false);
            Find<TextBlock>("RecycleStatusText").Text = "Đang đọc Thùng rác trên tất cả ổ đĩa…";
            try
            {
                recycleInfo = await recycleService.QueryAsync();
                Find<TextBlock>("RecycleStatusText").Text = recycleInfo.Count == 0 ? "Thùng rác trên tất cả ổ đĩa đang trống." : "Đã cập nhật Thùng rác trên tất cả ổ đĩa. Mở Thùng rác để kiểm tra hoặc khôi phục dữ liệu trước khi dọn.";
            }
            catch (Exception e) { Find<TextBlock>("RecycleStatusText").Text = "Không đọc được Thùng rác: " + e.Message; }
            finally { SetBusy(false); }
        }
        private async Task EmptyRecycleAsync()
        {
            if (busy || !recycleMode || recycleInfo == null || recycleInfo.Count == 0) return;
            var approved = recycleInfo;
            string message = "Xóa vĩnh viễn toàn bộ " + approved.Count.ToString("N0") + " mục trong Thùng rác trên tất cả ổ đĩa của tài khoản hiện tại?\n\n" +
                "Tổng kích thước Windows báo cáo: " + Format.Bytes(approved.Bytes) + ".\n\n" +
                "Hãy khôi phục các mục cần giữ trước khi dọn. Không áp dụng ngưỡng tuổi file. Khi một mục cần quyền xóa, chọn Có để cho phép hoặc Không để giữ mục đó và tiếp tục. File không xóa được sẽ được bỏ qua. Thao tác này không có nút Dừng và không thể khôi phục bằng Thùng rác sau khi hoàn tất.";
            if (!ConfirmAction("Xác nhận dọn Thùng rác · Tất cả ổ đĩa", message)) return;
            SetBusy(true, false);
            Find<TextBlock>("RecycleStatusText").Text = "Windows đang dọn Thùng rác trên tất cả ổ đĩa. Đợi thao tác hoàn tất…";
            try
            {
                var owner = new System.Windows.Interop.WindowInteropHelper(Window).Handle;
                var progress = new Progress<RecycleBinProgress>(update => {
                    if (!busy || !recycleMode) return;
                    Find<TextBlock>("RecycleStatusText").Text = "Đang dọn: " + (update.Deleted + update.Skipped).ToString("N0") + "/" + update.Total.ToString("N0") +
                        " mục · Đã xóa " + update.Deleted.ToString("N0") + " · Bỏ qua " + update.Skipped.ToString("N0") +
                        (update.Current == null ? "" : " · " + update.Current);
                });
                var result = await recycleService.EmptyAsync(approved, logDirectory, item => Window.Dispatcher.Invoke(() =>
                    ConfirmAction("Cho phép xóa mục trong Thùng rác?", "Mục này cần quyền để xóa:\n\n" + item.Name +
                        "\n\nChọn Có để cho phép Windows thử xóa mục này. Windows có thể hỏi quyền quản trị. Chọn Không để giữ mục này và tiếp tục dọn các mục còn lại.")),
                    update => ((IProgress<RecycleBinProgress>)progress).Report(update), owner);
                ShowRecycleResult(result);
                if (result.Error != null) ShowError(result.Error);
            }
            catch (Exception e) { recycleInfo = null; Find<TextBlock>("RecycleStatusText").Text = "Không hoàn tất việc dọn: " + e.Message; }
            finally { SetBusy(false); RefreshDisk(); }
        }
        private void ShowRecycleResult(RecycleBinResult result)
        {
            recycleInfo = result.After;
            string summary = "Đã xóa " + result.Deleted.ToString("N0") + " mục; bỏ qua " + result.Skipped.ToString("N0") + " mục.";
            if (result.Error != null) summary += " Đã dừng: " + result.Error;
            else if (result.Completed) summary += " Thùng rác trên tất cả ổ đĩa đã trống.";
            else if (result.After != null) summary += " Còn " + result.After.Count.ToString("N0") + " mục trong Thùng rác.";
            if (result.LogPath != null) summary += " Nhật ký đã lưu.";
            Find<TextBlock>("RecycleStatusText").Text = summary;
        }
        public void VerifyRecyclePage()
        {
            EnterRecycleMode(); recycleInfo = null; UpdateRecycleInfo();
            if (recycleEmptyButton.IsEnabled) throw new InvalidOperationException("Empty recycle bin enabled before reading its size.");
            recycleInfo = new RecycleBinInfo(0, 0); UpdateRecycleInfo();
            if (recycleEmptyButton.IsEnabled) throw new InvalidOperationException("Empty recycle bin enabled for an empty bin.");
            recycleInfo = new RecycleBinInfo(512L * 1024 * 1024, 27); UpdateRecycleInfo();
            if (!recycleEmptyButton.IsEnabled) throw new InvalidOperationException("Recycle cleanup disabled for a non-empty bin.");
            SetBusy(true, false);
            if (recycleEmptyButton.IsEnabled || recycleRefreshButton.IsEnabled || recycleNavButton.IsEnabled || cancelButton.Visibility == Visibility.Visible)
                throw new InvalidOperationException("Recycle operation did not disable controls or incorrectly offered cancellation.");
            SetBusy(false);
            ShowRecycleResult(new RecycleBinResult { Attempted = true, Finished = true, Deleted = 2, Skipped = 1, After = new RecycleBinInfo(1024, 1) });
            if (busy || Find<ProgressBar>("RecycleProgress").Visibility != Visibility.Collapsed || !recycleEmptyButton.IsEnabled ||
                !Find<TextBlock>("RecycleStatusText").Text.Contains("Đã xóa 2 mục; bỏ qua 1 mục."))
                throw new InvalidOperationException("Skipped recycle items left the UI busy or hid the partial result.");
            Find<TextBlock>("RecycleStatusText").Text = "Dữ liệu mẫu kiểm thử giao diện; chưa đọc hoặc dọn Thùng rác thật.";
            Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
            var content = (FrameworkElement)Window.Content; content.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview-recycle-bin.png"))) encoder.Save(stream);
            VerifyRecyclePermissionFlow(false);
            VerifyRecyclePermissionFlow(true);
        }
        private sealed class RecycleSmokeBackend : IRecycleBinBackend
        {
            public int Deleted, Attempts;
            public RecycleBinInfo Query(string root) { return new RecycleBinInfo((2 - Deleted) * 1024, 2 - Deleted); }
            public IList<RecycleBinItem> Snapshot() { return new[] { new RecycleBinItem("Thư mục mẫu cần quyền xóa"), new RecycleBinItem("File mẫu tiếp theo") }; }
            public RecycleDeleteOutcome Delete(RecycleBinItem item, bool elevation, IntPtr owner) {
                Attempts++;
                if (item.Name.StartsWith("Thư mục") && !elevation) return new RecycleDeleteOutcome(false, true, "Fixture needs permission.");
                Deleted++; return new RecycleDeleteOutcome(true, false, "Fixture only; no real deletion.");
            }
        }
        private void VerifyRecyclePermissionFlow(bool allow)
        {
            var backend = new RecycleSmokeBackend();
            var controller = new MainController(recycleOperations: new RecycleBinService(backend));
            controller.Window.Owner = Window; controller.Window.ShowActivated = false; controller.Window.ShowInTaskbar = false; controller.Window.Opacity = 0;
            int prompts = 0;
            controller.confirmationProbe = (title, message) => {
                bool permission = title.StartsWith("Cho phép"); if (permission) prompts++;
                return controller.ConfirmAction(title, message, null,
                    permission ? (allow ? "preview-recycle-permission-yes.png" : "preview-recycle-permission-no.png") : "preview-recycle-start.png", !permission || allow);
            };
            var previousContext = SynchronizationContext.Current;
            try {
                controller.Window.Show(); controller.EnterRecycleMode(); controller.recycleInfo = backend.Query(null); controller.UpdateRecycleInfo();
                // Smoke runs before Application.Run; emulate the context of a real UI click.
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(controller.Window.Dispatcher));
                var task = controller.EmptyRecycleAsync();
                var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                var deadline = Stopwatch.StartNew();
                timer.Tick += delegate { if (task.IsCompleted || deadline.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
                timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
                if (!task.IsCompleted) throw new InvalidOperationException("Recycle Yes/No flow did not finish without Stop.");
                task.GetAwaiter().GetResult();
                controller.Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
                string expected = allow ? "Đã xóa 2 mục; bỏ qua 0 mục." : "Đã xóa 1 mục; bỏ qua 1 mục.";
                if (prompts != 1 || backend.Attempts != (allow ? 3 : 2) || controller.busy ||
                    controller.Find<ProgressBar>("RecycleProgress").Visibility != Visibility.Collapsed ||
                    !controller.Find<TextBlock>("RecycleStatusText").Text.Contains(expected))
                    throw new InvalidOperationException("Recycle permission response hid the result or left the UI busy.");
            } finally { SynchronizationContext.SetSynchronizationContext(previousContext); controller.Window.Close(); }
        }
        private void ShowGuide()
        {
            var group = groupsGrid.SelectedItem as CleanupGroup;
            if (group == null) { statusText.Text = "Chọn một dòng trong danh sách rồi bấm Cách dọn."; return; }
            if (!RequireGroupAccess(group.Id)) return;
            var dialog = new Window { Owner = Window, Title = "Cách dọn · " + group.Name, Width = 720, Height = 560, MinWidth = 550, MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
            var outer = new DockPanel { Margin = new Thickness(24) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); outer.Children.Add(actions);
            var openFolder = new Button { Content = "Mở thư mục", Padding = new Thickness(12, 8, 12, 8) };
            openFolder.Click += delegate { if (Directory.Exists(group.Root)) Open(group.Root); else ShowError("Thư mục không tồn tại hoặc không truy cập được."); }; actions.Children.Add(openFolder);
            if (!String.IsNullOrEmpty(group.Commands))
            {
                var copy = new Button { Content = "Sao chép lệnh", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(10, 0, 0, 0) };
                copy.Click += delegate { try { Clipboard.SetText(group.Commands); copy.Content = "Đã sao chép"; } catch (Exception e) { ShowError(e.Message); } }; actions.Children.Add(copy);
            }
            if (!String.IsNullOrEmpty(group.DocumentationUrl))
            {
                var docs = new Button { Content = "Tài liệu chính thức ↗", Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(10, 0, 0, 0) };
                docs.Click += delegate { Open(group.DocumentationUrl); }; actions.Children.Add(docs);
            }
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = group.Name, FontSize = 24, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBox { Text = group.Root, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) });
            body.Children.Add(new TextBlock { Text = group.Measured ? "Đã đo " + group.TotalSizeText + " trong " + group.TotalCount.ToString("N0") + " file. " + (group.CanClean ? "Đủ điều kiện dọn: " + group.Count.ToString("N0") + " file (" + group.SizeText + ")." : "Chỉ thống kê; quản lý dữ liệu theo hướng dẫn bên dưới.") : "Chưa đo dung lượng. Các con số không xác định dữ liệu còn cần dùng hay không.", TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = String.IsNullOrEmpty(group.Guidance) ? "Đóng ứng dụng liên quan, quét và xem danh sách trước khi dọn. Mặc định không giới hạn ngày tạo/sửa file. Nếu chọn giữ file mới hơn 1/7/14/30 ngày, file chưa đủ tuổi sẽ được giữ lại. Chỉ file còn nguyên trạng sau khi quét mới được xóa. Xóa vĩnh viễn, không qua Thùng rác." : group.Guidance, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 14), LineHeight = 23 });
            if (!String.IsNullOrEmpty(group.Commands))
            {
                body.Children.Add(new TextBlock { Text = "Lệnh tham khảo · có thể sao chép; nút Chạy lệnh trên dòng sẽ hiển thị lệnh thực tế để xác nhận", FontSize = 12, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBox { Text = group.Commands, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 0), Background = new SolidColorBrush(Color.FromRgb(240, 244, 248)) });
            }
            outer.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dialog.Content = outer; dialog.ShowDialog();
        }
        private string ChooseFvmVersion(CleanupGroup group)
        {
            string directory = Path.Combine(group.Root, "versions");
            if (!Directory.Exists(directory)) throw new IOException("Không có thư mục fvm/versions ở vị trí đang thống kê.");
            if (PathPolicy.HasLinkedAncestor(directory)) throw new IOException("Không chọn SDK qua đường dẫn liên kết.");
            var versions = Directory.EnumerateDirectories(directory).Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0)
                .Select(Path.GetFileName).Where(v => System.Text.RegularExpressions.Regex.IsMatch(v, @"^(?:\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?|stable|beta|dev|master|main)$")).OrderByDescending(v => v).ToList();
            if (versions.Count == 0) throw new IOException("Không tìm thấy SDK tiêu chuẩn. Các bản fork hoặc cache tùy chỉnh cần quản lý bằng FVM.");
            var dialog = new Window { Owner = Window, Title = "Chọn Flutter SDK cần gỡ", Width = 540, Height = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
            var body = new StackPanel { Margin = new Thickness(24) };
            body.Children.Add(new TextBlock { Text = "Chọn một SDK đã cài trong " + directory + ". Kiểm tra .fvmrc và phiên bản global của các dự án trước khi gỡ.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
            var list = new ComboBox { ItemsSource = versions, SelectedIndex = -1, Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 20) }; body.Children.Add(list);
            string selected = null;
            var button = new Button { Content = "Tiếp tục · xem lệnh", IsEnabled = false, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Right };
            list.SelectionChanged += delegate { button.IsEnabled = list.SelectedItem != null; };
            button.Click += delegate { selected = list.SelectedItem as string; dialog.Close(); }; body.Children.Add(button);
            dialog.Content = body; dialog.ShowDialog(); return selected;
        }
        public void VerifyCommandDialogs()
        {
            ConfirmCommand(commandRunner.Prepare("gradle-caches"), true);
            ConfirmCommand(commandRunner.Prepare("codex-data"), true, true);
            var updatePlan = commandRunner.Prepare(WindowsUpdateCache.GroupId);
            if (!updatePlan.Description.Contains(WindowsUpdateCache.Warning)) throw new InvalidOperationException("Windows Update confirmation is missing its warning.");
            ConfirmCommand(updatePlan, true);
            cancellation = new CancellationTokenSource(); SetBusy(true);
            if (!cancelButton.IsEnabled || cancelButton.Visibility != Visibility.Visible) throw new InvalidOperationException("Preflight cancellation is unavailable.");
            RequestCancel();
            if (!cancellation.IsCancellationRequested) throw new InvalidOperationException("Preflight cancellation did not reach the validator.");
            cancellation.Dispose(); cancellation = null;
            SetBusy(true, false);
            if (cancelButton.Visibility != Visibility.Collapsed || cancelButton.IsEnabled || groupsGrid.IsEnabled)
                throw new InvalidOperationException("Windows Update operation must wait for service recovery without cancellation.");
            RequestCancel();
            Window.Close();
            if (!Window.IsVisible || !statusText.Text.Contains("khởi động lại dịch vụ"))
                throw new InvalidOperationException("Window closed before service recovery finished.");
            SetBusy(false);
            ConfirmAction("Xác nhận dọn file tạm", "Xóa vĩnh viễn các file tạm đã chọn?\n\nDữ liệu mẫu kiểm thử; không thực hiện xóa.", null, "preview-confirm-temp.png");
            ConfirmAction("Xác nhận dọn Thùng rác · Tất cả ổ đĩa", "Xóa vĩnh viễn toàn bộ mục trong Thùng rác trên tất cả ổ đĩa?\n\nDữ liệu mẫu kiểm thử; không gọi API xóa.", null, "preview-confirm-recycle.png");
        }
        public void VerifyAdminAccess()
        {
            bool elevated = false; int warnings = 0;
            var controller = new MainController(() => elevated, message => {
                if (message != WindowsUpdateCache.AccessWarning) throw new InvalidOperationException("Missing administrator warning.");
                warnings++;
            });
            controller.Window.Owner = Window; controller.Window.ShowActivated = false;
            controller.Window.ShowInTaskbar = false; controller.Window.Opacity = 0;
            controller.Window.Show(); controller.SetMode(true);
            controller.Window.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
            var groups = ((IEnumerable<CleanupGroup>)controller.groupsGrid.ItemsSource).ToList();
            var locked = groups.Single(g => g.Id == WindowsUpdateCache.GroupId);
            if (!locked.State.Contains("không được truy cập")) throw new InvalidOperationException("Locked row has no administrator notice.");
            var row = (DataGridRow)controller.groupsGrid.ItemContainerGenerator.ContainerFromItem(locked);
            if (row == null) throw new InvalidOperationException("Windows Update row did not render.");
            var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) {
                RoutedEvent = UIElement.PreviewMouseDownEvent, Source = row
            };
            row.RaiseEvent(click);
            if (!click.Handled || warnings != 1 || controller.groupsGrid.SelectedItem != null)
                throw new InvalidOperationException("Non-admin mouse access was not blocked immediately.");
            controller.groupsGrid.SelectedItem = locked;
            if (warnings != 2 || controller.groupsGrid.SelectedItem != null)
                throw new InvalidOperationException("Non-admin keyboard/selection access was not blocked.");
            controller.ScanAsync(locked.Id).GetAwaiter().GetResult();
            controller.RunCommandAsync(locked.Id).GetAwaiter().GetResult();
            if (warnings != 4 || controller.busy || controller.scan != null || locked.Measured)
                throw new InvalidOperationException("Non-admin action reached scanning or command confirmation.");
            elevated = true; controller.groupsGrid.SelectedItem = locked;
            if (controller.groupsGrid.SelectedItem != locked || !controller.RequireGroupAccess(locked.Id) || warnings != 4)
                throw new InvalidOperationException("Administrator could not access Windows Update.");
            elevated = false; controller.ShowGuide();
            if (warnings != 5 || !controller.RequireGroupAccess("gradle-caches") || warnings != 5)
                throw new InvalidOperationException("Guide access bypassed the administrator check or other groups were blocked.");
            controller.groupsGrid.SelectedItem = groups.First(g => g.Id == "gradle-caches");
            if (controller.groupsGrid.SelectedItem == null || warnings != 5) throw new InvalidOperationException("Other groups are incorrectly restricted.");
            controller.Window.Close();
        }
        public void VerifyTempElevation()
        {
            foreach (string scenario in new[] { "no", "uac-no", "failed", "yes", "admin", "busy", "other" })
            {
                int launches = 0, prompts = 0; bool elevated = scenario == "admin";
                var controller = new MainController(() => elevated, elevatedLauncher: info => {
                    launches++;
                    if (!info.UseShellExecute || info.Verb != "runas" || info.FileName != Assembly.GetExecutingAssembly().Location || info.Arguments != "--windows-temp-admin 7" || info.RedirectStandardInput)
                        throw new InvalidOperationException("Elevation is not scoped to relaunching this EXE with the age option.");
                    if (scenario == "uac-no") throw new System.ComponentModel.Win32Exception(1223);
                    return scenario != "failed";
                });
                controller.Window.Owner = Window; controller.Window.ShowActivated = false; controller.Window.ShowInTaskbar = false; controller.Window.Opacity = 0;
                controller.confirmationProbe = (title, message) => {
                    prompts++;
                    if (!message.Contains("Chưa xóa file") || !message.Contains("Chọn Không") || !message.Contains("UAC")) throw new InvalidOperationException("Missing elevation consent explanation.");
                    return controller.ConfirmAction(title, message, null, "preview-temp-elevation-" + scenario + ".png", scenario != "no");
                };
                try {
                    controller.Window.Show(); controller.ConfigureWindowsTemp(7);
                    if (controller.scan != null || ((IEnumerable<CleanupGroup>)controller.groupsGrid.ItemsSource).Any(group => group.Selected != (group.Id == "windows-temp")))
                        throw new InvalidOperationException("Elevation startup reused candidates or selected other users' temp data.");
                    if (controller.Find<StackPanel>("TempAdminNotice").Visibility != (elevated ? Visibility.Collapsed : Visibility.Visible))
                        throw new InvalidOperationException("Temp administrator notice does not reflect current rights.");
                    if (scenario == "busy") controller.SetBusy(true);
                    if (scenario == "other") controller.SetMode(true);
                    bool opened = controller.RequestTempElevation();
                    bool blocked = scenario == "admin" || scenario == "busy" || scenario == "other";
                    if (opened != (scenario == "yes") || prompts != (blocked ? 0 : 1) || launches != (blocked || scenario == "no" ? 0 : 1) ||
                        controller.Window.IsVisible != (scenario != "yes") || controller.scan != null)
                        throw new InvalidOperationException("Elevation consent/cancel/relaunch flow failed: " + scenario);
                    if (scenario == "uac-no" && !controller.statusText.Text.Contains("chưa cấp quyền")) throw new InvalidOperationException("UAC refusal was not handled.");
                    if (scenario == "busy") controller.SetBusy(false);
                } finally { controller.Window.Close(); }
            }
            foreach (int expected in new[] { 0, 1, 7, 14, 30 }) {
                int parsed;
                if (!TempElevation.TryReadArguments(new[] { "--windows-temp-admin", expected.ToString() }, out parsed) || parsed != expected)
                    throw new InvalidOperationException("Elevation startup lost retention setting.");
            }
            foreach (string[] invalid in new[] {
                new[] { "--windows-temp-admin" }, new[] { "--windows-temp-admin", "-1" }, new[] { "--windows-temp-admin", "365" },
                new[] { "--windows-temp-admin", "0", "delete" }, new[] { "--windows-temp-admin", "C:\\Windows" }
            }) {
                bool rejected = false; try { int parsed; TempElevation.TryReadArguments(invalid, out parsed); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new InvalidOperationException("Elevation accepted unexpected startup arguments.");
            }
        }
        private bool ConfirmCommand(CommandPlan plan, bool preview = false, bool previewAccept = false)
        {
            bool update = plan.Action == CommandAction.WindowsUpdateDownloads;
            string message = "Đích: " + plan.Target + "\n\n" + plan.Description +
                "\n\nXóa vĩnh viễn, không qua Thùng rác. Lựa chọn thời gian giữ file không áp dụng cho lệnh này." +
                (update ? "" : " Dừng giữa chừng không khôi phục được phần đã xóa.") + "\n\nBạn có muốn chạy lệnh này?";
            return ConfirmAction(update ? "Xác nhận dọn cache Windows Update" : "Xác nhận chạy lệnh", message, plan.DisplayCommand, preview ? "preview-confirm-" + plan.GroupId + ".png" : null, previewAccept);
        }
        private bool ConfirmAction(string title, string message, string command = null, string previewFile = null, bool previewAccept = false)
        {
            if (previewFile == null && confirmationProbe != null) return confirmationProbe(title, message);
            var dialog = new Window { Owner = Window, Title = title, Width = 760, Height = 560, MinWidth = 600, MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Background = Brushes.White };
            bool confirmed = false;
            var outer = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); outer.Children.Add(actions);
            var no = new Button { Content = "Không", IsCancel = true, IsDefault = true, MinWidth = 95, Padding = new Thickness(16, 10, 16, 10) }; actions.Children.Add(no);
            var yes = new Button { Content = "Có", MinWidth = 95, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(16, 10, 16, 10) }; actions.Children.Add(yes);
            no.Click += delegate { dialog.Close(); };
            yes.Click += delegate { confirmed = true; dialog.Close(); };
            dialog.Loaded += delegate { no.Focus(); };
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 23, Margin = new Thickness(0, 0, 0, 16) });
            if (!String.IsNullOrEmpty(command))
                body.Children.Add(new TextBox { Text = command, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(240, 244, 248)) });
            outer.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            dialog.Content = new Border { Background = Brushes.White, Padding = new Thickness(24), Child = outer };
            if (previewFile != null)
            {
                // Verify the shared Yes/No dialog and its result without executing the caller's operation.
                dialog.ShowActivated = false; dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show();
                dialog.Dispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(delegate { }));
                if (confirmed || !yes.IsEnabled || !no.IsEnabled || !no.IsDefault || !no.IsCancel || yes.IsDefault)
                    throw new InvalidOperationException("Yes/No confirmation has incorrect defaults.");
                if (VisualChild<CheckBox>(dialog) != null) throw new InvalidOperationException("Confirmation still requires a checkbox.");
                var text = VisualChild<TextBox>(dialog);
                if (text != null && !text.IsReadOnly) throw new InvalidOperationException("Confirmation still requires text input.");
                var content = (FrameworkElement)dialog.Content; content.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, previewFile))) encoder.Save(stream);
                (previewAccept ? yes : no).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (confirmed != previewAccept) throw new InvalidOperationException("Confirmation did not return the selected Yes/No result.");
                return confirmed;
            }
            dialog.ShowDialog(); return confirmed;
        }
        private async Task RunCommandAsync(string groupId)
        {
            if (busy || !developerMode) return;
            var group = ((IEnumerable<CleanupGroup>)groupsGrid.ItemsSource).FirstOrDefault(g => g.Id == groupId);
            if (group == null || !group.CanRunCommand) return;
            if (!RequireGroupAccess(groupId)) return;
            CommandPlan plan;
            try
            {
                string version = null;
                if (CommandRunner.ActionFor(groupId) == CommandAction.FvmVersion) { version = ChooseFvmVersion(group); if (version == null) return; }
                plan = commandRunner.Prepare(groupId, version);
                if (plan.Action == CommandAction.OpenEditor)
                {
                    string binary = Path.Combine(Path.GetDirectoryName(plan.Executable), "..", groupId == "vscode-extensions" ? "Code.exe" : "Cursor.exe");
                    if (!File.Exists(binary)) throw new IOException("Không tìm thấy file ứng dụng cạnh CLI. Mở trình soạn thảo rồi nhấn Ctrl+Shift+X.");
                    Process.Start(new ProcessStartInfo(Path.GetFullPath(binary)) { UseShellExecute = true });
                    statusText.Text = "Đã mở trình soạn thảo. Nhấn Ctrl+Shift+X, chọn extension rồi Uninstall."; return;
                }
                if (!ConfirmCommand(plan)) return;
            }
            catch (Exception e) { ShowError(e.Message); return; }
            cancellation = new CancellationTokenSource(); var operation = cancellation; SetBusy(true);
            statusText.Text = "Đang chạy lệnh · " + group.Name + "…";
            var progress = new Progress<string>(line => {
                if (busy && Object.ReferenceEquals(cancellation, operation) && !operation.IsCancellationRequested)
                    statusText.Text = "Đang chạy " + group.Name + " · " + (line.Length > 160 ? line.Substring(0, 160) + "…" : line).Replace("\r", " ").Replace("\n", " ");
            });
            CommandResult result = null;
            try
            {
                result = await Task.Run(() => commandRunner.Run(plan, logDirectory, operation.Token, line => ((IProgress<string>)progress).Report(line),
                    () => Window.Dispatcher.Invoke(new Action(() => SetBusy(true, false)))));
                statusText.Text = result.Canceled ? (result.Started ? "Đã dừng cây tiến trình. Dữ liệu đã xóa không được khôi phục; hãy quét lại." : "Đã dừng kiểm tra thư mục; chưa chạy lệnh hoặc xóa dữ liệu.") : result.Error != null ? "Lệnh chưa hoàn tất: " + result.Error : (result.Summary ?? "Lệnh hoàn tất (mã 0).") + " Quét lại thư mục để cập nhật dung lượng.";
            }
            catch (Exception e) { statusText.Text = "Lệnh gặp lỗi: " + e.Message; ShowError(e.Message); }
            finally
            {
                // Any command may have changed files even on error/cancellation; never keep deletion snapshots.
                scan = null; ResetGroups();
                var refreshed = ((IEnumerable<CleanupGroup>)groupsGrid.ItemsSource).FirstOrDefault(g => g.Id == groupId);
                if (refreshed != null) { refreshed.State = "Đã chạy lệnh · cần quét lại"; groupsGrid.SelectedItem = refreshed; }
                SetBusy(false); cancellation.Dispose(); cancellation = null; RefreshDisk();
            }
            if (result != null) ShowCommandResult(result);
        }
        private void ShowCommandResult(CommandResult result)
        {
            var dialog = new Window { Owner = Window, Title = "Kết quả chạy lệnh", Width = 860, Height = 620, MinWidth = 600, MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            var outer = new DockPanel { Margin = new Thickness(20) };
            var header = new TextBlock { Text = (result.Canceled ? "Đã dừng" : result.Error ?? result.Summary ?? "Hoàn tất · mã thoát " + result.ExitCode) + "\nNhật ký: " + (result.LogPath ?? "Chưa tạo nhật ký; lệnh chưa bắt đầu."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) }; DockPanel.SetDock(header, Dock.Top); outer.Children.Add(header);
            outer.Children.Add(new TextBox { Text = result.Output ?? "", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
            dialog.Content = outer; dialog.ShowDialog();
        }
        private void ShowPreview()
        {
            var files = SelectedFiles().OrderByDescending(f => f.Length).Take(1000).ToList();
            var preview = new Window { Owner = Window, Title = "Danh sách file dự kiến dọn", Width = 1000, Height = 600, MinWidth = 700, MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 13 };
            var panel = new DockPanel { Margin = new Thickness(20) };
            var note = new TextBlock { Text = "Hiển thị tối đa 1.000 file lớn nhất trong các nhóm đã chọn. Tổng đã chọn: " + SelectedFiles().Count.ToString("N0") + " file.\nChỉ xem trước; chưa xóa dữ liệu.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(note, Dock.Top); panel.Children.Add(note);
            var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, ItemsSource = files };
            grid.Columns.Add(new DataGridTextColumn { Header = "Đường dẫn", Binding = new System.Windows.Data.Binding("Path"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            grid.Columns.Add(new DataGridTextColumn { Header = "Dung lượng", Binding = new System.Windows.Data.Binding("SizeText"), Width = 105 });
            grid.Columns.Add(new DataGridTextColumn { Header = "Sửa lần cuối", Binding = new System.Windows.Data.Binding("ModifiedText"), Width = 145 });
            panel.Children.Add(grid); preview.Content = panel; preview.ShowDialog();
        }
        private void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { ShowError("Không mở được: " + e.Message); }
        }
        private void ShowError(string message) { MessageBox.Show(Window, message, "Dọn dẹp ổ cứng", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
}
