using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RouterSpeed;

/// <summary>Manages one Task Scheduler logon task for the current interactive Windows user.</summary>
public sealed class StartupRegistration
{
    private const int InteractiveToken = 3;
    private const int CreateOrUpdate = 6;
    private const int LogonTrigger = 9;
    private const int IgnoreNewInstance = 2;
    private const int FullControl = 0x001f01ff;
    private readonly string _taskName;
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string _userSid;

    public StartupRegistration()
    {
        _userSid = CurrentUserSid();
        _taskName = "RouterSpeed-" + _userSid;
        _executablePath = Path.GetFullPath(Application.ExecutablePath);
        _workingDirectory = Path.GetDirectoryName(_executablePath)!;
    }

    /// <summary>Returns the actual enabled task state; a missing or mismatched task is a successful false result.</summary>
    public bool TryGetEnabled(out bool enabled, out string? error)
    {
        enabled = false;
        error = null;
        try
        {
            using var scope = new ComScope();
            dynamic service = Connect(scope);
            dynamic folder = scope.Track((object)service.GetFolder("\\"));
            object? task = FindTask((object)folder, scope);
            enabled = task is not null && MatchesTask(task, scope);
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            error = TaskError(ex, reading: true);
            return false;
        }
    }

    /// <summary>Creates a verified ordinary-user logon task, or removes this user's task.</summary>
    public bool TrySetEnabled(bool enabled, out string? error) =>
        enabled ? TryRegisterTask(out error) : TryDeleteTask(out error);

    private bool TryRegisterTask(out string? error)
    {
        error = null;
        try
        {
            using var scope = new ComScope();
            dynamic service = Connect(scope);
            dynamic folder = scope.Track((object)service.GetFolder("\\"));
            dynamic definition = scope.Track((object)service.NewTask(0));
            dynamic registration = scope.Track((object)definition.RegistrationInfo);
            registration.Author = _userSid;
            registration.Description = "登录当前 Windows 用户后显示 RouterSpeed 网速条。";
            dynamic principal = scope.Track((object)definition.Principal);
            principal.UserId = _userSid;
            principal.LogonType = InteractiveToken;
            principal.RunLevel = 0; // TASK_RUNLEVEL_LUA: ordinary interactive token, never elevated.
            dynamic settings = scope.Track((object)definition.Settings);
            settings.Enabled = true;
            settings.Hidden = false;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.RunOnlyIfIdle = false;
            settings.RunOnlyIfNetworkAvailable = false;
            settings.WakeToRun = false;
            settings.ExecutionTimeLimit = "PT0S";
            settings.MultipleInstances = IgnoreNewInstance;
            dynamic idle = scope.Track((object)settings.IdleSettings);
            idle.StopOnIdleEnd = false;
            idle.RestartOnIdle = false;
            dynamic triggers = scope.Track((object)definition.Triggers);
            dynamic trigger = scope.Track((object)triggers.Create(LogonTrigger));
            trigger.UserId = _userSid;
            trigger.Delay = "PT10S";
            trigger.Enabled = true;
            dynamic actions = scope.Track((object)definition.Actions);
            dynamic action = scope.Track((object)actions.Create(0));
            // ExecAction.Path is a dedicated executable field, not a shell command.
            action.Path = _executablePath;
            action.WorkingDirectory = _workingDirectory;
            action.Arguments = "--startup";
            string security = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{_userSid})";
            scope.Track((object)folder.RegisterTaskDefinition(_taskName, definition, CreateOrUpdate,
                _userSid, null, InteractiveToken, security));
            object? saved = FindTask((object)folder, scope);
            if (saved is null || !MatchesTask(saved, scope) || !HasRequiredPermissions(saved))
            {
                error = "未能确认登录启动任务及权限已正确保存，请稍后重试。";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            error = TaskError(ex, reading: false);
            return false;
        }
    }

