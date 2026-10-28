using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using WixToolset.BootstrapperApplicationApi;

namespace Tausi.Setup
{
    /// <summary>
    /// The installer's screens: Welcome (and licence), Database, Port, Installing, Finish. An upgrade goes from
    /// Welcome straight to Installing; Remove (from Apps and features) asks whether to delete all data too.
    /// </summary>
    public sealed class SetupForm : Form
    {
        private static readonly Color Ground = ColorTranslator.FromHtml("#F4F4F2");
        private static readonly Color Primary = ColorTranslator.FromHtml("#B34A0C");
        private static readonly Color Dark = ColorTranslator.FromHtml("#1C1C1C");
        private static readonly Color Muted = ColorTranslator.FromHtml("#5C5C58");
        private static readonly Color WarningTint = ColorTranslator.FromHtml("#FBE7DA");
        private static readonly Color WarningText = ColorTranslator.FromHtml("#8A3708");

        private readonly TausiBootstrapper _ba;
        private readonly Panel _body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 8), BackColor = Ground };
        private readonly Label _title = new Label { Dock = DockStyle.Top, Height = 56, Font = new Font("Segoe UI Semibold", 15f), ForeColor = Color.White, BackColor = Dark, Padding = new Padding(24, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
        private readonly FlowLayoutPanel _buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(16, 12, 16, 12), BackColor = Ground };
        private readonly Button _next = Primary_("Next");
        private readonly Button _back = Secondary("Back");
        private readonly Button _cancel = Secondary("Cancel");

        private Action _onNext;
        private Action _onBack;
        private bool _running;

        // Database screen
        private RadioButton _sqlExpress, _existing, _postgres;
        private TextBox _instance, _sqlUser, _sqlPassword, _pgHost, _pgPort, _pgUser, _pgPassword;
        private RadioButton _windowsAuth, _sqlAuth;
        private CheckBox _grantSystem;
        private CheckBox _restore;
        private TextBox _restoreFile, _restorePassword;
        private Label _testResult;
        private bool _tested;

        // Port screen
        private NumericUpDown _port;
        private CheckBox _firewall;
        private Label _portResult;

        // Installing
        private ProgressBar _progress;
        private Label _status;

        public SetupForm(TausiBootstrapper ba)
        {
            _ba = ba;
            Text = "Tausi POS Server Setup";
            ClientSize = new Size(720, 560);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10f);
            BackColor = Ground;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

            _buttons.Controls.AddRange(new Control[] { _next, _back, _cancel });
            Controls.Add(_body);
            Controls.Add(_buttons);
            Controls.Add(_title);
            _next.Click += (s, e) => _onNext?.Invoke();
            _back.Click += (s, e) => _onBack?.Invoke();
            _cancel.Click += (s, e) => Close();
            FormClosing += (s, e) =>
            {
                if (_running) e.Cancel = true;
            };

            _ba.Progressed += p => OnUi(() => { if (_progress != null) _progress.Value = Math.Max(0, Math.Min(100, p)); });
            _ba.Status += t => OnUi(() => { if (_status != null) _status.Text = t; });
            _ba.Finished += (code, message) => OnUi(() => ShowFinish(code, message));

            Page("Tausi POS Server", Text_("Looking at this PC…"));
            _next.Enabled = false;
        }

        private void OnUi(Action action)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }

        /// <summary>Detection is done: start at the right screen.</summary>
        public void Detected()
        {
            if (_ba.Action == LaunchAction.Uninstall || (_ba.Installed && _ba.UpgradingFrom == null)) ShowRemove();
            else ShowWelcome();
        }

        // ---- Screens ----

        private void ShowWelcome()
        {
            var version = typeof(SetupForm).Assembly.GetName().Version;
            var intro = _ba.UpgradingFrom != null
                ? $"Tausi POS Server {_ba.UpgradingFrom} is installed. This upgrades it to {_ba.Get("WixBundleVersion")}.\r\n\r\n" +
                  "A backup is taken first; the settings and all data are kept. If anything goes wrong, the previous version and its data are put back."
                : $"Tausi POS Server {_ba.Get("WixBundleVersion")} by NexaGen Solutions.\r\n\r\n" +
                  "This PC becomes the bar's server: it holds the data and the printing, and the counter PCs connect to it. Please read the licence agreement.";
            var licence = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = Licence(), BackColor = Color.White };
            var accept = new CheckBox { Text = "I accept the licence agreement", Dock = DockStyle.Bottom, Height = 36 };
            accept.CheckedChanged += (s, e) => _next.Enabled = accept.Checked;
            Page(_ba.UpgradingFrom != null ? "Upgrade Tausi POS Server" : "Welcome", Text_(intro, 72), licence, accept);
            _back.Visible = false;
            _next.Text = _ba.UpgradingFrom != null ? "Upgrade" : "Next";
            _next.Enabled = false;
            _onNext = () =>
            {
                if (_ba.UpgradingFrom != null) Install();
                else ShowDatabase();
            };
        }

        private void ShowDatabase()
        {
            _sqlExpress = _sqlExpress ?? new RadioButton { Text = "Install SQL Server Express (recommended)", Checked = _ba.Get("DB") == "sqlexpress", AutoSize = true };
            _existing = _existing ?? new RadioButton { Text = "Use an existing SQL Server on this PC or the network", Checked = _ba.Get("DB") == "existing", AutoSize = true };
            _postgres = _postgres ?? new RadioButton { Text = "Use PostgreSQL (advanced)", Checked = _ba.Get("DB") == "postgres", AutoSize = true };
            _instance = _instance ?? new TextBox { Text = _ba.Get("DB") == "existing" ? _ba.Get("SQLINSTANCE") : @".\SQLEXPRESS", Width = 260 };
            _windowsAuth = _windowsAuth ?? new RadioButton { Text = "Windows sign-in", Checked = true, AutoSize = true };
            _sqlAuth = _sqlAuth ?? new RadioButton { Text = "SQL login", AutoSize = true };
            _sqlUser = _sqlUser ?? new TextBox { Width = 160 };
            _sqlPassword = _sqlPassword ?? new TextBox { Width = 160, UseSystemPasswordChar = true };
            _grantSystem = _grantSystem ?? new CheckBox
            {
                Text = "Let the installer set up the database (adds this PC's SYSTEM account as a SQL Server administrator)",
                Checked = true, AutoSize = true, MaximumSize = new Size(600, 0),
            };
            _pgHost = _pgHost ?? new TextBox { Text = _ba.Get("PGHOST"), Width = 200 };
            _pgPort = _pgPort ?? new TextBox { Text = _ba.Get("PGPORT"), Width = 60 };
            _pgUser = _pgUser ?? new TextBox { Text = _ba.Get("PGUSER"), Width = 160 };
            _pgPassword = _pgPassword ?? new TextBox { Width = 160, UseSystemPasswordChar = true };
            _restore = _restore ?? new CheckBox { Text = "Restore from a backup (moving to a new PC): skips setup", AutoSize = true };
            _restoreFile = _restoreFile ?? new TextBox { Width = 380, ReadOnly = true };
            _restorePassword = _restorePassword ?? new TextBox { Width = 160, UseSystemPasswordChar = true };
            _testResult = _testResult ?? new Label { AutoSize = false, Width = 640, Height = 44, ForeColor = Muted };
            var test = Secondary("Test connection");
            var browse = Secondary("Choose…");

            var existingBox = Group(
                Row(Label_("Server or instance"), _instance),
                Row(_windowsAuth, _sqlAuth),
                Row(Label_("User"), _sqlUser, Label_("Password"), _sqlPassword),
                _grantSystem);
            var postgresBox = Group(
                Row(Label_("Host"), _pgHost, Label_("Port"), _pgPort),
                Row(Label_("User"), _pgUser, Label_("Password"), _pgPassword));
            var restoreBox = Group(Row(_restoreFile, browse), Row(Label_("Backup password (if any)"), _restorePassword));

            void Update()
            {
                existingBox.Visible = _existing.Checked;
                postgresBox.Visible = _postgres.Checked;
                test.Visible = !_sqlExpress.Checked;
                _sqlUser.Enabled = _sqlPassword.Enabled = _sqlAuth.Checked;
                _grantSystem.Visible = _windowsAuth.Checked;
                restoreBox.Visible = _restore.Checked;
            }
            foreach (var c in new Control[] { _sqlExpress, _existing, _postgres, _windowsAuth, _sqlAuth, _restore })
            {
                if (c is RadioButton r) r.CheckedChanged += (s, e) => { _tested = false; Update(); };
                if (c is CheckBox b) b.CheckedChanged += (s, e) => Update();
            }
            test.Click += (s, e) => TestConnection();
            browse.Click += (s, e) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "Tausi POS backups (*.tausibak)|*.tausibak", Title = "Choose the backup to restore" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) _restoreFile.Text = dialog.FileName;
            };
            Update();

            var flow = Flow(
                Text_("Where Tausi POS keeps its data. PCs that already run SQL Server Express (for SambaPOS) can use it: Tausi POS gets its own database and never touches SambaPOS's.", 44),
                _sqlExpress, _existing, existingBox, _postgres, postgresBox, Row(test), _testResult, _restore, restoreBox);
            Page("Database", flow);
            _back.Visible = true;
            _next.Text = "Next";
            _next.Enabled = true;
            _onBack = ShowWelcome;
            _onNext = () =>
            {
                if (!_sqlExpress.Checked && !_tested)
                {
                    TestConnection();
                    if (!_tested) return;
                }
                if (_restore.Checked && !File.Exists(_restoreFile.Text))
                {
                    MessageBox.Show(this, "Choose the backup file to restore.", Text);
                    return;
                }
                SaveDatabase();
                // The installer's database steps run as SYSTEM, which SQL Server 2012+ doesn't make an administrator.
                if (_existing.Checked && _windowsAuth.Checked && !Checks.SystemIsSqlAdmin(_instance.Text.Trim()))
                {
                    if (!_grantSystem.Checked)
                    {
                        MessageBox.Show(this, "The installer needs to set up the database as this PC's SYSTEM account. Tick the box, or use a SQL login.", Text);
                        return;
                    }
                    var message = Checks.GrantSystem(_instance.Text.Trim(), out var granted);
                    if (!granted)
                    {
                        MessageBox.Show(this, message, Text);
                        return;
                    }
                }
                if (_sqlExpress.Checked && string.IsNullOrEmpty(_ba.Get("SQLMEDIA")) && !Checks.Online())
                {
                    MessageBox.Show(this,
                        "This PC can't reach the internet to download SQL Server Express. Choose the SQL Server Express installer (SQLEXPR_x64_ENU.exe) from a USB drive.",
                        Text);
                    using (var dialog = new OpenFileDialog { Filter = "SQL Server Express installer (SQLEXPR*.exe)|SQLEXPR*.exe|Programs (*.exe)|*.exe" })
                    {
                        if (dialog.ShowDialog(this) != DialogResult.OK) return;
                        _ba.Set("SQLMEDIA", dialog.FileName);
                    }
                }
                ShowPort();
            };
        }

        private void TestConnection()
        {
            Cursor = Cursors.WaitCursor;
            try
            {
                bool ok;
                if (_postgres.Checked)
                    _testResult.Text = Checks.TestPostgres(_pgHost.Text.Trim(), int.TryParse(_pgPort.Text, out var p) ? p : 5432, out ok);
                else
                    _testResult.Text = Checks.TestSqlServer(_instance.Text.Trim(), _sqlAuth.Checked ? _sqlUser.Text.Trim() : null, _sqlPassword.Text, out ok);
                _testResult.ForeColor = ok ? Dark : WarningText;
                _tested = ok;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void SaveDatabase()
        {
            if (_sqlExpress.Checked)
            {
                _ba.Set("DB", "sqlexpress");
                _ba.Set("SQLINSTANCE", @".\TAUSI");
                _ba.Set("SQLUSER", "");
            }
            else if (_existing.Checked)
            {
                _ba.Set("DB", "existing");
                _ba.Set("SQLINSTANCE", _instance.Text.Trim());
                _ba.Set("SQLUSER", _sqlAuth.Checked ? _sqlUser.Text.Trim() : "");
                _ba.Set("SQLPASSWORD", _sqlAuth.Checked ? _sqlPassword.Text : "");
            }
            else
            {
                _ba.Set("DB", "postgres");
                _ba.Set("PGHOST", _pgHost.Text.Trim());
                _ba.Set("PGPORT", _pgPort.Text.Trim());
                _ba.Set("PGUSER", _pgUser.Text.Trim());
                _ba.Set("PGPASSWORD", _pgPassword.Text);
            }
            _ba.Set("SQLSERVICEACCOUNT", Checks.SqlServiceAccount(_ba.Get("SQLINSTANCE")));
            _ba.Set("RESTOREFILE", _restore.Checked ? _restoreFile.Text : "");
            _ba.Set("RESTOREPASSWORD", _restore.Checked ? _restorePassword.Text : "");
        }

        private void ShowPort()
        {
            _port = _port ?? new NumericUpDown { Minimum = 1, Maximum = 65535, Value = int.TryParse(_ba.Get("PORT"), out var p) ? p : 5080, Width = 100 };
            _firewall = _firewall ?? new CheckBox { Text = "Open the Windows Firewall for the local network", Checked = _ba.Get("FIREWALL") != "0", AutoSize = true };
            _portResult = _portResult ?? new Label { AutoSize = false, Width = 640, Height = 44 };
            void Check()
            {
                var free = Checks.PortFree((int)_port.Value);
                _portResult.Text = free ? $"Port {_port.Value} is free." : $"Something on this PC already uses port {_port.Value}. Choose another.";
                _portResult.ForeColor = free ? Dark : WarningText;
                _next.Enabled = free;
            }
            _port.ValueChanged += (s, e) => Check();
            Check();
            Page("Port", Flow(
                Text_("The counter PCs and browsers reach the server on this port. 5080 suits nearly every bar.", 44),
                Row(Label_("Port"), _port), _portResult, _firewall,
                Text_("The firewall rule lets in the private (home or work) network only, on this port.", 44)));
            _next.Text = "Install";
            _onBack = ShowDatabase;
            _onNext = () =>
            {
                _ba.Set("PORT", ((int)_port.Value).ToString());
                _ba.Set("FIREWALL", _firewall.Checked ? "1" : "0");
                Install();
            };
        }

        private void Install()
        {
            ShowRunning(_ba.UpgradingFrom != null ? "Upgrading" : "Installing");
            _ba.Start(LaunchAction.Install);
        }

        private void ShowRunning(string title)
        {
            _progress = new ProgressBar { Width = 640, Height = 24, Style = ProgressBarStyle.Continuous };
            _status = new Label { AutoSize = false, Width = 640, Height = 64, Text = "Starting…" };
            Page(title, Flow(_progress, _status));
            _running = true;
            _next.Visible = _back.Visible = _cancel.Visible = false;
        }

        private void ShowFinish(int status, string message)
        {
            _running = false;
            _next.Visible = true;
            _back.Visible = false;
            _cancel.Visible = false;
            _next.Enabled = true;
            _next.Text = "Close";
            _onNext = Close;
            if (status < 0)
            {
                Page("Not finished", Flow(Warning(message)));
                return;
            }
            if (_ba.Action == LaunchAction.Uninstall || _removing)
            {
                Page("Removed", Flow(Text_(_ba.Get("DELETEDATA") == "1"
                    ? "Tausi POS Server and all its data are removed from this PC."
                    : @"Tausi POS Server is removed. Its data (C:\ProgramData\Tausi POS) and its database are kept, and come back when it's installed again.", 64)));
                return;
            }

            var port = _ba.Get("PORT");
            var addresses = Checks.Addresses();
            var list = string.Join("\r\n", addresses.Select(a => $"    http://{a}:{port}"));
            var token = Checks.SetupToken();
            var restored = !string.IsNullOrEmpty(_ba.Get("RESTOREFILE"));
            var controls = new System.Collections.Generic.List<Control>
            {
                Text_("Tausi POS Server is running. On each counter PC, install the Tausi POS terminal and enter this server's address:", 44),
                new Label { Text = list, AutoSize = false, Width = 640, Height = 22 * Math.Max(1, addresses.Count) + 8, Font = new Font("Consolas", 11f) },
            };
            if (!string.IsNullOrEmpty(token))
            {
                controls.Add(Text_("Then finish setting up the business in a browser. You'll need the setup token:", 28));
                controls.Add(new TextBox { Text = token, ReadOnly = true, Width = 200, Font = new Font("Consolas", 16f), BackColor = Color.White });
            }
            else if (restored)
                controls.Add(Text_("The backup is restored: sign in to the admin site as before.", 28));
            controls.Add(Warning("Give this PC a fixed IP address (in the router or in Windows), so the counter PCs always find it."));
            var open = Primary_(string.IsNullOrEmpty(token) ? "Open the admin site" : "Open setup in the browser");
            open.Click += (s, e) => Process.Start($"http://localhost:{port}/" + (string.IsNullOrEmpty(token) ? "admin" : "setup"));
            controls.Add(Row(open));
            Page(_ba.UpgradingFrom != null ? "Upgraded" : "Installed", Flow(controls.ToArray()));
        }

        private bool _removing;

        private void ShowRemove()
        {
            var deleteAll = new CheckBox { Text = "Also delete all data (the database, backups, settings and keys)", AutoSize = true };
            var name = new TextBox { Width = 300, Enabled = false };
            var warning = Warning("This can't be undone. Type the business name exactly as it is in Tausi POS to confirm.");
            warning.Visible = false;
            deleteAll.CheckedChanged += (s, e) => { name.Enabled = warning.Visible = deleteAll.Checked; };
            Page("Remove Tausi POS Server", Flow(
                Text_(@"This removes the service and the program. The data in C:\ProgramData\Tausi POS and the database are kept unless you tick the box.", 44),
                deleteAll, warning, Row(Label_("Business name"), name)));
            _back.Visible = false;
            _next.Text = "Remove";
            _next.Enabled = true;
            _onNext = () =>
            {
                if (deleteAll.Checked && string.IsNullOrWhiteSpace(name.Text))
                {
                    MessageBox.Show(this, "Type the business name to delete all data.", Text);
                    return;
                }
                _ba.Set("DELETEDATA", deleteAll.Checked ? "1" : "0");
                _ba.Set("BUSINESSNAME", name.Text.Trim());
                _removing = true;
                ShowRunning("Removing");
                _ba.Start(LaunchAction.Uninstall);
            };
        }

        // ---- Layout helpers ----

        private void Page(string title, params Control[] controls)
        {
            _title.Text = title;
            _body.SuspendLayout();
            _body.Controls.Clear();
            foreach (var c in controls.Reverse()) _body.Controls.Add(c);
            _body.ResumeLayout();
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            flow.Controls.AddRange(controls);
            return flow;
        }

        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            row.Controls.AddRange(controls);
            return row;
        }

        private static FlowLayoutPanel Group(params Control[] controls)
        {
            var group = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(24, 0, 0, 6) };
            group.Controls.AddRange(controls);
            return group;
        }

        private static Label Text_(string text, int height = 24) =>
            new Label { Text = text, AutoSize = false, Width = 660, Height = height, Dock = DockStyle.Top };

        private static Label Label_(string text) =>
            new Label { Text = text, AutoSize = true, Margin = new Padding(0, 7, 6, 0) };

        private static Label Warning(string text) =>
            new Label { Text = text, AutoSize = false, Width = 640, Height = 52, BackColor = WarningTint, ForeColor = WarningText, Padding = new Padding(8), Margin = new Padding(0, 8, 0, 8) };

        private static Button Primary_(string text) =>
            new Button { Text = text, AutoSize = true, MinimumSize = new Size(120, 40), BackColor = Primary, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0) };

        private static Button Secondary(string text) =>
            new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 40), BackColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(8, 0, 0, 0) };

        private static string Licence()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE.txt"))
                return stream == null ? "" : new StreamReader(stream).ReadToEnd().Replace("\n", "\r\n");
        }
    }
}
