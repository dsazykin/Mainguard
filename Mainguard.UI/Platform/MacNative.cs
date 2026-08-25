using System;
using System.Runtime.InteropServices;

namespace Mainguard.UI.Platform;

/// <summary>
/// The one place managed code talks to AppKit directly (macOS only; every member is a safe no-op
/// elsewhere and never throws). Used for the small pieces Avalonia does not surface: Notification
/// Center banners and the Dock badge. Kept to raw <c>objc_msgSend</c> so no binding package joins
/// the supply chain; each feature detects its classes at runtime and reports false when the OS has
/// removed them — callers keep their cross-platform fallback.
/// </summary>
public static class MacNative
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport(CoreFoundation)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string str, uint encoding);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);

    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    private static IntPtr NSString(string value) =>
        CFStringCreateWithCString(IntPtr.Zero, value, Utf8); // toll-free bridged to NSString

    /// <summary>
    /// Posts a Notification Center banner attributed to the running app (which is only a real
    /// identity inside the .app bundle — see build/macos-bundle). False when unavailable
    /// (non-macOS, the legacy notification class removed, any interop failure) so the caller
    /// keeps its in-window fallback. Uses NSUserNotification: deprecated but delegate-free —
    /// the UNUserNotificationCenter upgrade (authorization prompt + action buttons) is a
    /// follow-up that needs a real delegate object.
    /// </summary>
    public static bool TryPostNotification(string title, string body)
    {
        if (!OperatingSystem.IsMacOS()) return false;

        try
        {
            var notificationClass = objc_getClass("NSUserNotification");
            var centerClass = objc_getClass("NSUserNotificationCenter");
            if (notificationClass == IntPtr.Zero || centerClass == IntPtr.Zero) return false;

            var center = MsgSend(centerClass, sel_registerName("defaultUserNotificationCenter"));
            if (center == IntPtr.Zero) return false;

            var notification = MsgSend(MsgSend(notificationClass, sel_registerName("alloc")), sel_registerName("init"));
            if (notification == IntPtr.Zero) return false;

            var titleString = NSString(title);
            var bodyString = NSString(body);
            try
            {
                MsgSend(notification, sel_registerName("setTitle:"), titleString);
                MsgSend(notification, sel_registerName("setInformativeText:"), bodyString);
                MsgSend(center, sel_registerName("deliverNotification:"), notification);
            }
            finally
            {
                CFRelease(titleString);
                CFRelease(bodyString);
                MsgSend(notification, sel_registerName("release")); // the center retains its copy
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sets (or clears, with null/empty) the Dock icon badge. AppKit-thread-sensitive —
    /// call from the UI thread. No-op off macOS or on any failure.</summary>
    public static void SetDockBadge(string? label)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var app = MsgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (app == IntPtr.Zero) return;
            var dockTile = MsgSend(app, sel_registerName("dockTile"));
            if (dockTile == IntPtr.Zero) return;

            if (string.IsNullOrEmpty(label))
            {
                MsgSend(dockTile, sel_registerName("setBadgeLabel:"), IntPtr.Zero);
            }
            else
            {
                var labelString = NSString(label);
                try { MsgSend(dockTile, sel_registerName("setBadgeLabel:"), labelString); }
                finally { CFRelease(labelString); }
            }
        }
        catch
        {
            // Cosmetic — never let a badge take the app down.
        }
    }
}
