using System;
using System.Linq;
using System.Windows.Forms;
using WixToolset.BootstrapperApplicationApi;

namespace Tausi.Setup
{
    /// <summary>
    /// Drives the bundle: detect, then (with the screens, or straight away for /quiet and /passive) plan and
    /// apply. Every step answers through an engine event, so nothing here waits: the message loop keeps
    /// running and the next step starts from the event.
    /// </summary>
    public sealed class TausiBootstrapper : BootstrapperApplication
    {
        private IEngine _engine;
        private IBootstrapperCommand _command;
        private SetupForm _form;
        private ApplicationContext _quiet;
        private Control _marshal;
        /// <summary>The window Apply runs under (WiX needs one, even with no screens): the form's, or the hidden control's.</summary>
        private IntPtr _window;
        private LaunchAction _planned;

        public int ExitCode { get; private set; }

        /// <summary>The installed version this upgrades, when it does.</summary>
        public string UpgradingFrom { get; private set; }

        /// <summary>Whether this bundle's own version is already installed (then it can only be removed).</summary>
        public bool Installed { get; private set; }

        public IEngine Engine => _engine;
        public LaunchAction Action => _command.Action;
        public bool Silent => _command.Display == Display.None || _command.Display == Display.Passive || _command.Display == Display.Embedded;

        public event Action<int> Progressed;
        public event Action<string> Status;
        public event Action<int, string> Finished;

        protected override void OnCreate(CreateEventArgs args)
        {
            base.OnCreate(args);
            _engine = args.Engine;
            _command = args.Command;
        }

        protected override void Run()
        {
            // NAME=value on the command line, e.g. /quiet DB=existing SQLINSTANCE=.\SQLEXPRESS PORT=5080.
            foreach (var pair in _command.ParseCommandLine().Variables)
                _engine.SetVariableString(pair.Key, pair.Value, false);

            // The WiX library calls Run on its own UI thread (single-threaded apartment), as Windows Forms needs.
            RunScreens();
            _engine.Quit(ExitCode);
        }

        private void RunScreens()
        {
            if (Silent)
            {
                _quiet = new ApplicationContext();
                // Engine events come on another thread; this hidden control brings them to the loop's.
                _marshal = new Control();
                _marshal.CreateControl();
                _window = _marshal.Handle;
                _engine.Detect();
                Application.Run(_quiet);
            }
            else
            {
                Application.EnableVisualStyles();
                _form = new SetupForm(this);
                // Detect once the window exists, so its answer can be shown.
                _form.Shown += (s, e) =>
                {
                    _window = _form.Handle;
                    _engine.Detect();
                };
                Application.Run(_form);
            }
        }

        public string Get(string name) => _engine.ContainsVariable(name) ? _engine.GetVariableString(name) : "";

        public void Set(string name, string value) => _engine.SetVariableString(name, value ?? "", false);

        public void Log(string message) => _engine.Log(LogLevel.Standard, "TausiSetup: " + message);

        protected override void OnDetectRelatedBundle(DetectRelatedBundleEventArgs args)
        {
            base.OnDetectRelatedBundle(args);
            if (args.RelationType == RelationType.Upgrade) UpgradingFrom = args.Version;
        }

        protected override void OnDetectPackageComplete(DetectPackageCompleteEventArgs args)
        {
            base.OnDetectPackageComplete(args);
            if (args.PackageId == "TausiServer" && args.State == PackageState.Present) Installed = true;
        }

