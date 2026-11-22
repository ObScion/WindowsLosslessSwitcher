// Minimal Core Audio interop: device enumeration, default-device notifications,
// exclusive-mode format probing, shared-mode device format get/set (IPolicyConfig), peak meter.
// Windows counterpart of SimplyCoreAudio as used by the macOS LosslessSwitcher.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LosslessSwitcher
{
    public enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    public enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
        public PropertyKey(Guid g, int p) { FormatId = g; PropertyId = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    // Undocumented but long-stable (Win7+) interface used by every "set default format" tool.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, [MarshalAs(UnmanagedType.Bool)] bool defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string id, IntPtr endpointFormat, IntPtr mixFormat);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigCom { }

    sealed class DeviceInfo
    {
        public string Id;
        public string Name;
    }

    /// <summary>A PCM device format (the equivalent of an AudioStreamBasicDescription).</summary>
    sealed class DevFormat
    {
        static readonly Guid SubTypePcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid SubTypeFloat = new Guid("00000003-0000-0010-8000-00AA00389B71");

        public int Rate;
        public int Bits;       // valid bits per sample
        public int Container;  // container bits per sample
        public int Channels;
        public uint Mask;

        public static DevFormat FromPtr(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int tag = (ushort)Marshal.ReadInt16(p, 0);
            var f = new DevFormat
            {
                Channels = (ushort)Marshal.ReadInt16(p, 2),
                Rate = Marshal.ReadInt32(p, 4),
                Container = (ushort)Marshal.ReadInt16(p, 14),
            };
            f.Bits = f.Container;
            int cb = (ushort)Marshal.ReadInt16(p, 16);
            if (tag == 0xFFFE && cb >= 22)
            {
                int valid = (ushort)Marshal.ReadInt16(p, 18);
                if (valid > 0) f.Bits = valid;
                f.Mask = (uint)Marshal.ReadInt32(p, 20);
            }
            return f;
        }

        /// <summary>WAVEFORMATEXTENSIBLE (integer PCM), allocated with AllocHGlobal.</summary>
        public IntPtr ToHGlobal()
        {
            var p = Marshal.AllocHGlobal(40);
            int blockAlign = Channels * Container / 8;
            Marshal.WriteInt16(p, 0, unchecked((short)0xFFFE));
            Marshal.WriteInt16(p, 2, (short)Channels);
            Marshal.WriteInt32(p, 4, Rate);
            Marshal.WriteInt32(p, 8, Rate * blockAlign);
            Marshal.WriteInt16(p, 12, (short)blockAlign);
            Marshal.WriteInt16(p, 14, (short)Container);
            Marshal.WriteInt16(p, 16, 22);
            Marshal.WriteInt16(p, 18, (short)Bits);
            Marshal.WriteInt32(p, 20, unchecked((int)Mask));
            Marshal.Copy(SubTypePcm.ToByteArray(), 0, p + 24, 16);
            return p;
        }

        public override string ToString() { return Bits + "-bit/" + Rate + " Hz (" + Container + "c, " + Channels + "ch)"; }
    }

    static class CoreAudio
    {
        const int DeviceStateActive = 1;
        const int ClsctxAll = 23;
        static Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        static Guid IID_IAudioMeterInformation = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
        static PropertyKey PKEY_Device_FriendlyName = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        public static readonly int[] StandardRates = { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000 };
        static readonly int[][] Shapes = { new[] { 16, 16 }, new[] { 24, 24 }, new[] { 24, 32 }, new[] { 32, 32 } };

        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

        public static IMMDeviceEnumerator NewEnumerator() { return (IMMDeviceEnumerator)new MMDeviceEnumeratorCom(); }

        public static List<DeviceInfo> RenderDevices()
        {
            var list = new List<DeviceInfo>();
            IMMDeviceCollection col;
            if (NewEnumerator().EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out col) != 0) return list;
            int n; col.GetCount(out n);
            for (int i = 0; i < n; i++)
            {
                IMMDevice d;
                if (col.Item(i, out d) != 0) continue;
                string id; d.GetId(out id);
                list.Add(new DeviceInfo { Id = id, Name = FriendlyName(d) ?? id });
            }
            return list;
        }

        public static string DefaultRenderId()
        {
            IMMDevice d;
            if (NewEnumerator().GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out d) != 0) return null;
            string id; d.GetId(out id);
            return id;
        }

        static IMMDevice GetDevice(string id)
        {
            IMMDevice d;
            return NewEnumerator().GetDevice(id, out d) == 0 ? d : null;
        }

        static string FriendlyName(IMMDevice d)
        {
            IPropertyStore store;
            if (d.OpenPropertyStore(0, out store) != 0) return null;
            PropVariant v;
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out v) != 0) return null;
            try { return v.vt == 31 ? Marshal.PtrToStringUni(v.p) : null; }
            finally { PropVariantClear(ref v); }
        }

        /// <summary>Current shared-mode device format (Sound control panel "Default Format").</summary>
        public static DevFormat GetDeviceFormat(string id)
        {
            if (id == null) return null;
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigCom();
                IntPtr p;
                if (pc.GetDeviceFormat(id, false, out p) != 0 || p == IntPtr.Zero) return null;
                try { return DevFormat.FromPtr(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch (Exception ex) { Log.Write("GetDeviceFormat failed: " + ex.Message); return null; }
        }

        public static bool SetDeviceFormat(string id, DevFormat f)
        {
            var p = f.ToHGlobal();
            try
            {
                var pc = (IPolicyConfig)new PolicyConfigCom();
                int hr = pc.SetDeviceFormat(id, p, p);
                if (hr != 0) Log.Write("SetDeviceFormat " + f + " hr=0x" + hr.ToString("X8"));
                return hr == 0;
            }
            catch (Exception ex) { Log.Write("SetDeviceFormat failed: " + ex.Message); return false; }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>
        /// Formats the hardware accepts, found by exclusive-mode IsFormatSupported probing
        /// (the equivalent of availablePhysicalFormats on macOS).
        /// </summary>
        public static List<DevFormat> ProbeFormats(string id, DevFormat current)
        {
            var result = new List<DevFormat>();
            var d = GetDevice(id);
            if (d == null) return result;
            object o;
            var iid = IID_IAudioClient;
            if (d.Activate(ref iid, ClsctxAll, IntPtr.Zero, out o) != 0) return result;
            var client = (IAudioClient)o;
            try
            {
                int ch = current != null && current.Channels > 0 ? current.Channels : 2;
                uint mask = current != null && current.Mask != 0 ? current.Mask : (ch == 1 ? 4u : ch == 2 ? 3u : 0u);
                var masks = mask == 0 ? new uint[] { 0 } : new uint[] { mask, 0 };
                foreach (var rate in StandardRates)
                {
                    foreach (var shape in Shapes)
                    {
                        foreach (var m in masks)
                        {
                            var f = new DevFormat { Rate = rate, Bits = shape[0], Container = shape[1], Channels = ch, Mask = m };
                            var p = f.ToHGlobal();
                            int hr;
                            try { hr = client.IsFormatSupported(1 /* exclusive */, p, IntPtr.Zero); }
                            finally { Marshal.FreeHGlobal(p); }
                            if (hr == 0) { result.Add(f); break; }
                        }
                    }
                }
            }
            finally { Marshal.ReleaseComObject(client); }
            return result;
        }

        public static float Peak(string id)
        {
            try
            {
                var d = GetDevice(id);
                if (d == null) return -1;
                object o;
                var iid = IID_IAudioMeterInformation;
                if (d.Activate(ref iid, ClsctxAll, IntPtr.Zero, out o) != 0) return -1;
                var meter = (IAudioMeterInformation)o;
                float v;
                int hr = meter.GetPeakValue(out v);
                Marshal.ReleaseComObject(meter);
                return hr == 0 ? v : -1;
            }
            catch { return -1; }
        }
    }

    /// <summary>Forwards Core Audio device notifications (deviceListChanged / defaultOutputDeviceChanged).</summary>
    public sealed class DeviceNotifier : IMMNotificationClient
    {
        readonly Action<bool> _changed; // arg: true when the default device changed
        readonly IMMDeviceEnumerator _enumerator;

        public DeviceNotifier(Action<bool> changed)
        {
            _changed = changed;
            _enumerator = CoreAudio.NewEnumerator();
            _enumerator.RegisterEndpointNotificationCallback(this);
        }

        public void Dispose() { try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { } }

        public void OnDeviceStateChanged(string id, int newState) { _changed(false); }
        public void OnDeviceAdded(string id) { _changed(false); }
        public void OnDeviceRemoved(string id) { _changed(false); }
        public void OnDefaultDeviceChanged(EDataFlow flow, ERole role, string id)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia) _changed(true);
        }
        public void OnPropertyValueChanged(string id, PropertyKey key) { }
    }
}
