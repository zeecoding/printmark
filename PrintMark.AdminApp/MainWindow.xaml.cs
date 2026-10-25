using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PrintMark.Core;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace PrintMark.AdminApp;

public partial class MainWindow : Window
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private WatermarkConfig _policy = new();
    private bool _loading;
    private bool _scanning;
    private bool _updatingNavigation;
    private readonly WatermarkPresetStore _presets = new();

    public MainWindow()
    {
        InitializeComponent();
        foreach (var combo in new[] { WatermarkTextCombo, FontCombo, PlacementCombo, ContentModeCombo, PaperCombo, OrientationCombo })
            combo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(UpdatePreview));
        WatermarkTextCombo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdatePreview()));
        foreach (var input in new[] { ColorInput, BannerInput }) input.TextChanged += (_, _) => UpdatePreview();
        foreach (var slider in new[] { FontSizeSlider, OpacitySlider, AngleSlider }) slider.ValueChanged += (_, _) => UpdatePreview();
        foreach (var check in new[] { EnabledCheck, BannerCheck, FooterCheck, QrCheck })
        { check.Checked += (_, _) => UpdatePreview(); check.Unchecked += (_, _) => UpdatePreview(); }
        Loaded += async (_, _) => { LoadSavedWatermarks(); UpdatePreview(); await LoadCurrentWatermarkPolicyAsync(); };
        Closed += (_, _) => _http.Dispose();
    }

    private void LoadSavedWatermarks()
    {
        try { SavedWatermarkCombo.ItemsSource = _presets.Load(); }
        catch (Exception ex) { StatusText.Text = $"Cannot read saved watermarks: {ex.Message}"; }
    }
    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        if (!ValidColor) { StatusText.Text = "Correct the hex color before saving your watermark."; return; }
        if (string.IsNullOrWhiteSpace(WatermarkTextCombo.Text) && ContentModeCombo.SelectedIndex != 1)
        { StatusText.Text = "Enter your watermark text before saving."; return; }
        try
        {
            var preset = new SavedWatermark(PresetNameInput.Text.Trim(), CurrentConfig(), PaperCombo.SelectedIndex, OrientationCombo.SelectedIndex);
            var saved = _presets.Save(preset);
            SavedWatermarkCombo.ItemsSource = saved;
            SavedWatermarkCombo.SelectedItem = saved.First(p => p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase));
            StatusText.Text = $"Watermark '{preset.Name}' saved on this computer. Use Apply watermark policy to publish it to the fleet.";
        }
        catch (Exception ex) { StatusText.Text = $"Watermark was not saved: {ex.Message}"; }
    }
    private void OnSavedWatermarkSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SavedWatermarkCombo.SelectedItem is not SavedWatermark preset) return;
        // Clone the saved policy so edits remain a draft until the user saves again.
        Populate(JsonSerializer.Deserialize<WatermarkConfig>(JsonSerializer.Serialize(preset.Config))!);
        PaperCombo.SelectedIndex = Math.Clamp(preset.PaperIndex, 0, 1);
        OrientationCombo.SelectedIndex = Math.Clamp(preset.OrientationIndex, 0, 1);
        PresetNameInput.Text = preset.Name;
        UpdatePreview();
        StatusText.Text = $"Loaded '{preset.Name}' into the editor. Changes have not been applied to the fleet.";
    }
    private string BaseUrl => ServerUrlInput.Text.Trim().TrimEnd('/');
    private bool ValidColor => Regex.IsMatch(ColorInput.Text, "^#[0-9a-fA-F]{6}$");
    private WatermarkConfig CurrentConfig()
    {
        // Preserve settings outside this editor, including image size and tile spacing.
        var cfg = JsonSerializer.Deserialize<WatermarkConfig>(JsonSerializer.Serialize(_policy))!;
        cfg.Enabled = EnabledCheck.IsChecked == true;
        cfg.TextTemplate = WatermarkTextCombo.Text;
        cfg.PrimaryPlacement = (WatermarkPlacement)Math.Max(0, PlacementCombo.SelectedIndex);
        cfg.ContentMode = (WatermarkContentMode)Math.Max(0, ContentModeCombo.SelectedIndex);
        cfg.FontFamily = FontCombo.Text;
        cfg.FontSize = FontSizeSlider.Value;
        cfg.Opacity = OpacitySlider.Value;
        cfg.DirectionAngle = AngleSlider.Value;
        cfg.HexColor = ValidColor ? ColorInput.Text : _policy.HexColor;
        cfg.ShowTopBanner = BannerCheck.IsChecked == true;
        cfg.TopBannerText = BannerInput.Text;
        cfg.ShowBottomAuditFooter = FooterCheck.IsChecked == true;
        cfg.ShowQrCode = QrCheck.IsChecked == true;
        return cfg;
    }

    private void UpdatePreview()
    {
        if (_loading || Preview == null) return;
        FontSizeLabel.Text = $"Font size · {FontSizeSlider.Value:0} pt";
        OpacityLabel.Text = $"Watermark opacity · {OpacitySlider.Value:P0}";
        AngleLabel.Text = PlacementCombo.SelectedIndex == 1 ? "Rotation · 0° (horizontal placement)" : $"Rotation · {AngleSlider.Value:0}°";
        AngleSlider.IsEnabled = PlacementCombo.SelectedIndex != 1;
        ColorError.Text = ValidColor ? "" : "Enter a six-digit hex color, for example #DC2626.";
        SaveButton.IsEnabled = ValidColor;
        var landscape = OrientationCombo.SelectedIndex == 1;
        var width = PaperCombo.SelectedIndex == 1 ? 612.0 : 595.28;
        var height = PaperCombo.SelectedIndex == 1 ? 792.0 : 841.89;
        Preview.Width = landscape ? height : width;
        Preview.Height = landscape ? width : height;
        Preview.Config = CurrentConfig();
        Preview.InvalidateVisual();
    }

    private void Populate(WatermarkConfig cfg)
    {
        _loading = true;
        _policy = cfg;
        EnabledCheck.IsChecked = cfg.Enabled;
        WatermarkTextCombo.Text = cfg.TextTemplate;
        PlacementCombo.SelectedIndex = (int)cfg.PrimaryPlacement;
        ContentModeCombo.SelectedIndex = (int)cfg.ContentMode;
        FontCombo.Text = cfg.FontFamily;
        FontSizeSlider.Value = cfg.FontSize;
        OpacitySlider.Value = cfg.Opacity;
        AngleSlider.Value = cfg.DirectionAngle;
        ColorInput.Text = cfg.HexColor;
        BannerInput.Text = cfg.TopBannerText;
        BannerCheck.IsChecked = cfg.ShowTopBanner;
        FooterCheck.IsChecked = cfg.ShowBottomAuditFooter;
        QrCheck.IsChecked = cfg.ShowQrCode;
        ImageStatus.Text = !string.IsNullOrWhiteSpace(cfg.ImageBase64) ? "Policy logo loaded" : cfg.ImagePath ?? "No logo selected";
        _loading = false;
        UpdatePreview();
    }

    private void OnSelectImageClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            WatermarkPreview.DecodeImage(bytes); // Validate before replacing the current logo.
            _policy.ImageBase64 = Convert.ToBase64String(bytes);
            _policy.ImagePath = null;
            ImageStatus.Text = Path.GetFileName(dlg.FileName);
            ContentModeCombo.SelectedIndex = 2;
            UpdatePreview();
        }
        catch (Exception ex) { StatusText.Text = $"Cannot load logo: {ex.Message}"; }
    }
    private void OnRemoveImage(object sender, RoutedEventArgs e)
    { _policy.ImageBase64 = null; _policy.ImagePath = null; ImageStatus.Text = "No logo selected"; UpdatePreview(); }
    private void OnInsertVariable(object sender, RoutedEventArgs e)
    { WatermarkTextCombo.Text += " | " + ((Button)sender).Tag; UpdatePreview(); }
    private void OnReset(object sender, RoutedEventArgs e) => Populate(new WatermarkConfig());
    private void OnHome(object sender, RoutedEventArgs e) => ShowPanel(HomePanel);
    private void OnWatermark(object sender, RoutedEventArgs e) => ShowPanel(DesignerPanel);
    private async void OnFleet(object sender, RoutedEventArgs e) { ShowPanel(FleetPanel); await LoadFleetAsync(); }
    private async void OnDeployment(object sender, RoutedEventArgs e) { ShowPanel(DeploymentPanel); await LoadDomainComputersAsync(); }
    private void ShowPanel(UIElement panel)
    {
        foreach (var p in new UIElement[] { HomePanel, DesignerPanel, FleetPanel, DeploymentPanel })
            if (p != null) p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
        _updatingNavigation = true;
        try
        {
            HomeNav.IsChecked = panel == HomePanel;
            WatermarkNav.IsChecked = panel == DesignerPanel;
            FleetNav.IsChecked = panel == FleetPanel;
            DeploymentNav.IsChecked = panel == DeploymentPanel;
        }
        finally { _updatingNavigation = false; }
    }

    private async void OnNavigationChecked(object sender, RoutedEventArgs e)
    {
        // The initial checked event can occur while XAML is still creating the panels.
        if (_updatingNavigation || HomePanel == null || DesignerPanel == null || FleetPanel == null || DeploymentPanel == null) return;
        var target = ((RadioButton)sender).Tag?.ToString();
        ShowPanel(target == "Home" ? HomePanel : target == "Fleet" ? FleetPanel : target == "Deployment" ? DeploymentPanel : DesignerPanel);
        if (target == "Fleet") await LoadFleetAsync();
        else if (target == "Deployment") await LoadDomainComputersAsync();
    }
    private async void OnConnect(object sender, RoutedEventArgs e) => await LoadCurrentWatermarkPolicyAsync();

    private async void OnSaveWatermarkClicked(object sender, RoutedEventArgs e)
    {
        if (!ValidColor) return;
        SaveButton.IsEnabled = false;
        try
        {
            var cfg = CurrentConfig();
            using var response = await _http.PostAsJsonAsync($"{BaseUrl}/api/watermark/config", cfg);
            response.EnsureSuccessStatusCode();
            _policy = cfg;
            StatusText.Text = $"Policy applied successfully at {DateTime.Now:T}.";
            ConnectionText.Text = "● Connected";
        }
        catch (Exception ex) { StatusText.Text = $"Policy was not saved: {ex.Message}"; }
        finally { SaveButton.IsEnabled = ValidColor; }
    }

    private async Task LoadCurrentWatermarkPolicyAsync()
    {
        try
        {
            var cfg = await _http.GetFromJsonAsync<WatermarkConfig>($"{BaseUrl}/api/watermark/config");
            if (cfg == null) throw new InvalidOperationException("Server returned an empty policy.");
            Populate(cfg);
            ConnectionText.Text = "● Connected";
            StatusText.Text = "Current server policy loaded. Preview changes locally, then apply when ready.";
        }
        catch (Exception ex)
        { ConnectionText.Text = "○ Offline"; StatusText.Text = $"Unable to load policy: {ex.Message}. You can still preview and export locally."; }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadFleetAsync();
    private async Task LoadFleetAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            StatusText.Text = "Discovering printer queues and network devices…";
            var subnet = string.IsNullOrWhiteSpace(SubnetInput.Text) ? "auto" : SubnetInput.Text.Trim();
            var response = await _http.GetFromJsonAsync<FleetResponse>($"{BaseUrl}/api/fleet/printers?subnet={Uri.EscapeDataString(subnet)}");
            if (response == null) return;
            SubnetInput.Text = response.Subnet;
            PrintersGrid.ItemsSource = response.Printers;
            TotalCountText.Text = response.Printers.Count.ToString();
            DirectCountText.Text = response.Printers.Count(p => p.Capability.Contains("Direct", StringComparison.OrdinalIgnoreCase)).ToString();
            DriverQueueCountText.Text = response.Printers.Count(p => p.Capability.Contains("Driver", StringComparison.OrdinalIgnoreCase)).ToString();
            StatusText.Text = $"Fleet scan complete · {response.Printers.Count} devices · {DateTime.Now:T}";
        }
        catch (Exception ex) { StatusText.Text = $"Fleet scan failed: {ex.Message}"; }
        finally { _scanning = false; }
    }

    private void OnExportSample(object sender, RoutedEventArgs e)
    {
        if (!ValidColor) { StatusText.Text = "Correct the hex color before exporting."; return; }
        var dlg = new SaveFileDialog { Filter = "PDF document|*.pdf", FileName = "PrintMark-watermark-sample.pdf" };
        if (dlg.ShowDialog() != true) return;
        try { ExportSampleToFile(dlg.FileName); StatusText.Text = $"Sample PDF exported using the print engine: {dlg.FileName}"; }
        catch (Exception ex) { StatusText.Text = $"PDF export failed: {ex.Message}"; }
    }

    internal void ExportSampleToFile(string targetPath)
    {
        var source = Path.Combine(Path.GetTempPath(), $"printmark-sample-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var doc = new PdfDocument())
            {
                var page = doc.AddPage();
                page.Width = Preview.Width; page.Height = Preview.Height;
                using (var gfx = XGraphics.FromPdfPage(page))
                {
                    var title = new XFont("Arial", 23, XFontStyle.Bold);
                    var body = new XFont("Arial", 11);
                    gfx.DrawString("PRINTMARK / SAMPLE DOCUMENT", new XFont("Arial", 10, XFontStyle.Bold), XBrushes.SlateGray, 48, 65);
                    gfx.DrawString("Financial summary", title, XBrushes.Black, 48, 120);
                    gfx.DrawString("Sample content for reviewing your watermark policy.", body, XBrushes.Gray, 48, 148);
                    for (int i = 0; i < 14; i++) gfx.DrawLine(XPens.LightGray, 48, 190 + i * 24, page.Width - 48, 190 + i * 24);
                }
                doc.Save(source);
            }
            WatermarkEngine.ApplyWatermark(source, targetPath, WatermarkPreview.SampleMetadata, CurrentConfig());

        }
        finally { if (File.Exists(source)) File.Delete(source); }
    }

    public record FleetResponse(string Subnet, int Count, List<PrinterItem> Printers);
    public record PrinterItem(string Name, string IpAddress, int Port, string Driver, string Vendor, string Capability, bool IsOnline);
    public record DiscoveredComputer(string Name, string DnsHostName, string Description);

    private async void OnDiscoverComputersClicked(object sender, RoutedEventArgs e) => await LoadDomainComputersAsync();

    private async Task LoadDomainComputersAsync()
    {
        StatusText.Text = "Discovering Active Directory domain computers…";
        AdDiscoveryStatus.Text = "Querying Active Directory for domain computers…";
        try
        {
            List<DiscoveredComputer> computers = await Task.Run(() =>
            {
                var list = new List<DiscoveredComputer>();
                using var ctx = new PrincipalContext(ContextType.Domain);
                using var searcher = new PrincipalSearcher(new ComputerPrincipal(ctx));
                foreach (var result in searcher.FindAll())
                {
                    if (result is ComputerPrincipal comp)
                    {
                        string name = comp.Name ?? comp.SamAccountName?.TrimEnd('$') ?? "";
                        string dns = name;
                        try
                        {
                            if (comp.GetUnderlyingObject() is DirectoryEntry de)
                            {
                                string? d = de.Properties["dNSHostName"].Value?.ToString();
                                if (!string.IsNullOrWhiteSpace(d)) dns = d;
                            }
                        }
                        catch { }

                        string desc = comp.Description ?? "Domain Workstation";
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            list.Add(new DiscoveredComputer(name, dns, desc));
                        }
                    }
                }
                return list.OrderBy(c => c.Name).ToList();
            });

            ComputerListView.ItemsSource = computers;
            if (computers.Count > 0)
            {
                AdDiscoveryStatus.Text = $"Discovered {computers.Count} domain computer(s).";
                StatusText.Text = $"Active Directory: {computers.Count} computer(s) found at {DateTime.Now:T}";
                if (ComputerListView.SelectedIndex < 0) ComputerListView.SelectedIndex = 0;
            }
            else
            {
                AdDiscoveryStatus.Text = "No domain computers found. Enter target manually.";
                StatusText.Text = "Active Directory query returned 0 computers.";
            }
        }
        catch (Exception ex)
        {
            ComputerListView.ItemsSource = new List<DiscoveredComputer>();
            AdDiscoveryStatus.Text = "Active Directory discovery unavailable (Workgroup/Offline). Enter target computer manually.";
            StatusText.Text = $"AD Discovery: {ex.Message}. Manual entry available.";
        }
    }

    private void OnComputerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComputerListView.SelectedItem is DiscoveredComputer comp)
        {
            TargetComputerInput.Text = !string.IsNullOrWhiteSpace(comp.DnsHostName) ? comp.DnsHostName : comp.Name;
        }
    }

    private void OnBrowseInstallerClicked(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Installer Executable (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = "PrintMark-Enterprise-Setup.exe"
        };
        if (dlg.ShowDialog() == true)
        {
            InstallerPathInput.Text = dlg.FileName;
        }
    }

    private async void OnDeployClientClicked(object sender, RoutedEventArgs e)
    {
        string target = TargetComputerInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            DeploymentFeedbackText.Text = "Please enter or select a target workstation.";
            TargetComputerInput.Focus();
            return;
        }

        string rawInstaller = InstallerPathInput.Text.Trim();
        string installerPath = ResolveInstallerPath(rawInstaller);
        if (!File.Exists(installerPath))
        {
            DeploymentFeedbackText.Text = $"Installer file not found at '{installerPath}'. Please verify the path.";
            return;
        }

        DeployButton.IsEnabled = false;
        DeploymentProgressBar.Visibility = Visibility.Visible;
        DeploymentFeedbackText.Text = $"Starting remote deployment to {target}…";
        StatusText.Text = $"Deploying to {target}…";

        string adminUser = AdminUserInput.Text.Trim();
        string adminPassword = AdminPasswordInput.Password;

        try
        {
            // a) Copy installer to remote admin share \\{target}\c$\Windows\Temp\PrintMark-Setup.exe
            string remoteSharePath = $@"\\{target}\c$\Windows\Temp\PrintMark-Setup.exe";
            DeploymentFeedbackText.Text = $"Copying installer to \\\\{target}\\c$\\Windows\\Temp\\PrintMark-Setup.exe…";

            await Task.Run(() =>
            {
                File.Copy(installerPath, remoteSharePath, overwrite: true);
            });

            // b & c) Execute installer via WMI Win32_Process silently
            DeploymentFeedbackText.Text = $"Executing installer on {target} via WMI Win32_Process…";
            string wmiCommandLine = @"C:\Windows\Temp\PrintMark-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /COMPONENTS=""client""";

            var (returnCode, processId) = await Task.Run(() =>
            {
                var scope = new ManagementScope($@"\\{target}\root\cimv2");
                if (!string.IsNullOrWhiteSpace(adminUser) && !string.IsNullOrWhiteSpace(adminPassword))
                {
                    scope.Options.Username = adminUser;
                    scope.Options.Password = adminPassword;
                    scope.Options.Impersonation = ImpersonationLevel.Impersonate;
                    scope.Options.EnablePrivileges = true;
                }
                scope.Connect();

                using var processClass = new ManagementClass(scope, new ManagementPath("Win32_Process"), new ObjectGetOptions());
                using var inParams = processClass.GetMethodParameters("Create");
                inParams["CommandLine"] = wmiCommandLine;

                using var outParams = processClass.InvokeMethod("Create", inParams, new InvokeMethodOptions());
                uint rc = (uint)(outParams["ReturnValue"] ?? 1);
                uint pid = (uint)(outParams["ProcessId"] ?? 0);
                return (rc, pid);
            });

            if (returnCode == 0)
            {
                DeploymentFeedbackText.Text = $"✓ Success: Client deployment initiated on {target} (WMI Process ID: {processId}).";
                StatusText.Text = $"Deployment successful on {target} (PID: {processId}) at {DateTime.Now:T}";
            }
            else
            {
                string errorDesc = returnCode switch
                {
                    2 => "Access Denied (insufficient administrative privileges)",
                    3 => "Insufficient Privilege",
                    8 => "Unknown Failure",
                    9 => "Path Not Found",
                    21 => "Invalid Parameter",
                    _ => $"Error code {returnCode}"
                };
                DeploymentFeedbackText.Text = $"✗ Failed: WMI process creation returned error: {errorDesc}.";
                StatusText.Text = $"Deployment failed on {target}: {errorDesc}";
            }
        }
        catch (Exception ex)
        {
            DeploymentFeedbackText.Text = $"✗ Failed: {ex.Message}";
            StatusText.Text = $"Deployment error on {target}: {ex.Message}";
        }
        finally
        {
            DeploymentProgressBar.Visibility = Visibility.Collapsed;
            DeployButton.IsEnabled = true;
        }
    }

    private static string ResolveInstallerPath(string candidatePath)
    {
        if (File.Exists(candidatePath)) return candidatePath;

        string baseDir = AppContext.BaseDirectory;
        string direct = Path.Combine(baseDir, candidatePath);
        if (File.Exists(direct)) return direct;

        string setupExe = Path.Combine(baseDir, "PrintMark-Enterprise-Setup.exe");
        if (File.Exists(setupExe)) return setupExe;

        string parentSetup = Path.GetFullPath(Path.Combine(baseDir, "..", "PrintMark-Enterprise-Setup.exe"));
        if (File.Exists(parentSetup)) return parentSetup;

        return candidatePath;
    }
}




