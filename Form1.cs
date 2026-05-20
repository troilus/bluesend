using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace BlueSend;

public partial class Form1 : Form
{
    private readonly BluetoothManager _bt = new();
    private bool _connected;
    private string _myName = "我";
    private string _otherName = "对方";

    // Panels
    private Panel? _pnlHome, _pnlServerWait, _pnlClientConnect, _pnlChat;

    // Server wait controls
    private TextBox? _txtAddress;
    private Label? _lblServerStatus;

    // Client controls
    private TextBox? _txtRemoteAddress;
    private Button? _btnConnect;

    // Chat controls
    private RichTextBox? _txtChat;
    private TextBox? _txtInput;
    private Button? _btnSend, _btnFile;

    // File entries in chat (char offset -> file path)
    private readonly List<(int start, int length, string filePath, string fileName, long fileSize)> _fileEntries = new();
    private string _contextFilePath = "";

    // Proxy
    private bool _isServerMode;
    private ProxyForwarder? _proxyForwarder;
    private Socks5Server? _socksServer;
    private Panel? _pnlProxy;
    private Button? _btnToggleProxy;
    private Label? _lblProxyStatus;
    private TextBox? _txtSocksPort;
    private TextBox? _txtMaxConns;
    private bool _proxyRunning;

    public Form1()
    {
        Text = "BlueSend";
        Size = new Size(520, 680);
        MinimumSize = new Size(420, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei", 10);
        FormClosing += Form1_FormClosing;

        _bt.Connected += Bt_Connected;
        _bt.Disconnected += Bt_Disconnected;
        _bt.MessageReceived += Bt_MessageReceived;
        _bt.ErrorOccurred += Bt_ErrorOccurred;
        _bt.FileTransferStarted += Bt_FileStarted;
        _bt.FileTransferProgress += Bt_FileProgress;
        _bt.FileTransferCompleted += Bt_FileCompleted;

        BuildHomePanel();
    }

    private void ClearForm()
    {
        foreach (Control c in Controls) c.Dispose();
        Controls.Clear();
        _pnlHome = _pnlServerWait = _pnlClientConnect = _pnlChat = null;
        _fileEntries.Clear();
    }

    // ======================== HOME ========================

    private void BuildHomePanel()
    {
        ClearForm();
        _pnlHome = new Panel { Dock = DockStyle.Fill };
        Controls.Add(_pnlHome);

        var title = new Label
        {
            Text = "BlueSend",
            Font = new Font("Microsoft YaHei", 28, FontStyle.Bold),
            AutoSize = false, Size = new Size(400, 50),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(60, 140)
        };
        _pnlHome.Controls.Add(title);

        var sub = new Label
        {
            Text = "蓝牙文字通信",
            Font = new Font("Microsoft YaHei", 12),
            AutoSize = false, Size = new Size(400, 30),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(60, 190)
        };
        _pnlHome.Controls.Add(sub);

        var btnServer = new Button
        {
            Text = "创建聊天（服务端）",
            Font = new Font("Microsoft YaHei", 11),
            Size = new Size(260, 42),
            Location = new Point(130, 280),
            FlatStyle = FlatStyle.System
        };
        btnServer.Click += (_, _) => BuildServerWaitPanel();
        _pnlHome.Controls.Add(btnServer);

        var btnClient = new Button
        {
            Text = "加入聊天（客户端）",
            Font = new Font("Microsoft YaHei", 11),
            Size = new Size(260, 42),
            Location = new Point(130, 340),
            FlatStyle = FlatStyle.System
        };
        btnClient.Click += (_, _) => BuildClientConnectPanel();
        _pnlHome.Controls.Add(btnClient);
    }

    // ======================== SERVER WAIT ========================

    private void BuildServerWaitPanel()
    {
        ClearForm();
        _pnlServerWait = new Panel { Dock = DockStyle.Fill };
        Controls.Add(_pnlServerWait);

        var title = new Label
        {
            Text = "等待对方连接...",
            Font = new Font("Microsoft YaHei", 16, FontStyle.Bold),
            AutoSize = false, Size = new Size(400, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(60, 120)
        };
        _pnlServerWait.Controls.Add(title);

        var lblHint = new Label
        {
            Text = "本机蓝牙地址:",
            Font = new Font("Microsoft YaHei", 10),
            AutoSize = false, Size = new Size(400, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(60, 170)
        };
        _pnlServerWait.Controls.Add(lblHint);

        _txtAddress = new TextBox
        {
            Text = _bt.GetLocalAddress(),
            Font = new Font("Consolas", 14, FontStyle.Bold),
            ForeColor = Color.FromArgb(0x19, 0x76, 0xD2),
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            Size = new Size(240, 34),
            TextAlign = HorizontalAlignment.Center,
            Location = new Point(110, 196),
            Cursor = Cursors.IBeam
        };
        _pnlServerWait.Controls.Add(_txtAddress);

        var btnCopy = new Button
        {
            Text = "复制",
            Font = new Font("Microsoft YaHei", 9),
            Size = new Size(50, 28),
            Location = new Point(360, 199),
            FlatStyle = FlatStyle.System,
            Cursor = Cursors.Hand
        };
        btnCopy.Click += (_, _) =>
        {
            Clipboard.SetText(_txtAddress.Text);
            btnCopy.Text = "✓";
            var t = new System.Windows.Forms.Timer { Interval = 1500 };
            t.Tick += (_, _) => { btnCopy.Text = "复制"; t.Stop(); t.Dispose(); };
            t.Start();
        };
        _pnlServerWait.Controls.Add(btnCopy);

        _lblServerStatus = new Label
        {
            Text = "状态: 等待连接...",
            Font = new Font("Microsoft YaHei", 10),
            AutoSize = false, Size = new Size(400, 24),
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.Gray,
            Location = new Point(60, 290)
        };
        _pnlServerWait.Controls.Add(_lblServerStatus);

        var btnCancel = new Button
        {
            Text = "取消",
            Font = new Font("Microsoft YaHei", 11),
            Size = new Size(120, 36),
            Location = new Point(200, 340),
            FlatStyle = FlatStyle.System
        };
        btnCancel.Click += (_, _) => CancelAndGoHome();
        _pnlServerWait.Controls.Add(btnCancel);

        _bt.StartServer();
    }

    // ======================== CLIENT CONNECT ========================

    private void BuildClientConnectPanel()
    {
        ClearForm();
        _pnlClientConnect = new Panel { Dock = DockStyle.Fill };
        Controls.Add(_pnlClientConnect);

        var title = new Label
        {
            Text = "连接聊天",
            Font = new Font("Microsoft YaHei", 16, FontStyle.Bold),
            AutoSize = false, Size = new Size(400, 36),
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(60, 120)
        };
        _pnlClientConnect.Controls.Add(title);

        var lblHint = new Label
        {
            Text = "服务器蓝牙地址:",
            Font = new Font("Microsoft YaHei", 10),
            Location = new Point(110, 180), AutoSize = true
        };
        _pnlClientConnect.Controls.Add(lblHint);

        var savedAddr = "";
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueSend");
            var file = Path.Combine(dir, "remote_addr.txt");
            if (File.Exists(file))
                savedAddr = File.ReadAllText(file).Trim();
        }
        catch { }

        _txtRemoteAddress = new TextBox
        {
            Text = savedAddr,
            Font = new Font("Consolas", 12),
            Location = new Point(110, 208),
            Size = new Size(300, 28),
            TextAlign = HorizontalAlignment.Center
        };
        _pnlClientConnect.Controls.Add(_txtRemoteAddress);

        var lblFormat = new Label
        {
            Text = "格式: 00:11:22:33:44:55",
            Font = new Font("Microsoft YaHei", 9),
            ForeColor = Color.Gray,
            Location = new Point(112, 242), AutoSize = true
        };
        _pnlClientConnect.Controls.Add(lblFormat);

        _btnConnect = new Button
        {
            Text = "连接",
            Font = new Font("Microsoft YaHei", 11),
            Size = new Size(140, 38),
            Location = new Point(190, 290),
            FlatStyle = FlatStyle.System
        };
        _btnConnect.Click += BtnConnect_Click;
        _pnlClientConnect.Controls.Add(_btnConnect);

        var btnBack = new Button
        {
            Text = "返回",
            Font = new Font("Microsoft YaHei", 11),
            Size = new Size(120, 36),
            Location = new Point(200, 345),
            FlatStyle = FlatStyle.System
        };
        btnBack.Click += (_, _) => BuildHomePanel();
        _pnlClientConnect.Controls.Add(btnBack);

        _txtRemoteAddress.KeyPress += (_, e) =>
        {
            if (e.KeyChar == (char)Keys.Enter)
                BtnConnect_Click(this, EventArgs.Empty);
        };
    }

    private void BtnConnect_Click(object? sender, EventArgs e)
    {
        var addr = _txtRemoteAddress?.Text.Trim();
        if (string.IsNullOrEmpty(addr))
        {
            MessageBox.Show("请输入蓝牙地址", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _btnConnect!.Enabled = false;
        _btnConnect.Text = "连接中...";
        _otherName = addr;
        _bt.Connect(addr);

        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueSend");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "remote_addr.txt"), addr);
        }
        catch { }
    }

    // ======================== CHAT ========================

    private void BuildChatPanel()
    {
        ClearForm();
        _connected = true;

        _pnlChat = new Panel { Dock = DockStyle.Fill };
        Controls.Add(_pnlChat);

        _txtChat = new RichTextBox
        {
            ReadOnly = true,
            Font = new Font("Microsoft YaHei", 10),
            Location = new Point(10, 10),
            Size = new Size(ClientSize.Width - 20, ClientSize.Height - 200),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            WordWrap = true
        };
        _txtChat.AppendText("系统: 连接已建立，可以开始聊天了！\n");
        _txtChat.MouseDown += TxtChat_MouseDown;
        _pnlChat.Controls.Add(_txtChat);

        BuildProxyPanel();

        var inputPanel = new Panel
        {
            Location = new Point(10, ClientSize.Height - 95),
            Size = new Size(ClientSize.Width - 20, 40),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
        };
        _pnlChat.Controls.Add(inputPanel);

        _btnFile = new Button
        {
            Text = "📎",
            Font = new Font("Microsoft YaHei", 12),
            Size = new Size(40, 30),
            Location = new Point(0, 0),
            FlatStyle = FlatStyle.System,
            // ToolTip set separately below
        };
        _btnFile.Click += BtnFile_Click;
        inputPanel.Controls.Add(_btnFile);
        new ToolTip().SetToolTip(_btnFile, "发送文件");

        _txtInput = new TextBox
        {
            Font = new Font("Microsoft YaHei", 11),
            Location = new Point(45, 0),
            Size = new Size(inputPanel.Width - 45 - 80, 30),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };
        _txtInput.KeyPress += (_, e) =>
        {
            if (e.KeyChar == (char)Keys.Enter) { SendMessage(); e.Handled = true; }
        };
        inputPanel.Controls.Add(_txtInput);

        _btnSend = new Button
        {
            Text = "发送",
            Font = new Font("Microsoft YaHei", 10),
            Size = new Size(75, 30),
            Location = new Point(inputPanel.Width - 80, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.System
        };
        _btnSend.Click += (_, _) => SendMessage();
        inputPanel.Controls.Add(_btnSend);

        _txtInput.Focus();
    }

    private void BtnFile_Click(object? sender, EventArgs e)
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择要发送的文件",
            Multiselect = false
        };
        if (ofd.ShowDialog() != DialogResult.OK) return;

        var fi = new FileInfo(ofd.FileName);
        var sizeStr = FormatSize(fi.Length);
        _bt.SendFile(ofd.FileName);
        AppendFileMessage(_myName, fi.Name, fi.Length);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    // ======================== FILE UI ========================

    private void AppendFileMessage(string sender, string fileName, long fileSize)
    {
        _txtChat?.Invoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var line = $"  [{time}] {sender}: 📎 {fileName} ({FormatSize(fileSize)})\n";

            var start = _txtChat.TextLength;
            _txtChat.AppendText(line);

            if (sender != _myName)
                _fileEntries.Add((start, line.Length, "", fileName, fileSize));
        });
    }

    private void AddFileEntry(int start, int length, string filePath, string fileName, long fileSize)
    {
        _fileEntries.Add((start, length, filePath, fileName, fileSize));
    }

    private (string filePath, string fileName)? FindFileAtChar(int charIndex)
    {
        foreach (var e in _fileEntries)
            if (charIndex >= e.start && charIndex < e.start + e.length && !string.IsNullOrEmpty(e.filePath))
                return (e.filePath, e.fileName);
        return null;
    }

    private void TxtChat_MouseDown(object? sender, MouseEventArgs e)
    {
        if (_txtChat == null) return;
        var idx = _txtChat.GetCharIndexFromPosition(e.Location);
        var found = FindFileAtChar(idx);
        if (found == null) return;

        var (path, name) = found.Value;

        if (e.Button == MouseButtons.Left)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开文件: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            _contextFilePath = path;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(_contextFilePath) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show($"无法打开文件: {ex.Message}", "错误"); }
            });
            menu.Items.Add("另存为...", null, (_, _) =>
            {
                using var sfd = new SaveFileDialog
                {
                    FileName = name,
                    Title = "另存文件"
                };
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try { File.Copy(_contextFilePath, sfd.FileName, true); }
                    catch (Exception ex) { MessageBox.Show($"保存失败: {ex.Message}", "错误"); }
                }
            });
            menu.Show(_txtChat, e.Location);
        }
    }

    // ======================== TEXT MSG ========================

    private void SendMessage()
    {
        if (!_connected || _txtInput == null || _bt == null) return;
        var text = _txtInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _txtInput.Text = "";
        _bt.Send(text);
        AppendTextMessage(_myName, text);
    }

    private void AppendTextMessage(string sender, string text)
    {
        if (_txtChat == null || _txtChat.IsDisposed) return;
        _txtChat.Invoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            _txtChat.SelectionColor = Color.Gray;
            _txtChat.AppendText($"  [{time}] ");

            if (sender == "系统")
            {
                _txtChat.SelectionColor = Color.DimGray;
                _txtChat.AppendText($"{sender}: {text}\n");
            }
            else if (sender == _myName)
            {
                _txtChat.SelectionColor = Color.FromArgb(0x19, 0x76, 0xD2);
                _txtChat.SelectionFont = new Font("Microsoft YaHei", 10, FontStyle.Bold);
                _txtChat.AppendText($"{sender}: ");
                _txtChat.SelectionFont = new Font("Microsoft YaHei", 10);
                _txtChat.SelectionColor = Color.Black;
                _txtChat.AppendText($"{text}\n");
            }
            else
            {
                _txtChat.SelectionColor = Color.FromArgb(0x38, 0x8E, 0x3C);
                _txtChat.SelectionFont = new Font("Microsoft YaHei", 10, FontStyle.Bold);
                _txtChat.AppendText($"{sender}: ");
                _txtChat.SelectionFont = new Font("Microsoft YaHei", 10);
                _txtChat.SelectionColor = Color.Black;
                _txtChat.AppendText($"{text}\n");
            }
            _txtChat.ScrollToCaret();
        });
    }

    private void AppendSystemMessage(string text)
    {
        if (_txtChat == null || _txtChat.IsDisposed) return;
        _txtChat.Invoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            _txtChat.SelectionColor = Color.Gray;
            _txtChat.AppendText($"  [{time}] ");
            _txtChat.SelectionColor = Color.DimGray;
            _txtChat.AppendText($"系统: {text}\n");
            _txtChat.ScrollToCaret();
        });
    }

    // ======================== PROXY ========================

    private void BuildProxyPanel()
    {
        _pnlProxy = new Panel
        {
            Location = new Point(10, ClientSize.Height - 175),
            Size = new Size(ClientSize.Width - 20, 70),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BorderStyle = BorderStyle.FixedSingle
        };

        LoadProxyConfig();

        var y = 6;
        if (_isServerMode)
        {
            var lbl = new Label
            {
                Text = "代理转发器:",
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold),
                Location = new Point(8, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(lbl);

            _lblProxyStatus = new Label
            {
                Text = "已停止",
                Font = new Font("Microsoft YaHei", 9),
                ForeColor = Color.Gray,
                Location = new Point(100, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(_lblProxyStatus);
        }
        else
        {
            var lbl = new Label
            {
                Text = "SOCKS5 代理:",
                Font = new Font("Microsoft YaHei", 9, FontStyle.Bold),
                Location = new Point(8, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(lbl);

            var lblPort = new Label
            {
                Text = "端口:",
                Font = new Font("Microsoft YaHei", 9),
                Location = new Point(110, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(lblPort);

            _txtSocksPort = new TextBox
            {
                Text = _socksPort.ToString(),
                Font = new Font("Consolas", 9),
                Size = new Size(48, 22),
                Location = new Point(146, y - 2)
            };
            _pnlProxy.Controls.Add(_txtSocksPort);

            var lblMax = new Label
            {
                Text = "最大连接:",
                Font = new Font("Microsoft YaHei", 9),
                Location = new Point(200, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(lblMax);

            _txtMaxConns = new TextBox
            {
                Text = _maxConns.ToString(),
                Font = new Font("Consolas", 9),
                Size = new Size(36, 22),
                Location = new Point(268, y - 2)
            };
            _pnlProxy.Controls.Add(_txtMaxConns);

            _lblProxyStatus = new Label
            {
                Text = "已停止",
                Font = new Font("Microsoft YaHei", 9),
                ForeColor = Color.Gray,
                Location = new Point(315, y), AutoSize = true
            };
            _pnlProxy.Controls.Add(_lblProxyStatus);
        }

        y += 24;
        _btnToggleProxy = new Button
        {
            Text = "⚡ 启动代理",
            Font = new Font("Microsoft YaHei", 9),
            Size = new Size(130, 30),
            Location = new Point(8, y),
            FlatStyle = FlatStyle.System,
            Cursor = Cursors.Hand
        };
        _btnToggleProxy.Click += BtnToggleProxy_Click;
        _pnlProxy.Controls.Add(_btnToggleProxy);

        var lblHint = new Label
        {
            Text = _isServerMode
                ? "为蓝牙客户端提供网络代理转发"
                : "应用设置 SOCKS5 代理为 127.0.0.1:端口",
            Font = new Font("Microsoft YaHei", 8),
            ForeColor = Color.Gray,
            Location = new Point(145, y + 6), AutoSize = true
        };
        _pnlProxy.Controls.Add(lblHint);

        _pnlChat!.Controls.Add(_pnlProxy);
    }

    private int _socksPort = 1080;
    private int _maxConns = 10;

    private void LoadProxyConfig()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueSend", "proxy_config.json");
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = System.Text.Json.JsonSerializer.Deserialize<ProxyConfig>(json);
                if (cfg != null)
                {
                    _socksPort = cfg.SocksPort;
                    _maxConns = cfg.MaxConnections;
                }
            }
        }
        catch { }
    }

    private void SaveProxyConfig()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BlueSend");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "proxy_config.json");
            var cfg = new ProxyConfig
            {
                SocksPort = _socksPort,
                MaxConnections = _maxConns
            };
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    private void BtnToggleProxy_Click(object? sender, EventArgs e)
    {
        if (_proxyRunning)
        {
            StopProxy();
        }
        else
        {
            StartProxy();
        }
    }

    private void StartProxy()
    {
        if (!_connected || _bt == null) return;

        if (_isServerMode)
        {
            var max = _maxConns;
            if (_txtMaxConns != null && int.TryParse(_txtMaxConns.Text, out var m))
                max = Math.Clamp(m, 1, 100);
            _maxConns = max;
            _proxyForwarder = new ProxyForwarder(_bt, max);
            _proxyForwarder.Start();
            _proxyRunning = true;
            _btnToggleProxy!.Text = "⏹ 停止代理";
            _lblProxyStatus!.Text = "运行中";
            _lblProxyStatus.ForeColor = Color.Green;
            _txtMaxConns?.Invoke(() => _txtMaxConns.Enabled = false);
        }
        else
        {
            var port = _socksPort;
            if (_txtSocksPort != null && int.TryParse(_txtSocksPort.Text, out var p))
                port = Math.Clamp(p, 1024, 65535);
            var max = _maxConns;
            if (_txtMaxConns != null && int.TryParse(_txtMaxConns.Text, out var m))
                max = Math.Clamp(m, 1, 100);
            _socksPort = port;
            _maxConns = max;

            _socksServer = new Socks5Server(_bt, port, max);
            _socksServer.RunningChanged += (_, running) =>
            {
                if (!running) StopProxy();
            };
            _socksServer.Start();
            _proxyRunning = true;
            _btnToggleProxy!.Text = "⏹ 停止代理";
            _lblProxyStatus!.Text = $"运行中 (127.0.0.1:{port})";
            _lblProxyStatus.ForeColor = Color.Green;
            _txtSocksPort?.Invoke(() => _txtSocksPort.Enabled = false);
            _txtMaxConns?.Invoke(() => _txtMaxConns.Enabled = false);
        }

        SaveProxyConfig();
    }

    private void StopProxy()
    {
        if (_proxyForwarder != null)
        {
            _proxyForwarder.Stop();
            _proxyForwarder.Dispose();
            _proxyForwarder = null;
        }
        if (_socksServer != null)
        {
            _socksServer.Stop();
            _socksServer.Dispose();
            _socksServer = null;
        }
        _proxyRunning = false;
        if (_btnToggleProxy != null && !_btnToggleProxy.IsDisposed)
        {
            _btnToggleProxy.Invoke(() =>
            {
                _btnToggleProxy.Text = "⚡ 启动代理";
            });
        }
        if (_lblProxyStatus != null && !_lblProxyStatus.IsDisposed)
        {
            _lblProxyStatus.Invoke(() =>
            {
                _lblProxyStatus.Text = "已停止";
                _lblProxyStatus.ForeColor = Color.Gray;
            });
        }
        if (_txtSocksPort != null && !_txtSocksPort.IsDisposed)
            _txtSocksPort.Invoke(() => _txtSocksPort.Enabled = true);
        if (_txtMaxConns != null && !_txtMaxConns.IsDisposed)
            _txtMaxConns.Invoke(() => _txtMaxConns.Enabled = true);
    }

    // ======================== BLUETOOTH EVENTS ========================

    private void Bt_Connected(object? sender, string remoteAddr)
    {
        Invoke(() =>
        {
            Text = $"BlueSend - {remoteAddr}";
            if (sender == _bt && _pnlServerWait != null && !_pnlServerWait.IsDisposed)
            {
                _otherName = remoteAddr;
                _isServerMode = true;
                BuildChatPanel();
            }
            else if (_pnlClientConnect != null && !_pnlClientConnect.IsDisposed)
            {
                _isServerMode = false;
                BuildChatPanel();
            }
            else if (_connected)
            {
                AppendSystemMessage("对方已重新连接");
            }
        });
    }

    private void Bt_Disconnected(object? sender, EventArgs e)
    {
        _connected = false;
        StopProxy();
        Invoke(() => AppendSystemMessage("对方已断开连接"));
    }

    private void Bt_MessageReceived(object? sender, string msg)
    {
        Invoke(() => AppendTextMessage(_otherName, msg));
    }

    private void Bt_ErrorOccurred(object? sender, string error)
    {
        Invoke(() =>
        {
            if (_pnlServerWait != null && !_pnlServerWait.IsDisposed)
            {
                if (_lblServerStatus != null)
                    _lblServerStatus.Text = $"错误: {error}";
            }
            else if (_pnlClientConnect != null && !_pnlClientConnect.IsDisposed)
            {
                _btnConnect!.Enabled = true;
                _btnConnect.Text = "连接";
                MessageBox.Show(error, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (_connected)
            {
                AppendSystemMessage($"错误: {error}");
            }
        });
    }

    private void Bt_FileStarted(object? sender, (string fileName, long fileSize) info)
    {
        Invoke(() =>
        {
            AppendSystemMessage($"正在接收 {info.fileName} ({FormatSize(info.fileSize)})...");
        });
    }

    private void Bt_FileProgress(object? sender, (string fileName, int percentage) info)
    {
        // optional: could update the system message
    }

    private void Bt_FileCompleted(object? sender, (string fileName, string savedPath) info)
    {
        Invoke(() =>
        {
            var size = new FileInfo(info.savedPath).Length;
            var line = $"  [{DateTime.Now:HH:mm:ss}] {_otherName}: 📎 {info.fileName} ({FormatSize(size)})\n";
            var start = _txtChat!.TextLength;
            _txtChat.AppendText(line);
            _fileEntries.Add((start, line.Length, info.savedPath, info.fileName, size));
            _txtChat.ScrollToCaret();
        });
    }

    private void CancelAndGoHome()
    {
        StopProxy();
        _bt.Stop();
        _connected = false;
        Text = "BlueSend";
        if (InvokeRequired) Invoke(BuildHomePanel);
        else BuildHomePanel();
    }

    private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
    {
        StopProxy();
        _bt.Dispose();
    }
}

internal sealed class ProxyConfig
{
    public int SocksPort { get; set; } = 1080;
    public int MaxConnections { get; set; } = 10;
}
