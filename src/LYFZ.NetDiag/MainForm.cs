using System.Diagnostics;
using LYFZ.NetDiag.Diagnostics;

namespace LYFZ.NetDiag;

internal sealed class MainForm : Form
{
    private readonly TextBox _storeNameTextBox = new();
    private readonly ComboBox _carrierComboBox = new();
    private readonly ComboBox _modeComboBox = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _openReportButton = new();
    private readonly Button _openFolderButton = new();
    private readonly ProgressBar _progressBar = new();
    private readonly Label _statusLabel = new();
    private readonly RichTextBox _outputTextBox = new();
    private CancellationTokenSource? _cancellation;
    private string? _lastLogPath;
    private string? _lastHtmlReportPath;
    private bool _closeAfterCancellation;

    public MainForm()
    {
        Text = "利亚方舟海螺云网络诊断工具";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(820, 620);
        Size = new Size(940, 700);
        Font = new Font("Microsoft YaHei UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildLayout();
    }

    private void BuildLayout()
    {
        var headerPanel = new Panel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(18, 14, 18, 8) };
        var title = new Label
        {
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
            Text = "利亚方舟海螺云网络诊断工具",
            Location = new Point(18, 14)
        };
        var instructions = new Label
        {
            AutoSize = false,
            Location = new Point(20, 52),
            Size = new Size(870, 44),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "请在系统无法访问时立即运行，检测前不要清理DNS、重启路由器或切换网络。连续监测会记录故障、恢复和DNS节点变化，可随时停止并保留已有日志。"
        };

        var storeLabel = new Label { Text = "门店名称（选填）：", AutoSize = true, Location = new Point(20, 110) };
        _storeNameTextBox.Location = new Point(150, 106);
        _storeNameTextBox.Size = new Size(260, 27);
        _storeNameTextBox.MaxLength = 80;
        _storeNameTextBox.PlaceholderText = "例如：三亚××店";

        var carrierLabel = new Label { Text = "宽带运营商：", AutoSize = true, Location = new Point(445, 110) };
        _carrierComboBox.Location = new Point(545, 106);
        _carrierComboBox.Size = new Size(180, 27);
        _carrierComboBox.DropDownStyle = ComboBoxStyle.DropDown;
        _carrierComboBox.Items.AddRange(["未知", "中国电信", "中国联通", "中国移动", "广电网络", "其他"]);
        _carrierComboBox.SelectedIndex = 0;

        var modeLabel = new Label { Text = "检测模式：", AutoSize = true, Location = new Point(20, 151) };
        _modeComboBox.Location = new Point(150, 146);
        _modeComboBox.Size = new Size(260, 27);
        _modeComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _modeComboBox.Items.AddRange([
            "快速诊断一次",
             "连续检测1分钟(推荐)",
            "连续检测5分钟",
            "连续检测10分钟"
        ]);
        _modeComboBox.SelectedIndex = 0;

        var privacy = new Label
        {
            AutoSize = false,
            Location = new Point(20, 188),
            Size = new Size(870, 36),
            ForeColor = Color.DimGray,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = "日志包含设备名、内网IP、DNS、网关和公网出口IP，用于定位故障；不采集账号、密码、Cookie或业务内容。"
        };

        headerPanel.Controls.AddRange([
            title, instructions, storeLabel, _storeNameTextBox, carrierLabel, _carrierComboBox,
            modeLabel, _modeComboBox, privacy
        ]);

        _outputTextBox.Dock = DockStyle.Fill;
        _outputTextBox.ReadOnly = true;
        _outputTextBox.BackColor = Color.FromArgb(24, 26, 31);
        _outputTextBox.ForeColor = Color.Gainsboro;
        _outputTextBox.Font = new Font("Consolas", 10F);
        _outputTextBox.BorderStyle = BorderStyle.FixedSingle;
        _outputTextBox.Text = "等待开始检测……\r\n";

        var footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(18, 10, 18, 12) };
        _startButton.Text = "开始检测";
        _startButton.Size = new Size(120, 36);
        _startButton.Location = new Point(18, 12);
        _startButton.Click += StartButton_Click;

        _stopButton.Text = "停止检测";
        _stopButton.Size = new Size(100, 36);
        _stopButton.Location = new Point(148, 12);
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => _cancellation?.Cancel();

        _openReportButton.Text = "打开分析报告";
        _openReportButton.Size = new Size(130, 36);
        _openReportButton.Location = new Point(258, 12);
        _openReportButton.Enabled = false;
        _openReportButton.Click += OpenReportButton_Click;

        _openFolderButton.Text = "打开日志文件夹";
        _openFolderButton.Size = new Size(140, 36);
        _openFolderButton.Location = new Point(398, 12);
        _openFolderButton.Enabled = false;
        _openFolderButton.Click += OpenFolderButton_Click;

        _progressBar.Location = new Point(550, 15);
        _progressBar.Size = new Size(355, 28);
        _progressBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _progressBar.Minimum = 0;
        _progressBar.Maximum = DomainCatalog.All.Count;

        _statusLabel.Location = new Point(20, 60);
        _statusLabel.Size = new Size(880, 30);
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.Text = "准备就绪";

