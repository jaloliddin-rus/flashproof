using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlashProof.Windows;

/// <summary>
/// Watches the handles of a volume under test for Windows asking to remove the device or lock the volume: "Safely
/// remove", or another program formatting or checking it (design §5.4). Then <c>onQueryRemove</c> must stop all I/O,
/// and the handles are closed so the removal can go ahead. Dispose it after the test, never inside the callback:
/// unregistering waits for running callbacks, so it would deadlock.
/// </summary>
internal sealed unsafe class RemovalWatch : IDisposable
{
    readonly Action onQueryRemove;
    readonly Action closeHandles;
    readonly List<nint> registrations = [];
    GCHandle self;
    int fired;

    /// <param name="onQueryRemove">Stops the test and waits (bounded) for its I/O to end.</param>
    /// <param name="closeHandles">Closes every handle to the device that outlives the test.</param>
    public RemovalWatch(IEnumerable<SafeFileHandle> handles, Action onQueryRemove, Action closeHandles)
    {
        this.onQueryRemove = onQueryRemove;
        this.closeHandles = closeHandles;
        self = GCHandle.Alloc(this);
        try
        {
            foreach (var handle in handles)
            {
                var filter = new Native.CmNotifyFilter
                {
                    Size = (uint)sizeof(Native.CmNotifyFilter),
                    FilterType = Native.CmNotifyFilterTypeDeviceHandle,
                    Handle = handle.DangerousGetHandle(),
                };
                int result = Native.CM_Register_Notification(filter, GCHandle.ToIntPtr(self), &OnNotify, out nint registration);
                if (result != Native.CrSuccess)
                    throw new InvalidOperationException($"CM_Register_Notification failed: CONFIGRET {result}.");
                registrations.Add(registration);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (nint registration in registrations)
            Native.CM_Unregister_Notification(registration);
        registrations.Clear();
        if (self.IsAllocated)
            self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnNotify(nint notification, nint context, int action, nint data, uint size)
    {
        if (action == Native.CmNotifyActionDeviceQueryRemove && GCHandle.FromIntPtr(context).Target is RemovalWatch watch
            && Interlocked.Exchange(ref watch.fired, 1) == 0)
        {
            try
            {
                watch.onQueryRemove();
            }
            finally
            {
                watch.closeHandles();
            }
        }
        return 0; // ERROR_SUCCESS: don't veto
    }
}

/// <summary>Calls back when the PC goes to sleep or wakes up (design §5.4). Needs no window and no admin rights.</summary>
internal sealed unsafe class PowerWatch : IDisposable
{
    readonly Action onSleepOrWake;
    readonly Native.DeviceNotifySubscribeParameters* parameters;
    GCHandle self;
    nint registration;

    public PowerWatch(Action onSleepOrWake)
    {
        this.onSleepOrWake = onSleepOrWake;
        self = GCHandle.Alloc(this);
        parameters = (Native.DeviceNotifySubscribeParameters*)NativeMemory.Alloc((nuint)sizeof(Native.DeviceNotifySubscribeParameters));
        parameters->Callback = &OnPower;
        parameters->Context = GCHandle.ToIntPtr(self);
        uint result = Native.PowerRegisterSuspendResumeNotification(Native.DeviceNotifyCallback, parameters, out registration);
        if (result != 0)
        {
            Dispose();
            throw new InvalidOperationException($"PowerRegisterSuspendResumeNotification failed: error {result}.");
        }
    }

    public void Dispose()
    {
        if (!self.IsAllocated)
            return;
        if (registration != 0)
            Native.PowerUnregisterSuspendResumeNotification(registration);
        registration = 0;
        self.Free();
        NativeMemory.Free(parameters);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static uint OnPower(nint context, uint type, nint setting)
    {
        if (type is Native.PbtApmSuspend or Native.PbtApmResumeSuspend or Native.PbtApmResumeAutomatic
            && GCHandle.FromIntPtr(context).Target is PowerWatch watch)
            watch.onSleepOrWake();
        return 0;
    }
}