    private bool TryDeleteTask(out string? error)
    {
        error = null;
        try
        {
            using var scope = new ComScope();
            dynamic service = Connect(scope);
            dynamic folder = scope.Track((object)service.GetFolder("\\"));
            if (FindTask((object)folder, scope) is null) return true;
            try { folder.DeleteTask(_taskName, 0); }
            catch (Exception ex) when (IsMissingTask(ex)) { }
            if (FindTask((object)folder, scope) is not null)
            {
                error = "未能确认登录启动任务已移除，请稍后重试。";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            error = TaskError(ex, reading: false);
            return false;
        }
    }

    private object? FindTask(object rootFolder, ComScope scope)
    {
        dynamic folder = rootFolder;
        try { return scope.Track((object)folder.GetTask(_taskName)); }
        catch (Exception ex) when (IsMissingTask(ex)) { return null; }
    }

    private bool MatchesTask(object registeredTask, ComScope scope)
    {
        dynamic task = registeredTask;
        if (!(bool)task.Enabled) return false;
        dynamic definition = scope.Track((object)task.Definition);
        dynamic principal = scope.Track((object)definition.Principal);
        if (!MatchesSid((string)principal.UserId) || (int)principal.LogonType != InteractiveToken || (int)principal.RunLevel != 0)
            return false;
        dynamic settings = scope.Track((object)definition.Settings);
        if (!(bool)settings.Enabled || !(bool)settings.StartWhenAvailable || !(bool)settings.AllowDemandStart ||
            (bool)settings.DisallowStartIfOnBatteries || (bool)settings.StopIfGoingOnBatteries ||
            (bool)settings.RunOnlyIfIdle || (bool)settings.RunOnlyIfNetworkAvailable ||
            (int)settings.MultipleInstances != IgnoreNewInstance || (string)settings.ExecutionTimeLimit != "PT0S")
            return false;
        dynamic triggers = scope.Track((object)definition.Triggers);
        dynamic actions = scope.Track((object)definition.Actions);
        if ((int)triggers.Count != 1 || (int)actions.Count != 1) return false;
        dynamic trigger = scope.Track((object)triggers[1]);
        dynamic action = scope.Track((object)actions[1]);
        return (int)trigger.Type == LogonTrigger && (bool)trigger.Enabled && MatchesSid((string)trigger.UserId) &&
            (string)trigger.Delay == "PT10S" && (int)action.Type == 0 &&
            string.Equals((string)action.Path, _executablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)action.WorkingDirectory, _workingDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)action.Arguments, "--startup", StringComparison.Ordinal);
    }

    private bool MatchesSid(string value)
    {
        if (string.Equals(value, _userSid, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return string.Equals(((SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier))).Value,
                _userSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private bool HasRequiredPermissions(object registeredTask)
    {
        dynamic task = registeredTask;
        var security = new RawSecurityDescriptor((string)task.GetSecurityDescriptor(4)); // DACL_SECURITY_INFORMATION.
        if (security.DiscretionaryAcl is not { } acl) return false;
        string[] required = [_userSid, "S-1-5-18", "S-1-5-32-544"];
        return required.All(sid => acl.Cast<GenericAce>().OfType<CommonAce>().Any(ace =>
            ace.AceQualifier == AceQualifier.AccessAllowed && (ace.AccessMask & FullControl) == FullControl &&
            string.Equals(ace.SecurityIdentifier.Value, sid, StringComparison.OrdinalIgnoreCase)));
    }

    private static object Connect(ComScope scope)
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = scope.Track(Activator.CreateInstance(type)!);
        service.Connect();
        return service;
    }

    private static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("无法确认当前 Windows 用户。");
    }

    // COM interop may project HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) as either
    // COMException or FileNotFoundException. Only this exact error means absent.
    private static bool IsMissingTask(Exception exception) => exception.HResult == unchecked((int)0x80070002);
    private static bool IsExpectedFailure(Exception exception) => exception is COMException or UnauthorizedAccessException or
        SecurityException or IOException or ArgumentException or ObjectDisposedException or InvalidOperationException or
        Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;
    private static string TaskError(Exception exception, bool reading)
    {
        if (exception is UnauthorizedAccessException or SecurityException || exception.HResult == unchecked((int)0x80070005))
            return "没有权限访问本用户的登录启动任务，请检查任务权限。";
        return reading ? "暂时无法读取 Windows 登录启动任务，请稍后重试。" :
            "暂时无法更新 Windows 登录启动任务，请确认任务计划程序服务可用后重试。";
    }

    private sealed class ComScope : IDisposable
    {
        private readonly List<object> _objects = [];
        private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
        public object Track(object value)
        {
            if (_seen.Add(value)) _objects.Add(value);
            return value;
        }
        public void Dispose()
        {
            for (int index = _objects.Count - 1; index >= 0; index--)
            {
                try { if (Marshal.IsComObject(_objects[index])) Marshal.FinalReleaseComObject(_objects[index]); }
                catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException or COMException) { }
            }
            _objects.Clear();
            _seen.Clear();
        }
    }
}