        footerPanel.Controls.AddRange([_startButton, _stopButton, _openReportButton, _openFolderButton, _progressBar, _statusLabel]);

        Controls.Add(_outputTextBox);
        Controls.Add(footerPanel);
        Controls.Add(headerPanel);
        FormClosing += MainForm_FormClosing;
    }

    private async void StartButton_Click(object? sender, EventArgs e)
    {
        _startButton.Enabled = false;
        _stopButton.Enabled = true;
        _openReportButton.Enabled = false;
        _openFolderButton.Enabled = false;
        _storeNameTextBox.Enabled = false;
        _carrierComboBox.Enabled = false;
        _modeComboBox.Enabled = false;
        _progressBar.Value = 0;
        _outputTextBox.Clear();
        _lastLogPath = null;
        _lastHtmlReportPath = null;
        _cancellation = new CancellationTokenSource();

        try
        {
            var outputDirectory = OutputPaths.GetDefaultLogDirectory();
            AppendOutput($"日志目录：{outputDirectory}");
            AppendOutput("正在采集网络现场，请保持当前故障状态……");

            var progress = new Progress<DiagnosticProgress>(value =>
            {
                _progressBar.Maximum = Math.Max(1, value.Total);
                _progressBar.Value = Math.Clamp(value.Completed, _progressBar.Minimum, _progressBar.Maximum);
                _statusLabel.Text = $"{value.Message}（{value.Completed}/{value.Total}）";
                AppendOutput($"[{DateTime.Now:HH:mm:ss}] {value.Message}");
            });
            var runner = new DiagnosticRunner();
            var monitorDuration = _modeComboBox.SelectedIndex switch
            {
                1 => TimeSpan.FromMinutes(1),
                2 => TimeSpan.FromMinutes(5),
                3 => TimeSpan.FromMinutes(10),
                _ => TimeSpan.Zero
            };
            var result = await runner.RunAsync(
                new DiagnosticRunOptions(
                    outputDirectory,
                    _storeNameTextBox.Text,
                    _carrierComboBox.Text,
                    true,
                    monitorDuration,
                    TimeSpan.FromSeconds(10)),
                progress,
                _cancellation.Token);

            _lastLogPath = result.LogPath;
            _lastHtmlReportPath = result.HtmlReportPath;
            _openReportButton.Enabled = !string.IsNullOrWhiteSpace(_lastHtmlReportPath) && File.Exists(_lastHtmlReportPath);
            _openFolderButton.Enabled = true;
            _statusLabel.Text = result.Cancelled ? "检测已停止，已保存部分报告" : "检测完成，请把HTML、TXT和CSV发给利亚方舟技术人员";
            AppendOutput("");
            AppendOutput($"日志已保存：{result.LogPath}");
            if (!string.IsNullOrWhiteSpace(result.HtmlReportPath))
            {
                AppendOutput($"分析报告：{result.HtmlReportPath}");
            }
            if (!string.IsNullOrWhiteSpace(result.TimelineCsvPath))
            {
                AppendOutput($"时间序列：{result.TimelineCsvPath}");
            }
            if (!result.Cancelled)
            {
                var csvMessage = string.IsNullOrWhiteSpace(result.TimelineCsvPath)
                    ? string.Empty
                    : $"\r\n时间序列：{result.TimelineCsvPath}";
                var htmlMessage = string.IsNullOrWhiteSpace(result.HtmlReportPath)
                    ? string.Empty
                    : $"\r\n分析报告：{result.HtmlReportPath}";
                MessageBox.Show(
                    this,
                    $"网络检测完成。\r\n\r\n请把以下文件发给利亚方舟技术人员：\r\n{result.LogPath}{htmlMessage}{csvMessage}",
                    "检测完成",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "检测程序发生异常";
            AppendOutput($"程序异常：{ex.Message}");
            MessageBox.Show(this, ex.Message, "检测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _startButton.Enabled = true;
            _stopButton.Enabled = false;
            _storeNameTextBox.Enabled = true;
            _carrierComboBox.Enabled = true;
            _modeComboBox.Enabled = true;
            if (_closeAfterCancellation)
            {
                BeginInvoke(Close);
            }
        }
    }

    private void OpenReportButton_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastHtmlReportPath) || !File.Exists(_lastHtmlReportPath))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastHtmlReportPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "打开报告失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenFolderButton_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLogPath))
        {
            return;
        }

        try
        {
            var info = new ProcessStartInfo("explorer.exe", $"/select,\"{_lastLogPath}\"")
            {
                UseShellExecute = false
            };
            Process.Start(info);
        }
        catch
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.GetDirectoryName(_lastLogPath)!,
                UseShellExecute = true
            });
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cancellation is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "检测仍在进行，确定要退出吗？已经完成的结果仍会保存在日志中。",
            "确认退出",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.No)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _closeAfterCancellation = true;
        Enabled = false;
        _cancellation.Cancel();
    }

    private void AppendOutput(string text)
    {
        _outputTextBox.AppendText(text + Environment.NewLine);
        _outputTextBox.SelectionStart = _outputTextBox.TextLength;
        _outputTextBox.ScrollToCaret();
    }
}
