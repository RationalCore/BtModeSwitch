using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BtModeSwitch
{
    internal enum Mode { Mic = 0, Listen = 1, Off = 2 }

    internal static class EndpointFinder
    {
        private const string Desc = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
        private const string Friendly = "{a45c254e-df1c-4efd-8020-67d146a850e0},14";
        private const string DeviceDesc = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
        private const string DevicePath = "{b3f8fa53-0004-438e-9003-51a46e139bfc},2";

        public static List<EndpointInfo> Find(bool render)
        {
            var list = new List<EndpointInfo>();
            string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" + (render ? "Render" : "Capture");
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(path))
            {
                if (root == null) return list;
                foreach (string guid in root.GetSubKeyNames())
                {
                    int state = 0;
                    using (RegistryKey key = root.OpenSubKey(guid))
                    {
                        object raw = key == null ? null : key.GetValue("DeviceState");
                        if (raw is int) state = (int)raw;
                    }
                    string text = "";
                    using (RegistryKey props = root.OpenSubKey(guid + @"\Properties"))
                    {
                        if (props != null)
                        {
                            text = string.Join(" | ", new string[] {
                                Str(props.GetValue(Friendly)),
                                Str(props.GetValue(Desc)),
                                Str(props.GetValue(DeviceDesc)),
                                Str(props.GetValue(DevicePath))
                            });
                        }
                    }
                    list.Add(new EndpointInfo(guid, text, state));
                }
            }
            return list;
        }

        private static string Str(object value) { return value == null ? "" : value.ToString(); }
    }

    internal sealed class EndpointInfo
    {
        public readonly string Guid;
        public readonly string Text;
        public readonly int State;

        public EndpointInfo(string guid, string text, int state)
        {
            Guid = guid;
            Text = text;
            State = state;
        }

        public bool Active { get { return (State & 1) != 0; } }
        public bool Matches(string part) { return Text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }
        public string Id(bool render) { return (render ? "{0.0.0.00000000}." : "{0.0.1.00000000}.") + Guid; }
    }

    internal static class CoreAudio
    {
        private static readonly Guid IID_IAudioEndpointVolume = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
            int OpenPropertyStore(int stgmAccess, out IntPtr properties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            int GetState(out int state);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out int count);
            int SetMasterVolumeLevel(float level, ref Guid ctx);
            int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
            int GetMasterVolumeLevel(out float level);
            int GetMasterVolumeLevelScalar(out float level);
            int SetChannelVolumeLevel(int channel, float level, ref Guid ctx);
            int SetChannelVolumeLevelScalar(int channel, float level, ref Guid ctx);
            int GetChannelVolumeLevel(int channel, out float level);
            int GetChannelVolumeLevelScalar(int channel, out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
            int GetVolumeStepInfo(out int step, out int stepCount);
            int VolumeStepUp(ref Guid ctx);
            int VolumeStepDown(ref Guid ctx);
            int QueryHardwareSupport(out int mask);
            int GetVolumeRange(out float min, out float max, out float inc);
        }

        private static IAudioEndpointVolume Open(string id)
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            try
            {
                int hr = enumerator.GetDevice(id, out device);
                if (hr != 0) throw new COMException("GetDevice failed", hr);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }

            object raw;
            Guid iid = IID_IAudioEndpointVolume;
            try
            {
                int hr = device.Activate(ref iid, 23, IntPtr.Zero, out raw);
                if (hr != 0) throw new COMException("Activate failed", hr);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
            return (IAudioEndpointVolume)raw;
        }

        public static bool TryGetMute(string id, out bool mute)
        {
            mute = false;
            IAudioEndpointVolume volume = null;
            try
            {
                volume = Open(id);
                bool value;
                if (volume.GetMute(out value) != 0) return false;
                mute = value;
                return true;
            }
            catch { return false; }
            finally { if (volume != null) Marshal.ReleaseComObject(volume); }
        }

        public static bool TrySetMute(string id, bool mute)
        {
            IAudioEndpointVolume volume = null;
            try
            {
                volume = Open(id);
                Guid ctx = Guid.Empty;
                return volume.SetMute(mute, ref ctx) == 0;
            }
            catch { return false; }
            finally { if (volume != null) Marshal.ReleaseComObject(volume); }
        }
    }

    // Управление окном "M54" (BtMgr.exe).
    // ID контролов у окна меняются при перестройке, поэтому ищем по подписи строки и положению.
    internal static class BtMgrUi
    {
        public const string HeadsetLabel = "Bluetooth Headset and Microphone";
        public const string AudioLabel = "Bluetooth Advanced Audio";
        public const string GaiaLabel = "GAIA";

        private const uint WM_GETTEXT = 0x000D;
        private const uint BM_CLICK = 0x00F5;

        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageText(IntPtr hwnd, uint msg, IntPtr wparam, StringBuilder lparam);
        [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageValue(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);

        private sealed class Element
        {
            public IntPtr Handle;
            public string ClassName;
            public string Text;
            public int Top;
            public bool IsConnectButton
            {
                get
                {
                    return ClassName == "Button" &&
                        (Text.StartsWith("Connect", StringComparison.OrdinalIgnoreCase) ||
                         Text.StartsWith("Disconnect", StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        public static IntPtr FindWindow()
        {
            var pids = new List<uint>();
            foreach (Process process in Process.GetProcessesByName("BtMgr"))
            {
                pids.Add((uint)process.Id);
                process.Dispose();
            }
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hwnd, IntPtr param)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (!pids.Contains(pid)) return true;
                if (!IsWindowVisible(hwnd)) return true;
                var title = new StringBuilder(64);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.Length == 0) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static string TextOf(IntPtr hwnd)
        {
            var text = new StringBuilder(256);
            SendMessageText(hwnd, WM_GETTEXT, (IntPtr)text.Capacity, text);
            return text.ToString();
        }

        private static List<Element> Children(IntPtr root)
        {
            var list = new List<Element>();
            EnumChildWindows(root, delegate(IntPtr hwnd, IntPtr param)
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, cls.Capacity);
                string name = cls.ToString();
                if (name == "Button" || name == "Static")
                {
                    var element = new Element();
                    element.Handle = hwnd;
                    element.ClassName = name;
                    element.Text = TextOf(hwnd);
                    RECT rect;
                    if (GetWindowRect(hwnd, out rect)) element.Top = rect.Top;
                    list.Add(element);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static IntPtr RowButton(List<Element> elements, string label)
        {
            Element row = null;
            foreach (Element element in elements)
            {
                if (element.ClassName == "Static" && element.Text == label) { row = element; break; }
            }
            if (row == null) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            int bestDistance = 40;
            foreach (Element element in elements)
            {
                if (!element.IsConnectButton) continue;
                int distance = Math.Abs(element.Top - row.Top);
                if (distance < bestDistance) { bestDistance = distance; best = element.Handle; }
            }
            return best;
        }

        private static IntPtr ButtonByText(List<Element> elements, string prefix)
        {
            foreach (Element element in elements)
            {
                if (element.ClassName == "Button" && element.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return element.Handle;
            }
            return IntPtr.Zero;
        }

        private static string StatusText(List<Element> elements)
        {
            foreach (Element element in elements)
            {
                if (element.ClassName == "Static" && element.Text.IndexOf(", Paired", StringComparison.OrdinalIgnoreCase) >= 0)
                    return element.Text;
            }
            return "";
        }

        public static bool DeviceIdle()
        {
            IntPtr window = FindWindow();
            if (window == IntPtr.Zero) return false;
            return StatusText(Children(window)).StartsWith("Idle", StringComparison.OrdinalIgnoreCase);
        }

        public static void TrySearchService()
        {
            IntPtr window = FindWindow();
            if (window == IntPtr.Zero) return;
            IntPtr button = ButtonByText(Children(window), "Search service");
            if (button != IntPtr.Zero) SendMessageValue(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        }

        public static string EnsureProfile(string label, bool connected)
        {
            IntPtr window = FindWindow();
            if (window == IntPtr.Zero) return "окно M54 не открыто";
            List<Element> elements = Children(window);
            IntPtr button = RowButton(elements, label);
            if (button == IntPtr.Zero) return "профиль не найден: " + label;
            string text = TextOf(button);
            bool isConnected = text.StartsWith("Disconnect", StringComparison.OrdinalIgnoreCase);
            if (isConnected == connected) return null;
            SendMessageValue(button, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(900);
            return null;
        }

        public static string Describe()
        {
            IntPtr window = FindWindow();
            if (window == IntPtr.Zero) return "BtMgr: окно не найдено" + Environment.NewLine;
            List<Element> elements = Children(window);
            var sb = new StringBuilder();
            sb.AppendLine("device: " + StatusText(elements));
            foreach (string label in new string[] { HeadsetLabel, AudioLabel, GaiaLabel })
            {
                IntPtr button = RowButton(elements, label);
                sb.AppendLine(label + ": " + (button == IntPtr.Zero ? "не найден" : TextOf(button)));
            }
            IntPtr mute = ButtonByText(elements, "Mute");
            sb.AppendLine("mic mute: " + (mute == IntPtr.Zero ? "не найден" : TextOf(mute)));
            return sb.ToString();
        }
    }
    internal static class BrLinkTray
    {
        private const string ExePath = @"C:\Program Files (x86)\Barrot Technology Limited\BRLink\BRLinkTray.exe";
        private static readonly object Gate = new object();

        public static void EnsureRunning()
        {
            lock (Gate)
            {
                if (IsRunning()) return;
                try
                {
                    Process process = Process.Start(ExePath);
                    if (process != null) process.Dispose();
                    Program.Log("BRLinkTray started");
                }
                catch (Exception error)
                {
                    Program.Log("BRLinkTray start failed: " + error.Message);
                }
            }
        }

        private static bool IsRunning()
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(ExePath);
            foreach (Process process in Process.GetProcessesByName(name))
            {
                try
                {
                    string path = process.MainModule.FileName;
                    if (string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch
                {
                    // Имя совпало, но путь недоступен — считаем, что нужный процесс уже запущен.
                    return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }
    }

    internal static class Switch
    {
        public const string NamePart = "BRLINK";

        // Переключения могут прилетать подряд; каждый запуск получает номер,
        // и устаревший запуск не имеет права переписывать состояние более нового.
        private static readonly object Gate = new object();
        private static int _generation;

        public static string Apply(Mode mode)
        {
            BrLinkTray.EnsureRunning();
            int generation;
            lock (Gate) { generation = ++_generation; }

            // Мьюты применяем сразу: работа с профилями занимает секунды,
            // а результат нужен пользователю немедленно.
            ApplyMutes(mode);

            var notes = new List<string>();
            bool windowOpen = BtMgrUi.FindWindow() != IntPtr.Zero;
            if (windowOpen && mode != Mode.Off && BtMgrUi.DeviceIdle())
            {
                BtMgrUi.TrySearchService();
                for (int i = 0; i < 10; i++)
                {
                    Thread.Sleep(1000);
                    if (!BtMgrUi.DeviceIdle()) break;
                    if (!IsCurrent(generation)) return string.Join("; ", notes.ToArray());
                }
            }
            bool idle = windowOpen && BtMgrUi.DeviceIdle();

            if (!IsCurrent(generation)) return string.Join("; ", notes.ToArray());

            if (!windowOpen) notes.Add("окно M54 не открыто");

            if (windowOpen && !idle)
            {
                if (mode == Mode.Off)
                {
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.HeadsetLabel, false));
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.AudioLabel, false));
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.GaiaLabel, false));
                }
                else if (mode == Mode.Mic)
                {
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.HeadsetLabel, true));
                }
                else
                {
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.AudioLabel, true));
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.HeadsetLabel, false));
                    Add(notes, BtMgrUi.EnsureProfile(BtMgrUi.AudioLabel, true));
                }
            }
            else if (idle && mode != Mode.Off)
            {
                notes.Add("M54 не подключён (включи гарнитуру)");
            }

            if (IsCurrent(generation)) ApplyMutes(mode);
            Program.Log("mutes " + mode + ": " + MuteLine());
            return string.Join("; ", notes.ToArray());
        }

        private static bool IsCurrent(int generation)
        {
            lock (Gate) { return generation == _generation; }
        }

        private static void ApplyMutes(Mode mode)
        {
            if (mode == Mode.Off)
            {
                SetMute(true, false);
                SetMute(false, false);
            }
            else if (mode == Mode.Mic)
            {
                SetMute(true, true);
                SetMute(false, false, 8);
            }
            else
            {
                SetMute(true, false, 8);
                SetMute(false, true);
            }
        }

        private static string MuteLine()
        {
            return "render=" + EndpointState(true) + " capture=" + EndpointState(false);
        }

        private static string EndpointState(bool render)
        {
            foreach (EndpointInfo ep in EndpointFinder.Find(render))
            {
                if (!ep.Active || !ep.Matches(NamePart)) continue;
                bool mute;
                if (CoreAudio.TryGetMute(ep.Id(render), out mute)) return mute ? "MUTED" : "unmuted";
            }
            return "n/a";
        }

        private static void Add(List<string> notes, string error)
        {
            if (error != null) notes.Add(error);
        }

        private static int SetMute(bool render, bool mute)
        {
            return SetMute(render, mute, 1);
        }

        // Endpoint BRLINK появляется с задержкой после подключения профиля,
        // поэтому нужное состояние доводим несколькими попытками.
        private static int SetMute(bool render, bool mute, int attempts)
        {
            int count = 0;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) Thread.Sleep(400);
                count = 0;
                foreach (EndpointInfo ep in EndpointFinder.Find(render))
                {
                    if (!ep.Active || !ep.Matches(NamePart)) continue;
                    if (CoreAudio.TrySetMute(ep.Id(render), mute)) count++;
                }
                if (count > 0) return count;
            }
            return count;
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            Append(sb, true);
            Append(sb, false);
            sb.Append(BtMgrUi.Describe());
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, bool render)
        {
            bool any = false;
            foreach (EndpointInfo ep in EndpointFinder.Find(render))
            {
                if (!ep.Matches(NamePart)) continue;
                bool mute;
                string state = CoreAudio.TryGetMute(ep.Id(render), out mute) ? (mute ? "MUTED" : "unmuted") : "n/a";
                sb.AppendLine((render ? "Render  " : "Capture ") + ep.Guid + "  state=" + ep.State + "  " + state);
                any = true;
            }
            if (!any) sb.AppendLine((render ? "Render  " : "Capture ") + "BRLINK: not found");
        }
    }

    // Наблюдает за клавишей через WH_KEYBOARD_LL и НЕ съедает её:
    // другие программы продолжают получать эту клавишу.
    internal sealed class HotkeyWatcher
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public int VkCode;
            public int ScanCode;
            public int Flags;
            public int Time;
            public IntPtr ExtraInfo;
        }

        private delegate IntPtr HookProc(int code, IntPtr wparam, IntPtr lparam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wparam, IntPtr lparam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        private readonly HookProc _callback;
        private IntPtr _hook = IntPtr.Zero;
        private int _virtualKey;
        private bool _isDown;

        public bool Block;

        public event Action Pressed;

        public HotkeyWatcher()
        {
            _callback = Callback;
        }

        public bool Install(int virtualKey)
        {
            Remove();
            _virtualKey = virtualKey;
            if (virtualKey == 0) return true;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(null), 0);
            return _hook != IntPtr.Zero;
        }

        public void Remove()
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            _isDown = false;
        }

        private IntPtr Callback(int code, IntPtr wparam, IntPtr lparam)
        {
            if (code >= 0 && _virtualKey != 0)
            {
                int message = wparam.ToInt32();
                KBDLLHOOKSTRUCT info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lparam, typeof(KBDLLHOOKSTRUCT));
                if (info.VkCode == _virtualKey)
                {
                    if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
                    {
                        if (!_isDown)
                        {
                            _isDown = true;
                            Action handler = Pressed;
                            if (handler != null) handler();
                        }
                    }
                    else if (message == WM_KEYUP || message == WM_SYSKEYUP)
                    {
                        _isDown = false;
                    }
                    if (Block) return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, code, wparam, lparam);
        }
    }
    internal sealed class ComboItem
    {
        public readonly int Vk;
        private readonly string _text;

        public ComboItem(int vk, string text)
        {
            Vk = vk;
            _text = text;
        }

        public override string ToString() { return _text; }
    }

    internal sealed class SettingsForm : Form
    {
        private static readonly int[] Presets = new int[] { 0x14, 0x09, 0x91, 0x13, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B };
        private const int CustomVk = -1;
        public const int DoubleMin = 150;
        public const int DoubleMax = 1000;
        public const int DoubleDefault = 400;
        private const string HintNormal = "Клавиша не блокируется: другие программы её тоже получают.";
        private const string HintCapture = "Нажмите нужную клавишу… (Esc — отмена)";

        private readonly RadioButton _last;
        private readonly RadioButton _mic;
        private readonly RadioButton _listen;
        private readonly RadioButton _off;
        private readonly CheckBox _autoStart;
        private readonly ComboBox _hotkey;
        private readonly Label _hotkeyHint;
        private readonly CheckBox _blockKey;
        private readonly CheckBox _notify;
        private readonly NumericUpDown _notifySeconds;
        private readonly CheckBox _doubleOff;
        private readonly NumericUpDown _doubleMs;
        private bool _capturing;

        public int Startup { get; private set; }
        public bool AutoStart { get; private set; }
        public int Hotkey { get; private set; }
        public bool BlockHotkey { get; private set; }
        public bool Notify { get; private set; }
        public int NotifySeconds { get; private set; }
        public bool DoubleOff { get; private set; }
        public int DoubleMs { get; private set; }

        public SettingsForm(int startup, bool autoStart, int hotkey, bool blockHotkey, bool notify, int notifySeconds, bool doubleOff, int doubleMs)
        {
            Hotkey = hotkey;
            BlockHotkey = blockHotkey;
            Notify = notify;
            NotifySeconds = notifySeconds;
            DoubleOff = doubleOff;
            DoubleMs = ClampDouble(doubleMs);

            Text = "BRLINK — настройки";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            ClientSize = new Size(460, 460);

            _autoStart = new CheckBox();
            _autoStart.Text = "Запускать при входе в систему (автозапуск)";
            _autoStart.SetBounds(14, 16, 430, 22);
            _autoStart.Checked = autoStart;
            Controls.Add(_autoStart);

            var hotkeyLabel = new Label();
            hotkeyLabel.Text = "Клавиша переключения режимов:";
            hotkeyLabel.SetBounds(16, 48, 240, 18);
            Controls.Add(hotkeyLabel);

            _hotkey = new ComboBox();
            _hotkey.DropDownStyle = ComboBoxStyle.DropDownList;
            _hotkey.SetBounds(16, 68, 240, 22);
            _hotkey.SelectedIndexChanged += delegate { OnHotkeySelected(); };
            Controls.Add(_hotkey);
            FillHotkeyItems(hotkey);

            _hotkeyHint = new Label();
            _hotkeyHint.Text = HintNormal;
            _hotkeyHint.SetBounds(266, 48, 180, 44);
            Controls.Add(_hotkeyHint);

            _blockKey = new CheckBox();
            _blockKey.Text = "Перехватывать клавишу полностью (другие программы её не получат)";
            _blockKey.SetBounds(16, 96, 430, 22);
            _blockKey.Checked = blockHotkey;
            Controls.Add(_blockKey);

            var doubleLabel = new Label();
            doubleLabel.Text = "Интервал двойного нажатия:";
            doubleLabel.SetBounds(16, 146, 170, 18);
            Controls.Add(doubleLabel);

            _doubleMs = new NumericUpDown();
            _doubleMs.Minimum = DoubleMin;
            _doubleMs.Maximum = DoubleMax;
            _doubleMs.Increment = 50;
            _doubleMs.Value = ClampDouble(doubleMs);
            _doubleMs.SetBounds(190, 144, 60, 22);
            Controls.Add(_doubleMs);

            var msLabel = new Label();
            msLabel.Text = "мс";
            msLabel.SetBounds(256, 146, 40, 18);
            Controls.Add(msLabel);

            _doubleOff = new CheckBox();
            _doubleOff.Text = "Двойное нажатие горячей клавиши отключает устройство";
            _doubleOff.SetBounds(16, 120, 430, 22);
            _doubleOff.CheckedChanged += delegate { _doubleMs.Enabled = _doubleOff.Checked; };
            _doubleOff.Checked = doubleOff;
            _doubleMs.Enabled = doubleOff;
            Controls.Add(_doubleOff);

            _notify = new CheckBox();
            _notify.Text = "Выводить всплывающие уведомления";
            _notify.SetBounds(16, 176, 430, 22);
            _notify.Checked = notify;
            Controls.Add(_notify);

            var notifyLabel = new Label();
            notifyLabel.Text = "Скрывать уведомление через:";
            notifyLabel.SetBounds(16, 206, 170, 18);
            Controls.Add(notifyLabel);

            _notifySeconds = new NumericUpDown();
            _notifySeconds.Minimum = 1;
            _notifySeconds.Maximum = 10;
            _notifySeconds.Value = notifySeconds < 1 ? 1 : (notifySeconds > 10 ? 10 : notifySeconds);
            _notifySeconds.SetBounds(190, 204, 60, 22);
            Controls.Add(_notifySeconds);

            var secondsLabel = new Label();
            secondsLabel.Text = "сек";
            secondsLabel.SetBounds(256, 206, 40, 18);
            Controls.Add(secondsLabel);

            var group = new GroupBox();
            group.Text = "При запуске приложения";
            group.SetBounds(12, 232, 436, 152);
            Controls.Add(group);

            _last = Add(group, "Последний режим (как при закрытии)", 24, startup == 0);
            _mic = Add(group, "Включить режим записи", 54, startup == 1);
            _listen = Add(group, "Включить режим прослушивания", 84, startup == 2);
            _off = Add(group, "Оставить устройство отключённым (по умолчанию)", 114, startup == 3);

            var hint = new Label();
            hint.Text = "Автоподключение сработает, если устройство спарено (Idle, Paired).";
            hint.SetBounds(14, 390, 436, 18);
            Controls.Add(hint);

            var save = new Button();
            save.Text = "Сохранить";
            save.SetBounds(256, 418, 92, 28);
            save.DialogResult = DialogResult.OK;
            save.Click += delegate
            {
                Startup = Selected();
                AutoStart = _autoStart.Checked;
                BlockHotkey = _blockKey.Checked;
                Notify = _notify.Checked;
                NotifySeconds = (int)_notifySeconds.Value;
                DoubleOff = _doubleOff.Checked;
                DoubleMs = (int)_doubleMs.Value;
                ComboItem picked = _hotkey.SelectedItem as ComboItem;
                if (picked != null && picked.Vk != CustomVk) Hotkey = picked.Vk;
            };

            var cancel = new Button();
            cancel.Text = "Отмена";
            cancel.SetBounds(354, 418, 92, 28);
            cancel.DialogResult = DialogResult.Cancel;

            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static RadioButton Add(Control parent, string text, int top, bool selected)
        {
            var radio = new RadioButton();
            radio.Text = text;
            radio.SetBounds(12, top, 396, 24);
            radio.Checked = selected;
            parent.Controls.Add(radio);
            return radio;
        }

        private int Selected()
        {
            if (_mic.Checked) return 1;
            if (_listen.Checked) return 2;
            if (_off.Checked) return 3;
            return 0;
        }

        private void FillHotkeyItems(int hotkey)
        {
            _hotkey.Items.Clear();
            _hotkey.Items.Add(new ComboItem(0, Label(0)));
            foreach (int vk in Presets) _hotkey.Items.Add(new ComboItem(vk, Label(vk)));
            if (hotkey != 0 && !IsPreset(hotkey)) _hotkey.Items.Add(new ComboItem(hotkey, Label(hotkey)));
            _hotkey.Items.Add(new ComboItem(CustomVk, "Другая клавиша…"));

            for (int i = 0; i < _hotkey.Items.Count; i++)
            {
                if (((ComboItem)_hotkey.Items[i]).Vk == hotkey) { _hotkey.SelectedIndex = i; return; }
            }
            _hotkey.SelectedIndex = 0;
        }

        private static bool IsPreset(int vk)
        {
            foreach (int item in Presets) if (item == vk) return true;
            return false;
        }

        private void OnHotkeySelected()
        {
            ComboItem item = _hotkey.SelectedItem as ComboItem;
            if (item == null || item.Vk != CustomVk) return;
            _capturing = true;
            _hotkeyHint.Text = HintCapture;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_capturing)
            {
                CaptureKey(e.KeyCode == Keys.Escape ? 0 : (int)e.KeyCode);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (_capturing)
            {
                Keys key = keyData & Keys.KeyCode;
                CaptureKey(key == Keys.Escape ? 0 : (int)key);
                return true;
            }
            return base.ProcessDialogKey(keyData);
        }

        private void CaptureKey(int vk)
        {
            _capturing = false;
            _hotkeyHint.Text = HintNormal;
            if (vk == 0) { FillHotkeyItems(Hotkey); return; }
            Hotkey = vk;
            FillHotkeyItems(vk);
        }

        private static int ClampDouble(int ms)
        {
            if (ms < DoubleMin || ms > DoubleMax) return DoubleDefault;
            return ms;
        }

        private static string Label(int vk)
        {
            if (vk == 0) return "Не используется";
            if (vk == 0x14) return "Caps Lock";
            if (vk == 0x09) return "Tab";
            if (vk == 0x91) return "Scroll Lock";
            if (vk == 0x13) return "Pause";
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            return "0x" + vk.ToString("X2");
        }
    }
    // Всплывающее уведомление у трея. Штатные balloon-подсказки на Windows 10/11
    // не соблюдают заданный таймаут, поэтому окно своё и закрываем сами.
    internal sealed class Notice : Form
    {
        private const int CornerRadius = 10;

        private static Notice _current;

        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly Font _font;

        private Notice(string text, int seconds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            _font = new Font(Font, FontStyle.Bold);
            Size textSize = TextRenderer.MeasureText(text, _font);
            Size = new Size(textSize.Width + 30, textSize.Height + 20);

            var label = new Label();
            label.Text = text;
            label.Font = _font;
            label.ForeColor = Color.FromArgb(64, 64, 64);
            label.BackColor = Color.Transparent;
            label.SetBounds(15, 10, textSize.Width, textSize.Height);
            Controls.Add(label);

            _timer.Interval = seconds * 1000;
            _timer.Tick += delegate { Close(); };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width, Height), CornerRadius))
            {
                Region = new Region(path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(205, 205, 205)))
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), CornerRadius))
            {
                e.Graphics.DrawPath(pen, path);
            }
            base.OnPaint(e);
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public static void Popup(string text, int seconds)
        {
            if (_current != null && !_current.IsDisposed) _current.Close();
            var notice = new Notice(text, seconds);
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            notice.Location = new Point(work.Right - notice.Width - 12, work.Bottom - notice.Height - 12);
            _current = notice;
            notice.Show();
            notice._timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayApp : IDisposable
    {
        private readonly NotifyIcon _icon = new NotifyIcon();
        private readonly ToolStripMenuItem _micItem = new ToolStripMenuItem("Режим записи  (HFP, вывод выключен)");
        private readonly ToolStripMenuItem _listenItem = new ToolStripMenuItem("Режим прослушивания  (A2DP, микрофон выключен)");
        private readonly Icon _micIcon = MakeIcon(true);
        private readonly Icon _listenIcon = MakeIcon(false);
        private readonly Icon _offIcon = MakeOffIcon();
        private readonly ToolStripMenuItem _offItem = new ToolStripMenuItem("Отключить устройство");
        private readonly ToolStripMenuItem _settingsItem = new ToolStripMenuItem("Настройки...");
        private Mode _mode;
        private Mode _lastOn = Mode.Listen;
        private SynchronizationContext _context;
        private HotkeyWatcher _hotkeyWatcher;
        private bool _hasMode;
        private bool _notifyEnabled;
        private int _notifySeconds;
        private bool _doubleOff;
        private int _doubleMs;
        private System.Windows.Forms.Timer _hotkeyTimer;
        private bool _hotkeyPending;

        public void Start()
        {
            BrLinkTray.EnsureRunning();
            _micItem.Click += delegate { SetMode(Mode.Mic); };
            _listenItem.Click += delegate { SetMode(Mode.Listen); };
            _offItem.Click += delegate { SetMode(Mode.Off); };
            _settingsItem.Click += delegate { EditSettings(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(_micItem);
            menu.Items.Add(_listenItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_offItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Выход");
            exit.Click += delegate { Application.Exit(); };
            menu.Items.Add(exit);

            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;

            _icon.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (e.Clicks >= 2) return;
                EditSettings();
            };

            _context = SynchronizationContext.Current;
            _notifyEnabled = ReadNotify();
            _notifySeconds = ReadNotifySeconds();
            _doubleOff = ReadDoubleOff();
            _doubleMs = ReadDoubleMs();
            _hotkeyTimer = new System.Windows.Forms.Timer();
            _hotkeyTimer.Tick += delegate { FlushHotkey(); };
            ApplyAutoStart(ReadAutoStart());
            ApplyHotkey(ReadHotkey(), ReadBlockHotkey());

            int startup = ReadStartup();
            if (startup == 0)
            {
                int? saved = ReadSavedMode();
                // Сохранённого режима нет — фактически отключаем устройство,
                // это и есть состояние по умолчанию.
                if (saved.HasValue) SetMode((Mode)saved.Value, false);
                else SetMode(Mode.Off, false);
            }
            else
            {
                SetMode((Mode)(startup - 1), false);
            }
        }

        private void EditSettings()
        {
            using (var form = new SettingsForm(ReadStartup(), ReadAutoStart(), ReadHotkey(), ReadBlockHotkey(), ReadNotify(), ReadNotifySeconds(), ReadDoubleOff(), ReadDoubleMs()))
            {
                if (form.ShowDialog() != DialogResult.OK) return;
                SaveStartup(form.Startup);
                SaveAutoStart(form.AutoStart);
                ApplyAutoStart(form.AutoStart);
                SaveHotkey(form.Hotkey);
                SaveBlockHotkey(form.BlockHotkey);
                ApplyHotkey(form.Hotkey, form.BlockHotkey);
                SaveNotify(form.Notify);
                SaveNotifySeconds(form.NotifySeconds);
                SaveDoubleOff(form.DoubleOff);
                SaveDoubleMs(form.DoubleMs);
                _notifyEnabled = form.Notify;
                _notifySeconds = form.NotifySeconds;
                _doubleOff = form.DoubleOff;
                _doubleMs = form.DoubleMs;
            }
        }

        public static int ReadHotkey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("Hotkey");
                    if (value is int && (int)value >= 0 && (int)value <= 255) return (int)value;
                }
            }
            catch { }
            return 0;
        }

        public static bool ReadBlockHotkey()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("BlockHotkey");
                    if (value is int) return (int)value != 0;
                }
            }
            catch { }
            return false;
        }

        internal static void SaveBlockHotkey(bool block)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("BlockHotkey", block ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        internal static void SaveHotkey(int virtualKey)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("Hotkey", virtualKey, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private void ApplyHotkey(int virtualKey, bool block)
        {
            if (_hotkeyWatcher == null)
            {
                _hotkeyWatcher = new HotkeyWatcher();
                _hotkeyWatcher.Pressed += delegate
                {
                    SynchronizationContext context = _context;
                    if (context != null) context.Post(delegate { OnHotkeyPressed(); }, null);
                    else OnHotkeyPressed();
                };
            }
            _hotkeyWatcher.Block = block;
            bool ok = _hotkeyWatcher.Install(virtualKey);
            Program.Log("hotkey vk=" + virtualKey + " hook=" + ok + " block=" + block);
        }

        public static bool ReadAutoStart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("AutoStart");
                    if (value is int) return (int)value != 0;
                }
            }
            catch { }
            return true;
        }

        internal static void SaveAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("AutoStart", enabled ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static bool ReadNotify()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("Notify");
                    if (value is int) return (int)value != 0;
                }
            }
            catch { }
            return true;
        }

        internal static void SaveNotify(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("Notify", enabled ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static int ReadNotifySeconds()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("NotifySeconds");
                    if (value is int && (int)value >= 1 && (int)value <= 10) return (int)value;
                }
            }
            catch { }
            return 4;
        }

        public static bool ReadDoubleOff()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("DoubleOff");
                    if (value is int) return (int)value != 0;
                }
            }
            catch { }
            return true;
        }

        internal static void SaveDoubleOff(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("DoubleOff", enabled ? 1 : 0, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static int ReadDoubleMs()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("DoubleMs");
                    if (value is int && (int)value >= SettingsForm.DoubleMin && (int)value <= SettingsForm.DoubleMax) return (int)value;
                }
            }
            catch { }
            return SettingsForm.DoubleDefault;
        }

        internal static void SaveDoubleMs(int ms)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("DoubleMs", ms, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        internal static void SaveNotifySeconds(int seconds)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("NotifySeconds", seconds, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public static void ApplyAutoStart(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue("BtModeSwitch", "\"" + Application.ExecutablePath + "\"");
                    else key.DeleteValue("BtModeSwitch", false);
                }
            }
            catch { }
        }

        public static int ReadStartup()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("Startup");
                    if (value is int && (int)value >= 0 && (int)value <= 3) return (int)value;
                }
            }
            catch { }
            return 3;
        }

        internal static void SaveStartup(int startup)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("Startup", startup, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        public void SetMode(Mode mode) { SetMode(mode, true); }

        private void HandleSingleClick()
        {
            if (!_hasMode || _mode == Mode.Off) { SetMode(_lastOn); return; }
            SetMode(_mode == Mode.Mic ? Mode.Listen : Mode.Mic);
        }

        // Одиночное нажатие выполняется с задержкой на окно двойного нажатия,
        // поэтому двойное нажатие успевает перехватить его и отключить устройство.
        private void OnHotkeyPressed()
        {
            if (!_doubleOff)
            {
                HandleSingleClick();
                return;
            }
            if (_hotkeyPending)
            {
                _hotkeyTimer.Stop();
                _hotkeyPending = false;
                Program.Log("hotkey: double press -> " + Mode.Off);
                SetMode(Mode.Off);
                return;
            }
            _hotkeyPending = true;
            _hotkeyTimer.Interval = _doubleMs;
            _hotkeyTimer.Stop();
            _hotkeyTimer.Start();
        }

        private void FlushHotkey()
        {
            _hotkeyTimer.Stop();
            if (!_hotkeyPending) return;
            _hotkeyPending = false;
            HandleSingleClick();
        }

        private void SetMode(Mode mode, bool notify)
        {
            _mode = mode;
            _hasMode = true;
            if (mode != Mode.Off) _lastOn = mode;
            SaveMode(mode);
            Program.Log("mode=" + mode + ", startup=" + ReadStartup() + ", autostart=" + ReadAutoStart());

            bool mic = mode == Mode.Mic;
            bool listen = mode == Mode.Listen;
            _micItem.Checked = mic;
            _listenItem.Checked = listen;
            _offItem.Checked = mode == Mode.Off;
            _icon.Icon = mic ? _micIcon : listen ? _listenIcon : _offIcon;
            _icon.Text = "BRLINK: " + Title(mode);


            SynchronizationContext context = _context;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string note = Switch.Apply(mode);
                if (!notify) return;
                if (context != null)
                {
                    context.Post(delegate { ShowResult(mode, note); }, null);
                }
            });
        }

        private static string Title(Mode mode)
        {
            if (mode == Mode.Mic) return "Режим записи";
            if (mode == Mode.Listen) return "Режим прослушивания";
            return "Устройство отключено";
        }

        private void ShowResult(Mode mode, string note)
        {
            Program.Log("result " + mode + ": " + note);
            _icon.Text = "BRLINK: " + Title(mode);
            Notify(Title(mode));
        }

        private void Notify(string text)
        {
            if (!_notifyEnabled) return;
            Notice.Popup(text, _notifySeconds);
        }


        private static int? ReadSavedMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BtModeSwitch"))
                {
                    object value = key == null ? null : key.GetValue("Mode");
                    if (value is int && (int)value >= 0 && (int)value <= 2) return (int)value;
                }
            }
            catch { }
            return null;
        }

        private static void SaveMode(Mode mode)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BtModeSwitch"))
                {
                    if (key != null) key.SetValue("Mode", (int)mode, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static Icon MakeIcon(bool mic)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Color color = mic ? Color.FromArgb(214, 64, 64) : Color.FromArgb(46, 160, 67);
                    using (var brush = new SolidBrush(color))
                    using (var pen = new Pen(color, 2.4f))
                    {
                        if (mic)
                        {
                            var path = new GraphicsPath();
                            path.AddArc(11, 2, 10, 12, 180, 180);
                            path.AddArc(11, 8, 10, 12, 0, 180);
                            path.CloseFigure();
                            g.FillPath(brush, path);
                            g.DrawArc(pen, 6, 9, 20, 17, 0, 180);
                            g.DrawLine(pen, 16, 26, 16, 29);
                            g.DrawLine(pen, 11, 29, 21, 29);
                        }
                        else
                        {
                            g.FillPolygon(brush, new Point[] {
                                new Point(4, 12), new Point(11, 12), new Point(19, 5),
                                new Point(19, 27), new Point(11, 20), new Point(4, 20) });
                            g.DrawArc(pen, 18, 8, 12, 16, -55, 110);
                            g.DrawArc(pen, 22, 5, 16, 22, -55, 110);
                        }
                    }
                }
                IntPtr handle = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        // Серая иконка «отключено»: динамик с перечёркиванием (разрыв делается
        // прозрачным штрихом, иначе на 16x16 штрих сливался с корпусом).
        private static Icon MakeOffIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    Color color = Color.FromArgb(120, 120, 120);
                    using (var brush = new SolidBrush(color))
                    {
                        g.FillPolygon(brush, new Point[] {
                            new Point(4, 12), new Point(11, 12), new Point(19, 5),
                            new Point(19, 27), new Point(11, 20), new Point(4, 20) });
                    }
                    using (var slash = new GraphicsPath())
                    {
                        slash.AddLine(3, 29, 29, 3);
                        using (var gap = new Pen(Color.Transparent, 5.5f))
                        {
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawPath(gap, slash);
                            g.CompositingMode = CompositingMode.SourceOver;
                        }
                        using (var pen = new Pen(color, 2.4f)) g.DrawPath(pen, slash);
                    }
                }
                IntPtr handle = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        public void Dispose()
        {
            if (_hotkeyWatcher != null)
            {
                _hotkeyWatcher.Remove();
                _hotkeyWatcher = null;
            }
            _icon.Visible = false;
            _icon.Dispose();
            _micIcon.Dispose();
            _listenIcon.Dispose();
            _offIcon.Dispose();
        }
    }

    internal static class Program
    {
        internal static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BtModeSwitch.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                Run(args);
            }
            catch (Exception error)
            {
                Log("FATAL " + error);
            }
        }

        private static void Run(string[] args)
        {
            if (args.Length > 0)
            {
                string arg = args[0].ToLowerInvariant();
                if (arg == "--mic" || arg == "--listen" || arg == "--off")
                {
                    string note = Switch.Apply(arg == "--mic" ? Mode.Mic : arg == "--listen" ? Mode.Listen : Mode.Off);
                    Log(arg + " -> " + note + " (window=" + (BtMgrUi.FindWindow() != IntPtr.Zero) + ", idle=" + BtMgrUi.DeviceIdle() + ")");
                    return;
                }
                if (arg == "--settings")
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (var form = new SettingsForm(TrayApp.ReadStartup(), TrayApp.ReadAutoStart(), TrayApp.ReadHotkey(), TrayApp.ReadBlockHotkey(), TrayApp.ReadNotify(), TrayApp.ReadNotifySeconds(), TrayApp.ReadDoubleOff(), TrayApp.ReadDoubleMs()))
                    {
                        if (form.ShowDialog() == DialogResult.OK)
                        {
                            TrayApp.SaveStartup(form.Startup);
                            TrayApp.SaveAutoStart(form.AutoStart);
                            TrayApp.ApplyAutoStart(form.AutoStart);
                            TrayApp.SaveHotkey(form.Hotkey);
                            TrayApp.SaveBlockHotkey(form.BlockHotkey);
                            TrayApp.SaveNotify(form.Notify);
                            TrayApp.SaveNotifySeconds(form.NotifySeconds);
                            TrayApp.SaveDoubleOff(form.DoubleOff);
                            TrayApp.SaveDoubleMs(form.DoubleMs);
                        }
                    }
                    return;
                }
                if (arg == "--status")
                {
                    try
                    {
                        var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput());
                        stdout.AutoFlush = true;
                        stdout.Write(Switch.Describe());
                    }
                    catch { }
                    return;
                }
            }

            bool created;
            using (var mutex = new Mutex(true, "BtModeSwitch_SingleInstance", out created))
            {
                if (!created)
                {
                    Log("already running");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                {
                    app.Start();
                    Application.Run();
                }
            }
        }
    }
}