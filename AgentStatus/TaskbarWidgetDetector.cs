// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.CmdPal.Common.Helpers;

/// <summary>
/// Detects Windows 11 inline widget content and other system elements that occupy
/// space on the taskbar between the app buttons and the notification area.
/// Uses UI Automation to find XAML-rendered elements that have no separate HWND.
/// </summary>
public sealed class TaskbarWidgetDetector : IDisposable
{
    private IUIAutomation? _automation;
    private IUIAutomationCondition? _trueCondition;
    private bool _disposed;

    // Ignore very small elements (separators, borders, etc.)
    private const int MinElementWidth = 20;

    // Maximum UIA tree depth to traverse (avoids performance issues)
    private const int MaxDepth = 3;

    /// <summary>
    /// Scans the taskbar for system elements (widgets, search highlights, etc.)
    /// in the gap between app buttons and the notification area, and returns the
    /// effective content bounds.
    /// </summary>
    /// <param name="taskButtonsRightEdge">Screen-pixel X of the right edge of the last taskbar app button.</param>
    /// <param name="trayLeftEdge">Screen-pixel X of the left edge of TrayNotifyWnd.</param>
    /// <returns>
    /// (leftBound, rightBound) in screen pixels defining the free zone for content.
    /// leftBound: right edge of rightmost system element on the left side of the gap.
    /// rightBound: left edge of leftmost system element on the right side of the gap.
    /// If no extra elements are found, returns (taskButtonsRightEdge, trayLeftEdge).
    /// </returns>
    public (int leftBound, int rightBound) GetEffectiveContentBounds(
        int taskButtonsRightEdge, int trayLeftEdge)
    {
        if (_disposed || taskButtonsRightEdge >= trayLeftEdge)
        {
            return (taskButtonsRightEdge, trayLeftEdge);
        }

        try
        {
            HWND shellTray = PInvoke.FindWindow("Shell_TrayWnd", null);
            if (shellTray.IsNull)
            {
                return (taskButtonsRightEdge, trayLeftEdge);
            }

            PInvoke.GetWindowRect(shellTray, out RECT taskbarRect);
            int taskbarWidth = taskbarRect.Width;

            // Find the XAML composition bridge that renders the modern Win11 taskbar.
            // Widget inline content lives inside this tree with no separate HWND.
            HWND xamlBridge = PInvoke.FindWindowEx(
                shellTray, HWND.Null,
                "Windows.UI.Composition.DesktopWindowContentBridge", null);
            if (xamlBridge.IsNull)
            {
                return (taskButtonsRightEdge, trayLeftEdge);
            }

            EnsureAutomation();

            IUIAutomationElement bridgeElement = _automation!.ElementFromHandle(xamlBridge);
            if (bridgeElement == null)
            {
                return (taskButtonsRightEdge, trayLeftEdge);
            }

            try
            {
                int leftBound = taskButtonsRightEdge;
                int rightBound = trayLeftEdge;
                int gapMidpoint = (taskButtonsRightEdge + trayLeftEdge) / 2;

                ScanForOccupiedElements(
                    bridgeElement,
                    taskButtonsRightEdge,
                    trayLeftEdge,
                    taskbarWidth,
                    gapMidpoint,
                    ref leftBound,
                    ref rightBound,
                    depth: 0);

                return (leftBound, rightBound);
            }
            finally
            {
                Marshal.ReleaseComObject(bridgeElement);
            }
        }
        catch (Exception)
        {
            // Gracefully degrade — if widget detection fails for any reason,
            // fall back to the original behavior (no widget reservation).
            return (taskButtonsRightEdge, trayLeftEdge);
        }
    }

    /// <summary>
    /// Recursively scans UIA children for elements occupying space in the gap zone.
    /// Full-width elements are treated as containers and their children are scanned.
    /// </summary>
    private void ScanForOccupiedElements(
        IUIAutomationElement parent,
        int gapLeft,
        int gapRight,
        int taskbarWidth,
        int gapMidpoint,
        ref int leftBound,
        ref int rightBound,
        int depth)
    {
        if (depth >= MaxDepth)
        {
            return;
        }

        IUIAutomationElementArray? children;
        try
        {
            children = parent.FindAll(TreeScope.TreeScope_Children, _trueCondition!);
        }
        catch (COMException)
        {
            return;
        }

        if (children == null)
        {
            return;
        }

        int count;
        try
        {
            count = children.Length;
        }
        catch
        {
            Marshal.ReleaseComObject(children);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            IUIAutomationElement? child = null;
            try
            {
                child = children.GetElement(i);
                if (child == null)
                {
                    continue;
                }

                (int x, int w) = GetBounds(child);
                if (w <= 0)
                {
                    continue;
                }

                int right = x + w;

                // Full-width element is a container — recurse into it
                if (w >= taskbarWidth - MinElementWidth)
                {
                    ScanForOccupiedElements(
                        child, gapLeft, gapRight, taskbarWidth,
                        gapMidpoint, ref leftBound, ref rightBound, depth + 1);
                    continue;
                }

                // Skip elements entirely outside the gap zone
                if (right <= gapLeft || x >= gapRight)
                {
                    continue;
                }

                // Skip very small elements (separators, borders)
                if (w < MinElementWidth)
                {
                    continue;
                }

                // Classify as left-side or right-side element based on center position
                int center = x + (w / 2);
                if (center < gapMidpoint)
                {
                    // Left-side element: push left boundary right
                    int effectiveRight = Math.Min(right, gapRight);
                    if (effectiveRight > leftBound)
                    {
                        leftBound = effectiveRight;
                    }
                }
                else
                {
                    // Right-side element: push right boundary left
                    int effectiveLeft = Math.Max(x, gapLeft);
                    if (effectiveLeft < rightBound)
                    {
                        rightBound = effectiveLeft;
                    }
                }
            }
            catch (COMException)
            {
                // Skip elements that can't be read
            }
            finally
            {
                if (child != null)
                {
                    Marshal.ReleaseComObject(child);
                }
            }
        }

        Marshal.ReleaseComObject(children);
    }

    private static (int x, int width) GetBounds(IUIAutomationElement element)
    {
        try
        {
            object rect = element.GetCurrentPropertyValue(
                UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
            if (rect is double[] r && r.Length >= 4)
            {
                return ((int)r[0], (int)r[2]);
            }
        }
        catch (COMException)
        {
        }

        return (0, 0);
    }

    private void EnsureAutomation()
    {
        if (_automation == null)
        {
            _automation = (IUIAutomation)new CUIAutomation();
            _trueCondition = _automation.CreateTrueCondition();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            if (_trueCondition != null)
            {
                Marshal.ReleaseComObject(_trueCondition);
                _trueCondition = null;
            }

            if (_automation != null)
            {
                Marshal.ReleaseComObject(_automation);
                _automation = null;
            }

            _disposed = true;
        }
    }
}