        protected override void OnDetectComplete(DetectCompleteEventArgs args)
        {
            base.OnDetectComplete(args);
            // An upgrade keeps the port the server already uses.
            var installedPort = Get("InstalledPort");
            if (!string.IsNullOrEmpty(installedPort) && UpgradingFrom != null) Set("PORT", installedPort);
            if (Get("DB") == "existing" || Get("DB") == "sqlexpress")
                Set("SQLSERVICEACCOUNT", Checks.SqlServiceAccount(Get("SQLINSTANCE")));

            if (Silent)
            {
                // /quiet DB=existing SQLGRANTSYSTEM=1: let the installer (SYSTEM) set up the database there.
                if (_command.Action != LaunchAction.Uninstall && Get("DB") == "existing" && Get("SQLUSER") == "" && Get("SQLGRANTSYSTEM") == "1")
                    Log(Checks.GrantSystem(Get("SQLINSTANCE"), out _));
                Start(_command.Action == LaunchAction.Uninstall ? LaunchAction.Uninstall : LaunchAction.Install);
                return;
            }
            _form.BeginInvoke(new Action(_form.Detected));
        }

        /// <summary>Plans, then applies when the plan is ready.</summary>
        public void Start(LaunchAction action)
        {
            _planned = action;
            Log($"planning {action}");
            _engine.Plan(action);
        }

        protected override void OnPlanComplete(PlanCompleteEventArgs args)
        {
            base.OnPlanComplete(args);
            if (args.Status < 0)
            {
                Finish(args.Status, "The installer couldn't plan the " + (_planned == LaunchAction.Uninstall ? "removal." : "install."));
                return;
            }
            try
            {
                _engine.Apply(_window);
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                // Never wait for an apply that didn't start.
                Log("apply failed to start: " + e.Message);
                Finish(e.HResult, FailureText(e.HResult));
            }
        }

        protected override void OnPlanPackageBegin(PlanPackageBeginEventArgs args)
        {
            base.OnPlanPackageBegin(args);
            // SQL Server Express only when that's the choice, and never removed with Tausi POS.
            if (args.PackageId == "SqlExpress" && _planned == LaunchAction.Uninstall) args.State = RequestState.None;
        }

        protected override void OnCacheAcquireResolving(CacheAcquireResolvingEventArgs args)
        {
            base.OnCacheAcquireResolving(args);
            Status?.Invoke("Preparing " + args.PackageOrContainerId + "…");
        }

        protected override void OnExecutePackageBegin(ExecutePackageBeginEventArgs args)
        {
            base.OnExecutePackageBegin(args);
            Status?.Invoke(args.PackageId == "SqlExpress"
                ? "Installing SQL Server 2022 Express (this takes a few minutes)…"
                : _planned == LaunchAction.Uninstall ? "Removing Tausi POS Server…" : "Installing Tausi POS Server: database, service and first start…");
        }

        protected override void OnExecuteMsiMessage(ExecuteMsiMessageEventArgs args)
        {
            base.OnExecuteMsiMessage(args);
            if (args.MessageType == InstallMessage.ActionStart && !string.IsNullOrEmpty(args.Message))
            {
                var text = args.Message.Split(new[] { '.' }, 2).Skip(1).FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(text)) Status?.Invoke(text);
            }
        }

        protected override void OnProgress(ProgressEventArgs args)
        {
            base.OnProgress(args);
            Progressed?.Invoke(args.OverallPercentage);
        }

        protected override void OnError(ErrorEventArgs args)
        {
            base.OnError(args);
            Log($"error {args.ErrorCode} in {args.PackageId}: {args.ErrorMessage}");
            if (!string.IsNullOrEmpty(args.ErrorMessage)) Status?.Invoke(args.ErrorMessage);
        }

        protected override void OnApplyComplete(ApplyCompleteEventArgs args)
        {
            base.OnApplyComplete(args);
            Finish(args.Status, args.Status >= 0 ? null : FailureText(args.Status));
        }

        private static string FailureText(int status) => status == unchecked((int)0x800704C7)
            ? "The install was cancelled. Nothing was changed."
            : $"The install didn't finish (0x{status:X8}). Anything it changed was put back. " +
              @"The log is in %TEMP% (Tausi_POS_Server_*.log), and the server's own in C:\ProgramData\Tausi POS\logs.";

        private void Finish(int status, string message)
        {
            ExitCode = status;
            if (_quiet != null)
            {
                _marshal.BeginInvoke(new Action(_quiet.ExitThread));
                return;
            }
            Finished?.Invoke(status, message);
        }
    }
}
