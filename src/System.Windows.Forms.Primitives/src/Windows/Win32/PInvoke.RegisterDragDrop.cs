﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.Win32.System.Ole;

namespace Windows.Win32;

internal static partial class PInvoke
{
    public static unsafe HRESULT RegisterDragDrop<T>(T hwnd, IDropTarget.Interface pDropTarget)
        where T : IHandle<HWND>
    {
        using var dropTarget = ComHelpers.TryGetComScope<IDropTarget>(pDropTarget, out HRESULT hr);
        if (hr.Failed)
        {
            return hr;
        }

        // RegisterDragDrop calls AddRef()
        hr = RegisterDragDrop(hwnd.Handle, dropTarget);
        GC.KeepAlive(hwnd.Wrapper);
        return hr;
    }
}
