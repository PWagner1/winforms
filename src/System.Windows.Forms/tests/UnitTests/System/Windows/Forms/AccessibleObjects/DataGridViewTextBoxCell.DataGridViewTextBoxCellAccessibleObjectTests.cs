﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Windows.Win32.UI.Accessibility;

namespace System.Windows.Forms.Tests.AccessibleObjects;

public class DataGridViewTextBoxCell_DataGridViewTextBoxCellAccessibleObject
{
    [WinFormsFact]
    public void DataGridViewTextBoxCellAccessibleObject_ControlType_IsDataItem()
    {
        using var cell = new DataGridViewTextBoxCell();
        AccessibleObject accessibleObject = cell.AccessibilityObject;

        object actual = accessibleObject.GetPropertyValue(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);

        Assert.Equal(UIA_CONTROLTYPE_ID.UIA_DataItemControlTypeId, actual);
    }
}